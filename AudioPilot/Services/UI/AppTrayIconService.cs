using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.ViewModels;
using Hardcodet.Wpf.TaskbarNotification;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.UI
{
    internal interface IAppTrayRuntimeActions
    {
        int OutputCycleDeviceCount { get; }
        int InputCycleDeviceCount { get; }
        bool OutputHotkeysEnabled { get; }
        bool InputHotkeysEnabled { get; }
        string? ToggleVisibilityHotkey { get; }
        (string? Output, string? Input) SwitchHotkeys { get; }
        IReadOnlyList<Models.AudioRoutine> Routines { get; }
        Task<bool> RequestShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default);
        void RequestHide();
        void SelectSettings();
        Task SwitchOutputAsync();
        Task SwitchInputAsync();
        Task RunRoutineAsync(string routineId);
    }

    internal sealed class AppViewModelTrayRuntimeActions(AppViewModel appVm) : IAppTrayRuntimeActions
    {
        private readonly AppViewModel _appVm = appVm ?? throw new ArgumentNullException(nameof(appVm));

        public int OutputCycleDeviceCount => _appVm.OutputCycleDevices.Count;
        public int InputCycleDeviceCount => _appVm.InputCycleDevices.Count;
        public bool OutputHotkeysEnabled => _appVm.OutputHotkeysEnabled;
        public bool InputHotkeysEnabled => _appVm.InputHotkeysEnabled;
        public string? ToggleVisibilityHotkey => _appVm.GetTrayMenuToggleAppVisibilityHotkey();
        public (string? Output, string? Input) SwitchHotkeys => _appVm.GetTrayMenuSwitchHotkeys();
        public IReadOnlyList<Models.AudioRoutine> Routines => _appVm.GetTrayMenuRoutines();
        public Task<bool> RequestShowAsync(MainWindowOpenTarget target = MainWindowOpenTarget.Default) =>
            _appVm.ShowWindowAsync(target);
        public void RequestHide() => _appVm.MinimizeWindow();
        public void SelectSettings() => _appVm.SelectedSettingsTabIndex = 3;
        public Task SwitchOutputAsync() => _appVm.SwitchDevicesAsync(_appVm.MuteMic, _appVm.MuteSound, _appVm.Deafen).AsTask();
        public Task SwitchInputAsync() => _appVm.SwitchInputDevicesAsync().AsTask();
        public Task RunRoutineAsync(string routineId) => _appVm.RunRoutineFromTrayAsync(routineId);
    }

    internal sealed class AppTrayIconService : IAppTrayIconService
    {
        private readonly Application _application;
        private readonly Logger _logger;
        private readonly IAppDialogService _dialogs;
        private TaskbarIcon? _taskbarIcon;
        private ContextMenu? _contextMenu;
        private IAppTrayRuntimeActions? _runtimeActions;
        private IAppMainWindowManager? _windowManager;
        private Func<Task>? _requestShutdown;
        private ImageSource? _icon;
        private int _presentationPrewarmState;
        private int _firstMenuOpenLogged;
        private int _shutdownStarted;
        private int _disposeStarted;

        internal AppTrayIconService(
            Application application,
            IAppDialogService dialogs,
            Logger? logger = null)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _logger = logger ?? Logger.Instance;
        }

        public bool IsReady => _taskbarIcon != null;

        internal bool IsPresentationPrewarmedForTests => Volatile.Read(ref _presentationPrewarmState) == 2;
        internal int PresentationPrewarmAttemptCountForTests { get; private set; }
        internal TaskbarIcon? TaskbarIconForTests => _taskbarIcon;

        internal void AttachRuntime(
            IAppTrayRuntimeActions runtimeActions,
            IAppMainWindowManager windowManager,
            Func<Task> requestShutdown)
        {
            _runtimeActions = runtimeActions ?? throw new ArgumentNullException(nameof(runtimeActions));
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
        }

        public bool EnsureVisible(ImageSource? icon = null)
        {
            if (!_application.Dispatcher.CheckAccess())
            {
                if (_application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                    return false;
                }

                try
                {
                    return _application.Dispatcher.Invoke(() => EnsureVisible(icon));
                }
                catch (InvalidOperationException) when (
                    _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                    return false;
                }
                catch (OperationCanceledException) when (
                    _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                    return false;
                }
            }

            if (Volatile.Read(ref _shutdownStarted) != 0 || Volatile.Read(ref _disposeStarted) != 0)
            {
                return false;
            }

            try
            {
                _icon = icon ?? _icon ?? AppIconImageProvider.GetSharedIconFrameForDpi(1d);
                EnsureCreated();
                _taskbarIcon!.IconSource = _icon;
                _taskbarIcon.ToolTipText = Constants.AppConstants.Identity.DisplayName;
                _taskbarIcon.Visibility = Visibility.Visible;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(
                    "AppTrayIconService",
                    () => $"tray-initialization-failed | error={ex.GetType().Name}",
                    nameof(EnsureVisible),
                    ex);
                DisposeCreatedIcon();
                return false;
            }
        }

        private void EnsureCreated()
        {
            if (_taskbarIcon != null)
            {
                return;
            }

            TaskbarIcon? taskbarIcon = null;
            try
            {
                ContextMenu contextMenu = new()
                {
                    Name = "AudioPilotTrayContextMenu",
                };
                contextMenu.SetResourceReference(FrameworkElement.StyleProperty, "AppTrayContextMenuStyle");
                System.Windows.Automation.AutomationProperties.SetName(contextMenu, "AudioPilot tray menu");

                taskbarIcon = new TaskbarIcon
                {
                    ContextMenu = contextMenu,
                    IconSource = _icon,
                    ToolTipText = Constants.AppConstants.Identity.DisplayName,
                    Visibility = Visibility.Visible,
                };
                taskbarIcon.TrayContextMenuOpen += OnTrayContextMenuOpen;
                taskbarIcon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;

                _contextMenu = contextMenu;
                _taskbarIcon = taskbarIcon;
                _logger.Info("AppTrayIconService", "tray-runtime-ready | ownerWindow=false");
            }
            catch
            {
                if (taskbarIcon != null)
                {
                    taskbarIcon.TrayContextMenuOpen -= OnTrayContextMenuOpen;
                    taskbarIcon.TrayMouseDoubleClick -= OnTrayMouseDoubleClick;
                    taskbarIcon.Dispose();
                }

                throw;
            }
        }

        private void OnTrayContextMenuOpen(object sender, RoutedEventArgs e)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                _contextMenu?.IsOpen = false;
                return;
            }

            try
            {
                long started = Stopwatch.GetTimestamp();
                RebindContextMenuTheme();
                PopulateContextMenu();
                if (Interlocked.Exchange(ref _firstMenuOpenLogged, 1) == 0)
                {
                    _logger.Info(
                        "AppTrayIconService",
                        () => $"tray-menu-first-open-complete | prewarmed={IsPresentationPrewarmedForTests} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AppTrayIconService", "Themed tray menu failed", nameof(OnTrayContextMenuOpen), ex);
                if (_contextMenu != null)
                {
                    _contextMenu.Items.Clear();
                    MenuItem unavailable = AppTrayMenuBuilder.CreateTrayMenuItem(
                        new AppTrayMenuBuilder.TrayMenuEntry(AppTrayMenuBuilder.TrayMenuEntryKind.Unavailable, "AudioPilot menu unavailable"));
                    unavailable.IsEnabled = false;
                    _contextMenu.Items.Add(unavailable);
                }
            }
        }

        private void RebindContextMenuTheme()
        {
            ContextMenu? contextMenu = _contextMenu;
            if (contextMenu == null)
            {
                return;
            }

            contextMenu.SetResourceReference(FrameworkElement.StyleProperty, "AppTrayContextMenuStyle");
        }

        internal void SchedulePresentationPrewarm()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0
                || Volatile.Read(ref _disposeStarted) != 0
                || Interlocked.CompareExchange(ref _presentationPrewarmState, 1, 0) != 0)
            {
                return;
            }

            if (_application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
            {
                Volatile.Write(ref _presentationPrewarmState, 2);
                return;
            }

            try
            {
                _ = _application.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(PrewarmPresentationOnDispatcher));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _presentationPrewarmState, 2);
                _logger.Warning(
                    "AppTrayIconService",
                    () => $"tray-menu-prewarm-schedule-failed | error={ex.GetType().Name}",
                    nameof(SchedulePresentationPrewarm),
                    ex);
            }
        }

        private void PrewarmPresentationOnDispatcher()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0 || Volatile.Read(ref _disposeStarted) != 0)
            {
                Volatile.Write(ref _presentationPrewarmState, 2);
                return;
            }

            long started = Stopwatch.GetTimestamp();
            PresentationPrewarmAttemptCountForTests++;
            try
            {
                PrewarmPresentationCore(_application);
                _logger.Debug(
                    "AppTrayIconService",
                    () => $"tray-menu-prewarm-complete | elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "AppTrayIconService",
                    () => $"tray-menu-prewarm-failed | error={ex.GetType().Name}",
                    nameof(PrewarmPresentationOnDispatcher),
                    ex);
            }
            finally
            {
                Volatile.Write(ref _presentationPrewarmState, 2);
            }
        }

        private static void PrewarmPresentationCore(Application application)
        {
            Style contextMenuStyle = application.TryFindResource("AppTrayContextMenuStyle") as Style
                ?? throw new InvalidOperationException("The tray context-menu style is unavailable.");
            Style menuItemStyle = application.TryFindResource("AppTrayMenuItemStyle") as Style
                ?? throw new InvalidOperationException("The tray menu-item style is unavailable.");
            Style separatorStyle = application.TryFindResource("AppTrayMenuSeparatorStyle") as Style
                ?? throw new InvalidOperationException("The tray menu-separator style is unavailable.");

            ContextMenu probeMenu = new() { Style = contextMenuStyle };
            MenuItem probeItem = AppTrayMenuBuilder.CreateTrayMenuItem(
                new AppTrayMenuBuilder.TrayMenuEntry(
                    AppTrayMenuBuilder.TrayMenuEntryKind.ShowWindow,
                    "AudioPilot",
                    GestureText: "Ctrl+Alt+H"));
            probeItem.Style = menuItemStyle;
            Separator probeSeparator = new() { Style = separatorStyle };
            probeMenu.Items.Add(probeItem);
            probeMenu.Items.Add(probeSeparator);

            _ = probeMenu.ApplyTemplate();
            _ = probeItem.ApplyTemplate();
            _ = probeSeparator.ApplyTemplate();
            probeMenu.Measure(new Size(348d, 256d));
            probeMenu.Arrange(new Rect(probeMenu.DesiredSize));
            probeMenu.UpdateLayout();
            probeMenu.Items.Clear();
        }

        private void PopulateContextMenu()
        {
            IAppTrayRuntimeActions runtime = _runtimeActions ?? throw new InvalidOperationException("The tray runtime is not attached.");
            ContextMenu contextMenu = _contextMenu ?? throw new InvalidOperationException("The tray context menu is unavailable.");
            IAppMainWindowManager windowManager = _windowManager ?? throw new InvalidOperationException("The window manager is unavailable.");

            bool hasOutputCycle = AppTrayMenuBuilder.ShouldShowSwitchMenuItem(runtime.OutputCycleDeviceCount, runtime.OutputHotkeysEnabled);
            bool hasInputCycle = AppTrayMenuBuilder.ShouldShowSwitchMenuItem(runtime.InputCycleDeviceCount, runtime.InputHotkeysEnabled);
            (string? outputSwitchHotkey, string? inputSwitchHotkey) = runtime.SwitchHotkeys;

            IReadOnlyList<AppTrayMenuBuilder.TrayMenuEntry> entries = AppTrayMenuBuilder.BuildTrayMenuEntries(
                windowManager.IsVisible,
                runtime.ToggleVisibilityHotkey,
                hasOutputCycle,
                hasOutputCycle ? GetCurrentDefaultPlaybackDeviceName() : null,
                outputSwitchHotkey,
                hasInputCycle,
                hasInputCycle ? GetCurrentDefaultRecordingDeviceName() : null,
                inputSwitchHotkey,
                runtime.Routines);

            contextMenu.MaxHeight = AppTrayMenuBuilder.ResolveTrayMenuMaxHeightForRuntime();
            contextMenu.Items.Clear();

            foreach (AppTrayMenuBuilder.TrayMenuEntry entry in entries)
            {
                if (entry.Kind == AppTrayMenuBuilder.TrayMenuEntryKind.Separator)
                {
                    Separator separator = new();
                    separator.SetResourceReference(FrameworkElement.StyleProperty, "AppTrayMenuSeparatorStyle");
                    contextMenu.Items.Add(separator);
                    continue;
                }

                MenuItem item = AppTrayMenuBuilder.CreateTrayMenuItem(entry);
                AttachAction(item, entry);
                contextMenu.Items.Add(item);
            }
        }

        private void AttachAction(MenuItem item, AppTrayMenuBuilder.TrayMenuEntry entry)
        {
            if (entry.Kind is AppTrayMenuBuilder.TrayMenuEntryKind.Separator
                or AppTrayMenuBuilder.TrayMenuEntryKind.Unavailable)
            {
                return;
            }

            item.Click += (_, _) => ObserveUiAction(
                () => ExecuteEntryAsync(entry),
                $"tray-{entry.Kind.ToString().ToLowerInvariant()}-failed");
        }

        private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            ObserveUiAction(
                ShowFromTrayDoubleClickAsync,
                "tray-double-click-show-failed");
        }

        private Task<bool> ShowFromTrayDoubleClickAsync()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0
                || Volatile.Read(ref _disposeStarted) != 0
                || AppDispatcherHelper.IsDispatcherUnavailable(_application.Dispatcher))
            {
                return Task.FromResult(false);
            }

            try
            {
                return _application.Dispatcher.InvokeAsync(
                    () => ExecuteEntryAsync(new AppTrayMenuBuilder.TrayMenuEntry(
                        AppTrayMenuBuilder.TrayMenuEntryKind.ShowWindow,
                        "Show AudioPilot")),
                    DispatcherPriority.Input,
                    CancellationToken.None).Task.Unwrap();
            }
            catch (InvalidOperationException) when (AppDispatcherHelper.IsDispatcherUnavailable(_application.Dispatcher))
            {
                return Task.FromResult(false);
            }
        }

        internal async Task<bool> ExecuteEntryAsync(AppTrayMenuBuilder.TrayMenuEntry entry)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0 || Volatile.Read(ref _disposeStarted) != 0)
            {
                return false;
            }

            IAppTrayRuntimeActions runtime = _runtimeActions ?? throw new InvalidOperationException("The tray runtime is not attached.");
            IAppMainWindowManager windowManager = _windowManager ?? throw new InvalidOperationException("The window manager is unavailable.");

            switch (entry.Kind)
            {
                case AppTrayMenuBuilder.TrayMenuEntryKind.ShowWindow:
                    return await runtime.RequestShowAsync();
                case AppTrayMenuBuilder.TrayMenuEntryKind.HideWindow:
                    runtime.RequestHide();
                    return !windowManager.IsVisible;
                case AppTrayMenuBuilder.TrayMenuEntryKind.Settings:
                    runtime.SelectSettings();
                    return await runtime.RequestShowAsync(MainWindowOpenTarget.Settings);
                case AppTrayMenuBuilder.TrayMenuEntryKind.SwitchOutput:
                    return await ExecuteSwitchAsync(output: true);
                case AppTrayMenuBuilder.TrayMenuEntryKind.SwitchInput:
                    return await ExecuteSwitchAsync(output: false);
                case AppTrayMenuBuilder.TrayMenuEntryKind.Routine when !string.IsNullOrWhiteSpace(entry.RoutineId):
                    await runtime.RunRoutineAsync(entry.RoutineId);
                    return true;
                case AppTrayMenuBuilder.TrayMenuEntryKind.Exit:
                    Func<Task> shutdown = _requestShutdown ?? throw new InvalidOperationException("The shutdown action is unavailable.");
                    await shutdown();
                    return true;
                default:
                    return false;
            }
        }

        private async Task<bool> ExecuteSwitchAsync(bool output)
        {
            try
            {
                if (output)
                {
                    await _runtimeActions!.SwitchOutputAsync();
                }
                else
                {
                    await _runtimeActions!.SwitchInputAsync();
                }

                return true;
            }
            catch (Exception ex)
            {
                string operation = output ? "tray-output-switch" : "tray-input-switch";
                _logger.Error("AppTrayIconService", () => $"{operation}-failed | error={ex.GetType().Name}", nameof(ExecuteSwitchAsync), ex);
                await _dialogs.ShowErrorAsync(output ? "Error switching output devices." : "Error switching input devices.");
                return false;
            }
        }

        private void ObserveUiAction(Func<Task<bool>> action, string operation)
        {
            _ = ObserveUiActionAsync(action, operation);
        }

        private async Task ObserveUiActionAsync(Func<Task<bool>> action, string operation)
        {
            try
            {
                _ = await action();
            }
            catch (Exception ex)
            {
                _logger.Error("AppTrayIconService", () => $"{operation} | error={ex.GetType().Name}", nameof(ObserveUiActionAsync), ex);
            }
        }

        private static string GetCurrentDefaultPlaybackDeviceName()
        {
            return DeviceCacheHelper.IsInitialized
                ? AppTrayMenuBuilder.ResolveDefaultDeviceName(() => DeviceCacheHelper.Instance.GetPlaybackDeviceNameWithoutRefresh(Role.Multimedia))
                : "Unavailable";
        }

        private static string GetCurrentDefaultRecordingDeviceName()
        {
            return DeviceCacheHelper.IsInitialized
                ? AppTrayMenuBuilder.ResolveDefaultDeviceName(() => DeviceCacheHelper.Instance.GetRecordingDeviceNameWithoutRefresh(Role.Console))
                : "Unavailable";
        }

        public void Hide()
        {
            _taskbarIcon?.Visibility = Visibility.Collapsed;
        }

        public void ShowBalloon(string title, string message)
        {
            if (EnsureVisible())
            {
                _taskbarIcon!.ShowBalloonTip(title, message, BalloonIcon.Info);
            }
        }

        public void BeginShutdown()
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            {
                return;
            }

            if (_application.Dispatcher.CheckAccess())
            {
                HideForShutdownOnDispatcher();
                return;
            }

            if (_application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                _ = _application.Dispatcher.InvokeAsync(
                    HideForShutdownOnDispatcher,
                    System.Windows.Threading.DispatcherPriority.Send);
            }
            catch (InvalidOperationException) when (
                _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
            {
            }
        }

        private void HideForShutdownOnDispatcher()
        {
            _contextMenu?.IsOpen = false;

            _taskbarIcon?.Visibility = Visibility.Collapsed;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            BeginShutdown();
            if (!_application.Dispatcher.CheckAccess()
                && !_application.Dispatcher.HasShutdownStarted
                && !_application.Dispatcher.HasShutdownFinished)
            {
                try
                {
                    _application.Dispatcher.Invoke(DisposeCreatedIcon);
                }
                catch (InvalidOperationException) when (
                    _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                }
                catch (OperationCanceledException) when (
                    _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                }
                return;
            }

            DisposeCreatedIcon();
        }

        private void DisposeCreatedIcon()
        {
            TaskbarIcon? taskbarIcon = Interlocked.Exchange(ref _taskbarIcon, null);
            _contextMenu = null;
            if (taskbarIcon == null)
            {
                return;
            }

            try
            {
                taskbarIcon.TrayContextMenuOpen -= OnTrayContextMenuOpen;
                taskbarIcon.TrayMouseDoubleClick -= OnTrayMouseDoubleClick;
                taskbarIcon.Visibility = Visibility.Collapsed;
                taskbarIcon.Dispose();
                _logger.Info("AppTrayIconService", "tray-runtime-disposed");
            }
            catch (Exception ex)
            {
                _logger.Warning("AppTrayIconService", "tray-disposal-failed", nameof(DisposeCreatedIcon), ex);
            }
        }
    }
}
