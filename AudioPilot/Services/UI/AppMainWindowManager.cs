using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI
{
    internal sealed class AppMainWindowManager : IAppMainWindowManager, IAsyncDisposable
    {
        private readonly Application _application;
        private readonly Dispatcher _dispatcher;
        private readonly Logger _logger;
        private readonly IAppDialogService _dialogs;
        private readonly SemaphoreSlim _transitionGate = new(1, 1);
        private Func<MainWindow>? _windowFactory;
        private volatile MainWindow? _window;
        private Task<bool>? _activeShowTask;
        private MainWindowOpenTarget _pendingOpenTarget;
        private volatile bool _desiredVisible;
        private volatile bool _isVisible;
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
            Logger? logger = null)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _dispatcher = application.Dispatcher;
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _logger = logger ?? Logger.Instance;
        }

        public bool IsCreated => _window != null;

        public bool IsVisible => _isVisible;

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
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0 || IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }

            if (_dispatcher.CheckAccess())
            {
                return WaitForCallerAsync(StartShowOnDispatcher(target), cancellationToken);
            }

            try
            {
                BeforeBackgroundShowDispatchForTests?.Invoke();
                Task<bool> sharedShowTask = _dispatcher.InvokeAsync(
                    () => StartShowOnDispatcher(target),
                    DispatcherPriority.Normal,
                    CancellationToken.None).Task.Unwrap();
                return WaitForCallerAsync(sharedShowTask, cancellationToken);
            }
            catch (InvalidOperationException) when (IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }
        }

        private Task<bool> StartShowOnDispatcher(MainWindowOpenTarget target)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0 || IsDispatcherUnavailable())
            {
                return Task.FromResult(false);
            }

            _desiredVisible = true;
            _pendingOpenTarget = target;
            if (_activeShowTask is { IsCompleted: false })
            {
                MainWindow? existingWindow = _window;
                existingWindow?.NavigateTo(target);
                return _activeShowTask;
            }

            _activeShowTask = ShowCoreOverrideForTests?.Invoke() ?? ShowCoreAsync();
            return _activeShowTask;
        }

        private async Task<bool> ShowCoreAsync()
        {
            bool enteredTransitionGate = false;
            try
            {
                await _transitionGate.WaitAsync();
                enteredTransitionGate = true;

                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return false;
                }

                bool firstShow = _window == null;
                MainWindow window = _window ?? CreateWindow();
                MainWindowOpenTarget target = _pendingOpenTarget;
                window.NavigateTo(target);

                if (!firstShow && window.Visibility == Visibility.Visible && window.WindowState != WindowState.Minimized)
                {
                    _isVisible = true;
                    WindowFirstPresentationHelper.Activate(window);
                    return true;
                }

                long started = Stopwatch.GetTimestamp();
                if (firstShow)
                {
                    WindowFirstPresentationHelper.Prepare(window);
                }

                window.WindowState = WindowState.Normal;
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
                    window.ShowInTaskbar = true;
                    window.Opacity = 1d;
                }

                bool CanReveal() => _desiredVisible && Volatile.Read(ref _shutdownStarted) == 0;

                Task<bool>? firstRevealTask = firstShow
                    ? WindowFirstPresentationHelper.RevealAsync(
                        window,
                        activate: true,
                        canReveal: CanReveal,
                        waitForFirstRender: true)
                    : null;
                window.Show();
                window.UpdateLayout();

                bool revealed = firstRevealTask != null
                    ? await firstRevealTask
                    : await WindowFirstPresentationHelper.RevealAsync(window, activate: true, canReveal: CanReveal);
                if (!revealed && _desiredVisible && Volatile.Read(ref _shutdownStarted) == 0)
                {
                    throw new InvalidOperationException("The native main window did not complete presentation.");
                }

                if (!revealed || !_desiredVisible || Volatile.Read(ref _shutdownStarted) != 0)
                {
                    _isVisible = false;
                    if (firstShow)
                    {
                        HidePreparedWindowCore(window);
                    }
                    else
                    {
                        HideWindowCore(window);
                    }

                    WindowFirstPresentationHelper.WithdrawFirstPresentation(window);

                    return false;
                }

                window.NavigateTo(_pendingOpenTarget);
                _isVisible = true;
                _logger.Info(
                    "AppMainWindowManager",
                    () => $"main-window-show-complete | firstShow={firstShow} target={_pendingOpenTarget} visible={window.IsVisible} active={window.IsActive} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                return true;
            }
            catch (Exception ex)
            {
                await HandlePresentationFailureAsync(ex);
                return false;
            }
            finally
            {
                if (enteredTransitionGate)
                {
                    _transitionGate.Release();
                }
            }
        }

        private MainWindow CreateWindow()
        {
            Func<MainWindow> factory = _windowFactory
                ?? throw new InvalidOperationException("The main-window factory is not configured.");
            MainWindow? candidate = null;
            long started = Stopwatch.GetTimestamp();

            try
            {
                candidate = factory();
                candidate.Closed += OnWindowClosed;
                _application.MainWindow = candidate;
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

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not MainWindow closedWindow || !ReferenceEquals(_window, closedWindow))
            {
                return;
            }

            closedWindow.Closed -= OnWindowClosed;
            _isVisible = false;
            _window = null;
            if (ReferenceEquals(_application.MainWindow, closedWindow))
            {
                _application.MainWindow = null;
            }
        }

        private async Task HandlePresentationFailureAsync(Exception ex)
        {
            MainWindow? failedWindow = _window;
            if (failedWindow != null)
            {
                failedWindow.Closed -= OnWindowClosed;
                failedWindow.AllowCloseForRuntimeShutdown();
                TryClosePartialWindow(failedWindow);
            }

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

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            try
            {
                await _dialogs.ShowErrorAsync(
                    "The AudioPilot window could not be displayed. Audio switching and tray features remain available. Try opening the window again.",
                    "Window unavailable",
                    owner: null);
            }
            catch (Exception dialogEx)
            {
                _logger.Warning(
                    "AppMainWindowManager",
                    () => $"main-window-failure-dialog-failed | error={dialogEx.GetType().Name}",
                    nameof(HandlePresentationFailureAsync),
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
            _isVisible = false;
            if (window == null)
            {
                return true;
            }

            HideWindowCore(window);
            return true;
        }

        private static void HideWindowCore(MainWindow window)
        {
            window.Opacity = 0d;
            window.ShowInTaskbar = false;
            window.Hide();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
        }

        private static void HidePreparedWindowCore(MainWindow window)
        {
            window.Opacity = 0d;
            window.ShowInTaskbar = false;
            window.Hide();
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
            _isVisible = false;
            Interlocked.Exchange(ref _shutdownStarted, 1);
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

            await DrainQueuedDispatcherCallbacksAsync();
            _transitionGate.Dispose();
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
