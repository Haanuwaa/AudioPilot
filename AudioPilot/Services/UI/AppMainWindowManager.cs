using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI
{
    internal sealed partial class AppMainWindowManager : IAppMainWindowManager, IAsyncDisposable
    {
        private readonly Application _application;
        private readonly Dispatcher _dispatcher;
        private readonly Logger _logger;
        private readonly IAppDialogService _dialogs;
        private readonly MainWindowPlacementService? _placement;
        private readonly SemaphoreSlim _transitionGate = new(1, 1);
        private Func<MainWindow>? _windowFactory;
        private volatile MainWindow? _window;
        private Task<bool>? _activeShowTask;
        private Task? _failureDialogTask;
        private bool _activeShowDetached;
        private bool _revealInFlight;
        private readonly HashSet<(CancellationToken Token, MainWindowHotkeyPresentation? Hotkey)> _showRequests = [];
        private MainWindowOpenTarget _pendingOpenTarget;
        private volatile bool _desiredVisible;
        private volatile bool _isVisible;
        private volatile bool _isTaskbarMinimized;
        private bool _firstPresentationCompleted;
        private int _shutdownStarted;
        private int _closeStarted;
        private int _disposeStarted;

        internal Action? BeforeBackgroundShowDispatchForTests { get; set; }
        internal Func<Task<bool>>? ShowCoreOverrideForTests { get; set; }
        internal MainWindowOpenTarget PendingOpenTargetForTests => _pendingOpenTarget;
        internal bool DesiredVisibleForTests => _desiredVisible;

        internal AppMainWindowManager(
            Application application,
            IAppDialogService dialogs,
            Logger? logger = null,
            MainWindowPlacementService? placement = null)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _dispatcher = application.Dispatcher;
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _logger = logger ?? Logger.Instance;
            _placement = placement;
        }

        public bool IsCreated => _window != null;

        public bool IsVisible => _isVisible && !_isTaskbarMinimized;

        public bool CanRetryHotkeyShow => IsVisible && _window is { } window && WindowFirstPresentationHelper.CanRetryShortcutShow(window);

        public MainWindow? CurrentWindow => _window;

        internal void SetWindowFactory(Func<MainWindow> windowFactory)
        {
            ArgumentNullException.ThrowIfNull(windowFactory);
            if (_windowFactory != null)
            {
                throw new InvalidOperationException("The main-window factory has already been configured.");
            }

            _windowFactory = windowFactory;
        }

        public Task<bool> ShowAsync(
            MainWindowOpenTarget target = MainWindowOpenTarget.Default,
            MainWindowHotkeyPresentation? hotkey = null,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutdownStarted) != 0 || IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }

            if (_dispatcher.CheckAccess())
            {
                return WaitForCallerAsync(StartShowOnDispatcher(target, hotkey, cancellationToken), cancellationToken);
            }

            try
            {
                BeforeBackgroundShowDispatchForTests?.Invoke();
                Task<bool> sharedShowTask = _dispatcher.InvokeAsync(
                    () => StartShowOnDispatcher(target, hotkey, cancellationToken),
                    DispatcherPriority.Normal,
                    cancellationToken).Task.Unwrap();
                return WaitForCallerAsync(sharedShowTask, cancellationToken);
            }
            catch (InvalidOperationException) when (IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }
        }

        private Task<bool> StartShowOnDispatcher(MainWindowOpenTarget target, MainWindowHotkeyPresentation? hotkey, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutdownStarted) != 0 || IsDispatcherUnavailable()
                || hotkey?.CanPresent?.Invoke() == false)
            {
                return Task.FromResult(false);
            }

            _desiredVisible = true;
            _pendingOpenTarget = target;
            if (_activeShowTask is { IsCompleted: false })
            {
                _showRequests.Add((cancellationToken, hotkey));
                MainWindow? existingWindow = _window;
                existingWindow?.NavigateTo(target);
                return _activeShowTask;
            }

            _showRequests.Clear();
            _showRequests.Add((cancellationToken, hotkey));
            _activeShowDetached = false;
            Task<bool> showTask = ShowCoreOverrideForTests?.Invoke() ?? ShowCoreAsync();
            _activeShowTask = _activeShowDetached || showTask.IsCompleted ? null : showTask;
            return showTask;
        }

        private async Task<bool> ShowCoreAsync()
        {
            bool enteredTransitionGate = false;
            bool presentationFailed = false;
            try
            {
                await _transitionGate.WaitAsync();
                enteredTransitionGate = true;

                if (!CanReveal())
                {
                    _desiredVisible = false;
                    return false;
                }

                bool firstShow = _window == null || !_firstPresentationCompleted;
                if (firstShow && _placement != null) await _placement.LoadAsync();
                if (!CanReveal())
                {
                    _desiredVisible = false;
                    return false;
                }
                MainWindow window = _window ?? CreateWindow();
                MainWindowOpenTarget target = _pendingOpenTarget;
                window.NavigateTo(target);
                void RaiseForHotkey()
                {
                    if (!_desiredVisible || Volatile.Read(ref _shutdownStarted) != 0) return;
                    foreach (var (token, presentation) in _showRequests)
                    {
                        if (token.IsCancellationRequested || presentation is not { } hotkey) continue;
                        nint foreground = hotkey.GetRaiseForeground();
                        if (foreground == 0) continue;
                        WindowFirstPresentationHelper.TryRaiseForShortcut(window, _logger, TimeSpan.FromSeconds(3), expectedForeground: foreground);
                        return;
                    }
                }

                if (!firstShow && window.Visibility == Visibility.Visible && window.WindowState != WindowState.Minimized)
                {
                    _isTaskbarMinimized = false;
                    _isVisible = true;
                    WindowFirstPresentationHelper.Activate(window, RaiseForHotkey);
                    return true;
                }

                long started = Stopwatch.GetTimestamp();
                bool guardedShow = !_showRequests.Any(static request => !request.Token.IsCancellationRequested && request.Hotkey?.CanPresent == null);
                if (firstShow || guardedShow)
                {
                    WindowFirstPresentationHelper.Prepare(window, hideFromTaskbar: firstShow);
                }

                if (firstShow)
                {
                    window.WindowState = WindowState.Normal;
                }
                else if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = MainWindowPlacementService.ShouldRestoreMaximized(window)
                        ? WindowState.Maximized
                        : WindowState.Normal;
                }
                if (firstShow)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
                    window.ShowInTaskbar = true;
                    WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
                    _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true);
                }
                else
                {
                    if (!window.IsVisible && window.WindowState == WindowState.Normal)
                    {
                        _ = DialogWindowHelper.TryFitToWorkArea(window, null, center: false, margin: 0);
                    }
                    window.ShowActivated = !guardedShow;
                    window.ShowInTaskbar = true;
                    window.Opacity = guardedShow ? 0d : 1d;
                }

                _revealInFlight = true;
                Task<bool>? firstRevealTask = firstShow
                    ? WindowFirstPresentationHelper.RevealAsync(
                        window,
                        activate: true,
                        canReveal: CanReveal,
                        waitForFirstRender: true,
                        positionBeforeReveal: () => (_placement?.TryRestore(window) ?? false)
                            || DialogWindowHelper.TryFitToWorkArea(window, null, center: true, margin: 0),
                        onActivationDenied: RaiseForHotkey)
                    : null;
                double nativePreparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                window.Show();
                double showMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - nativePreparationMs;
                window.UpdateLayout();
                double layoutMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - nativePreparationMs - showMs;

                bool revealed = firstRevealTask != null
                    ? await firstRevealTask
                    : await WindowFirstPresentationHelper.RevealAsync(window, activate: true, canReveal: CanReveal,
                        revealOpacity: guardedShow ? 1d : null, onActivationDenied: RaiseForHotkey);
                double revealMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - nativePreparationMs - showMs - layoutMs;
                if (!revealed && CanReveal())
                {
                    throw new InvalidOperationException("The native main window did not complete presentation.");
                }

                // After activation the foreground may be AudioPilot itself.
                if (!revealed || !_desiredVisible || Volatile.Read(ref _shutdownStarted) != 0
                    || !_showRequests.Any(static request => !request.Token.IsCancellationRequested))
                {
                    if (!CanReveal()) _desiredVisible = false;
                    _isVisible = false;
                    if (firstShow)
                    {
                        HidePreparedWindowCore(window);
                    }
                    else
                    {
                        _placement?.Pause();
                        HideWindowCore(window);
                    }

                    WindowFirstPresentationHelper.WithdrawFirstPresentation(window);

                    return false;
                }

                window.NavigateTo(_pendingOpenTarget);
                window.ResetInteractionState();
                _firstPresentationCompleted = true;
                _isTaskbarMinimized = false;
                _isVisible = true;
                _placement?.Start(window);
                if (firstShow)
                {
                    _logger.Debug(
                        "AppMainWindowManager",
                        () => $"main-window-first-presentation | nativePreparationMs={nativePreparationMs:F1} showMs={showMs:F1} layoutMs={layoutMs:F1} revealMs={revealMs:F1}");
                }
                _logger.Info(
                    "AppMainWindowManager",
                    () => $"main-window-show-complete | firstShow={firstShow} target={_pendingOpenTarget} visible={window.IsVisible} active={window.IsActive} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                return true;
            }
            catch (Exception ex)
            {
                presentationFailed = true;
                CleanupPresentationFailure(ex);
            }
            finally
            {
                _revealInFlight = false;
                _showRequests.Clear();
                if (presentationFailed)
                {
                    _activeShowDetached = true;
                    _activeShowTask = null;
                }
                if (enteredTransitionGate)
                {
                    _transitionGate.Release();
                }
            }

            if (Volatile.Read(ref _shutdownStarted) == 0)
            {
                Task dialogTask = ShowPresentationFailureDialogAsync();
                _failureDialogTask = dialogTask;
                try
                {
                    await dialogTask;
                }
                finally
                {
                    if (ReferenceEquals(_failureDialogTask, dialogTask))
                    {
                        _failureDialogTask = null;
                    }
                }
            }

            return false;
        }

        private bool CanReveal() => _desiredVisible && Volatile.Read(ref _shutdownStarted) == 0
            && _showRequests.Any(static request => !request.Token.IsCancellationRequested && request.Hotkey?.CanPresent?.Invoke() != false);

        private MainWindow CreateWindow()
        {
            Func<MainWindow> factory = _windowFactory
                ?? throw new InvalidOperationException("The main-window factory is not configured.");
            MainWindow? candidate = null;
            long started = Stopwatch.GetTimestamp();

            try
            {
                WindowThemeResolver.EnsureApplicationThemeLoaded();
                candidate = factory();
                candidate.Closed += OnWindowClosed;
                candidate.StateChanged += OnWindowStateChanged;
                _application.MainWindow = candidate;
                _firstPresentationCompleted = false;
                _isTaskbarMinimized = false;
                _window = candidate;
                _logger.Info(
                    "AppMainWindowManager",
                    () => $"main-window-created | elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                return candidate;
            }
            catch
            {
                if (candidate != null)
                {
                    candidate.Closed -= OnWindowClosed;
                    candidate.StateChanged -= OnWindowStateChanged;
                    candidate.AllowCloseForRuntimeShutdown();
                    TryClosePartialWindow(candidate);
                }

                if (ReferenceEquals(_application.MainWindow, candidate))
                {
                    _application.MainWindow = null;
                }

                _window = null;
                throw;
            }
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            if (sender is not MainWindow window || !ReferenceEquals(_window, window))
            {
                return;
            }

            UpdateTaskbarMinimizedState(window.Visibility == Visibility.Visible, window.WindowState);
        }

        internal void UpdateTaskbarMinimizedState(bool isWindowVisible, WindowState state)
        {
            _isTaskbarMinimized = isWindowVisible && state == WindowState.Minimized;
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not MainWindow closedWindow || !ReferenceEquals(_window, closedWindow))
            {
                return;
            }

            closedWindow.Closed -= OnWindowClosed;
            closedWindow.StateChanged -= OnWindowStateChanged;
            _ = _placement?.StopAsync();
            _isTaskbarMinimized = false;
            _isVisible = false;
            _window = null;
            if (ReferenceEquals(_application.MainWindow, closedWindow))
            {
                _application.MainWindow = null;
            }
        }

        private void CleanupPresentationFailure(Exception ex)
        {
            MainWindow? failedWindow = _window;
            if (failedWindow != null)
            {
                failedWindow.Closed -= OnWindowClosed;
                failedWindow.StateChanged -= OnWindowStateChanged;
                _ = _placement?.StopAsync();
                failedWindow.AllowCloseForRuntimeShutdown();
                TryClosePartialWindow(failedWindow);
            }

            _isTaskbarMinimized = false;
            _isVisible = false;
            _window = null;
            if (ReferenceEquals(_application.MainWindow, failedWindow))
            {
                _application.MainWindow = null;
            }

            _logger.Error(
                "AppMainWindowManager",
                () => $"main-window-presentation-failed | error={ex.GetType().Name}",
                nameof(ShowCoreAsync),
                ex);
        }

        private async Task ShowPresentationFailureDialogAsync()
        {
            try
            {
                await _dialogs.ShowErrorAsync(
                    "The AudioPilot window could not be displayed. Audio switching and tray features remain available. Try opening the window again.",
                    "Window unavailable",
                    owner: null,
                    cancellationToken: _layoutCancellation.Token);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _shutdownStarted) != 0)
            {
            }
            catch (Exception dialogEx)
            {
                _logger.Warning(
                    "AppMainWindowManager",
                    () => $"main-window-failure-dialog-failed | error={dialogEx.GetType().Name}",
                    nameof(ShowPresentationFailureDialogAsync),
                    dialogEx);
            }
        }

        public bool Hide()
        {
            if (IsDispatcherUnavailable())
            {
                return false;
            }

            if (!_dispatcher.CheckAccess())
            {
                try
                {
                    return _dispatcher.Invoke(Hide);
                }
                catch (InvalidOperationException) when (IsDispatcherUnavailable())
                {
                    return false;
                }
            }

            MainWindow? window = _window;
            _desiredVisible = false;
            _isTaskbarMinimized = false;
            _isVisible = false;
            if (window == null)
            {
                return true;
            }

            // Let the in-flight reveal withdraw the window, or continue if another show arrives first.
            if (_revealInFlight) return true;

            _placement?.Pause();
            if (window.Visibility == Visibility.Visible) window.MarkPendingAutoScrollOnNextShow();
            HideWindowCore(window);
            return true;
        }

        private static void HideWindowCore(MainWindow window)
        {
            bool restoreMaximized = window.WindowState == WindowState.Minimized
                && MainWindowPlacementService.ShouldRestoreMaximized(window);
            window.ResetInteractionState();
            window.ShowActivated = false;
            window.Hide();
            window.Opacity = 0d;
            window.ShowInTaskbar = false;
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = restoreMaximized ? WindowState.Maximized : WindowState.Normal;
            }
        }

        private static void HidePreparedWindowCore(MainWindow window)
        {
            window.Hide();
            window.Opacity = 0d;
            window.ShowInTaskbar = false;
        }

        public Task<bool> HideAsync(CancellationToken cancellationToken = default)
        {
            if (_dispatcher.CheckAccess())
            {
                return Task.FromResult(Hide());
            }

            if (IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }

            try
            {
                return _dispatcher.InvokeAsync(Hide, DispatcherPriority.Normal, cancellationToken).Task;
            }
            catch (InvalidOperationException) when (IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }
        }

        public void BeginShutdown()
        {
            _desiredVisible = false;
            _isTaskbarMinimized = false;
            _isVisible = false;
            if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0) _layoutCancellation.Cancel();
        }

        internal async Task FlushPlacementForSessionEndingAsync(bool stop = false)
        {
            if (_placement == null || IsDispatcherUnavailable())
            {
                return;
            }

            try
            {
                Func<Task> action = stop ? _placement.StopAsync : _placement.FlushAsync;
                Task flush = _dispatcher.CheckAccess()
                    ? action()
                    : await _dispatcher.InvokeAsync(action, DispatcherPriority.Send);
                await flush.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                _logger.Debug("AppMainWindowManager", () => $"session-ending-placement-flush-skipped | error={ex.GetType().Name}", nameof(FlushPlacementForSessionEndingAsync));
            }
        }

        internal void AllowCloseForSessionEnding()
        {
            if (IsDispatcherUnavailable())
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                _window?.AllowCloseForRuntimeShutdown();
            }
            else
            {
                _ = _dispatcher.BeginInvoke(() => _window?.AllowCloseForRuntimeShutdown(), DispatcherPriority.Send);
            }
        }

        public async Task CloseForShutdownAsync(CancellationToken cancellationToken = default)
        {
            BeginShutdown();
            if (Interlocked.Exchange(ref _closeStarted, 1) != 0 || IsDispatcherUnavailable())
            {
                return;
            }

            try
            {
                if (_placement != null && !IsDispatcherUnavailable())
                {
                    Task flush = _dispatcher.CheckAccess() ? _placement.StopAsync()
                        : await _dispatcher.InvokeAsync(_placement.StopAsync, DispatcherPriority.Send, cancellationToken);
                    try { await flush.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); }
                    catch (TimeoutException ex) { _logger.Warning("AppMainWindowManager", "window-placement-flush-timed-out", nameof(CloseForShutdownAsync), ex); }
                }
                if (!_dispatcher.CheckAccess())
                {
                    await _dispatcher.InvokeAsync(
                        CloseForShutdownOnDispatcher,
                        DispatcherPriority.Send,
                        cancellationToken);
                    return;
                }

                CloseForShutdownOnDispatcher();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _closeStarted, 0);
                throw;
            }
            catch (InvalidOperationException) when (IsDispatcherUnavailable())
            {
            }
        }

        private void CloseForShutdownOnDispatcher()
        {
            MainWindow? window = _window;
            if (window == null)
            {
                return;
            }

            window.AllowCloseForRuntimeShutdown();
            window.Close();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            BeginShutdown();
            await CloseForShutdownAsync();
            Task<bool>? showTask = _activeShowTask;
            if (showTask != null)
            {
                try
                {
                    await showTask;
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "AppMainWindowManager",
                        () => $"main-window-dispose-show-drain-failed | error={ex.GetType().Name}",
                        nameof(DisposeAsync),
                        ex);
                }
            }

            Task? failureDialogTask = _failureDialogTask;
            if (failureDialogTask != null)
            {
                try
                {
                    await failureDialogTask;
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "AppMainWindowManager",
                        () => $"main-window-dispose-dialog-drain-failed | error={ex.GetType().Name}",
                        nameof(DisposeAsync),
                        ex);
                }
            }

            await DrainQueuedDispatcherCallbacksAsync();
            Volatile.Write(ref _layoutDisposalReady, 1);
            TryDisposeLayoutResources();
        }

        private async Task DrainQueuedDispatcherCallbacksAsync()
        {
            if (IsDispatcherUnavailable())
            {
                return;
            }

            try
            {
                await _dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ContextIdle);
            }
            catch (InvalidOperationException) when (IsDispatcherUnavailable())
            {
            }
            catch (OperationCanceledException) when (IsDispatcherUnavailable())
            {
            }
        }

        private static void TryClosePartialWindow(Window window)
        {
            try
            {
                window.Close();
            }
            catch (Exception)
            {
            }
        }

        private bool IsDispatcherUnavailable()
        {
            return _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished;
        }

        private async Task<bool> WaitForCallerAsync(Task<bool> sharedShowTask, CancellationToken cancellationToken)
        {
            try
            {
                return await sharedShowTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException) when (IsDispatcherUnavailable())
            {
                return false;
            }
            catch (InvalidOperationException) when (IsDispatcherUnavailable())
            {
                return false;
            }
        }

    }
}
