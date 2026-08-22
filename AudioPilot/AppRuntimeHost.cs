using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.ViewModels;
using Microsoft.Win32;

namespace AudioPilot
{
    internal sealed class AppRuntimeStartupAbortedException(
        AppRuntimeStartupInitializationOutcome outcome)
        : Exception($"Application runtime startup ended with outcome '{outcome}'.")
    {
        internal AppRuntimeStartupInitializationOutcome Outcome { get; } = outcome;
    }

    internal enum AppShutdownStepOutcome
    {
        Completed = 0,
        Faulted = 1,
        TimedOut = 2,
    }

    internal readonly record struct AppShutdownStepResult(
        AppShutdownStepOutcome Outcome,
        Exception? Exception = null);

    /// <summary>
    /// Application-scoped composition and lifetime root. Runtime services remain available without a main WPF
    /// window; the visual tree is materialized only by <see cref="AppMainWindowManager"/>.
    /// </summary>
    internal sealed class AppRuntimeHost : IAsyncDisposable
    {
        private readonly Application _application;
        private readonly SingleInstanceHelper _singleInstance;
        private readonly IAppDialogService _dialogs;
        private readonly Logger _logger;
        private readonly AppRuntimeServiceBundle _runtimeServices;
        private readonly AudioDeviceService _audioService;
        private readonly HotkeyService _hotkeyService;
        private readonly AppMainWindowManager _windowManager;
        private readonly AppTrayIconService _trayService;
        private readonly OverlayService _overlayService;
        private readonly AppHotplugOverlayCoordinator _hotplugOverlayCoordinator;
        private readonly AppRuntimeStartupResumeCoordinator _startupResumeCoordinator;
        private readonly AppRuntimeHotkeyBindings _hotkeyBindings;
        private readonly AppViewModel _appVm;
        private readonly Lock _shutdownSync = new();
        private Task? _shutdownTask;
        private InputLanguageManager? _inputLanguageManager;
        private bool _systemEventHandlersRegistered;
        private bool _globalEventHandlersRegistered;
        private bool _initialized;
        private int _shutdownStarted;
        private int _disposeStarted;
        private int _hotplugIgnoredDuringShutdownLogged;
        private int _pendingHotplugSignals;
        private int _hotplugSuppressedRefreshes;
        private int _hotplugCoalescedEvents;
        private int _hotplugAppliedRefreshes;
        private CancellationTokenSource? _hotplugRefreshDebounceCts;

        private AppRuntimeHost(
            Application application,
            SingleInstanceHelper singleInstance,
            IAppDialogService dialogs,
            Logger logger,
            AppRuntimeServiceBundle runtimeServices,
            HotkeyService hotkeyService,
            AppMainWindowManager windowManager,
            AppTrayIconService trayService,
            OverlayService overlayService,
            AppHotplugOverlayCoordinator hotplugOverlayCoordinator,
            AppRuntimeStartupResumeCoordinator startupResumeCoordinator,
            AppViewModel appViewModel)
        {
            _application = application;
            _singleInstance = singleInstance;
            _dialogs = dialogs;
            _logger = logger;
            _runtimeServices = runtimeServices;
            _audioService = _runtimeServices.AudioService;
            _hotkeyService = hotkeyService;
            _windowManager = windowManager;
            _trayService = trayService;
            _overlayService = overlayService;
            _hotplugOverlayCoordinator = hotplugOverlayCoordinator;
            _startupResumeCoordinator = startupResumeCoordinator;
            _appVm = appViewModel;
            _hotkeyBindings = CreateHotkeyBindings();
        }

        internal AppViewModel AppViewModel => _appVm;
        internal IAppMainWindowManager WindowManager => _windowManager;
        internal bool IsInitialized => _initialized;

        internal static async Task<AppRuntimeHost> CreateAndInitializeAsync(
            Application application,
            SingleInstanceHelper singleInstance,
            IAppDialogService dialogs,
            Logger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(application);
            ArgumentNullException.ThrowIfNull(singleInstance);
            ArgumentNullException.ThrowIfNull(dialogs);

            AppRuntimeHost? host = null;
            try
            {
                host = await ComposeAsync(application, singleInstance, dialogs, logger ?? Logger.Instance);
                await host.InitializeAsync();
                return host;
            }
            catch
            {
                if (host != null)
                {
                    await host.DisposeAsync();
                }

                throw;
            }
        }

        private static async Task<AppRuntimeHost> ComposeAsync(
            Application application,
            SingleInstanceHelper singleInstance,
            IAppDialogService dialogs,
            Logger logger)
        {
            AppRuntimeServiceBundle? runtimeServices = null;
            HotkeyService? hotkeyService = null;
            AppMainWindowManager? windowManager = null;
            AppTrayIconService? trayService = null;
            OverlayService? overlayService = null;
            MediaOverlayCommandService? mediaOverlayCommands = null;
            AppRuntimeStartupResumeCoordinator? startupResumeCoordinator = null;
            AppViewModel? appViewModel = null;

            try
            {
                runtimeServices = AppRuntimeServiceBundle.CreateDefault();
                AudioDeviceService audioService = runtimeServices.AudioService;
                SettingsService settingsService = runtimeServices.SettingsService;
                hotkeyService = new HotkeyService();
                DeviceCacheHelper.Initialize(audioService);

                windowManager = new AppMainWindowManager(application, dialogs, logger);
                trayService = new AppTrayIconService(application, dialogs, logger);
                AppShellService shell = new(windowManager, trayService);
                overlayService = new OverlayService();
                mediaOverlayCommands = new MediaOverlayCommandService();
                MainWindowVisibilityCoordinator visibilityCoordinator = new(logger);

                AppViewModel? appViewModelReference = null;
                AppCliOverlayCoordinator cliOverlayCoordinator = new(
                    audioService,
                    overlayService,
                    mediaOverlayCommands,
                    logger,
                    () => appViewModelReference?.CurrentSettings,
                    mediaHistoryRecorder: entry => appViewModelReference?.RecordCoordinatorExecutionHistory(entry),
                    endpointVolumeApplied: (mode, endpointId, volumePercent, isMuted) =>
                        appViewModelReference?.ProjectEndpointVolumeStateFromCommand(mode, endpointId, volumePercent, isMuted));
                AppSwitchCommandCoordinator switchCoordinator = new(
                    audioService,
                    overlayService,
                    logger,
                    runtimeServices.BluetoothReconnectCoordinator.Value,
                    (output, suppressMs) => appViewModelReference?.SuppressConnectedHotplugOverlay(output, suppressMs));

                appViewModel = appViewModelReference = new AppViewModel(
                    settings: settingsService,
                    startup: runtimeServices.StartupService,
                    audio: audioService,
                    hotkeys: hotkeyService,
                    cliOverlayCoordinator: cliOverlayCoordinator,
                    switchCoordinator: switchCoordinator,
                    shell: shell,
                    mixerFactory: () => new MixerViewModel(audioService, application.Dispatcher, AudioMixerMode.Output, logger, DeviceCacheHelper.Instance),
                    inputMixerFactory: () => new MixerViewModel(audioService, application.Dispatcher, AudioMixerMode.Input, logger, DeviceCacheHelper.Instance),
                    overlay: overlayService,
                    dispatcher: application.Dispatcher,
                    routineBluetoothReconnectCoordinator: runtimeServices.BluetoothReconnectCoordinator.Value,
                    deviceCache: DeviceCacheHelper.Instance,
                    dialogService: dialogs);

                AppStartupCoordinator startupCoordinator = new(
                    appViewModel,
                    hotkeyService,
                    dialogs,
                    onStartHiddenToTray: visibilityCoordinator.MarkPendingAutoScrollOnNextShow);
                AppHotplugOverlayCoordinator hotplugOverlayCoordinator = new(
                    settingsService,
                    appViewModel,
                    overlayService);
                AppRuntimeHost? hostReference = null;
                startupResumeCoordinator = new AppRuntimeStartupResumeCoordinator(
                    logger,
                    audioService,
                    settingsService,
                    startupCoordinator,
                    appViewModel,
                    hotplugOverlayCoordinator,
                    message => dialogs.ShowErrorAsync(message, DialogText.Captions.StartupError),
                    () => hostReference?.ObserveShutdownRequest("startup-initialization-failure"));

                AppRuntimeHost host = hostReference = new AppRuntimeHost(
                    application,
                    singleInstance,
                    dialogs,
                    logger,
                    runtimeServices,
                    hotkeyService,
                    windowManager,
                    trayService,
                    overlayService,
                    hotplugOverlayCoordinator,
                    startupResumeCoordinator,
                    appViewModel);
                windowManager.SetWindowFactory(() => new MainWindow(
                    new MainWindowDependencies(
                        appViewModel,
                        shell,
                        visibilityCoordinator,
                        () => host.RequestShutdownAsync("main-window-close"))));
                trayService.AttachRuntime(new AppViewModelTrayRuntimeActions(appViewModel), windowManager, () => host.RequestShutdownAsync("tray-exit"));
                return host;
            }
            catch
            {
                startupResumeCoordinator?.Dispose();
                if (appViewModel != null)
                {
                    await TryCleanupFailedCompositionStepAsync(appViewModel.CleanupAsync, logger, "cleanup-viewmodel");
                }

                if (hotkeyService != null)
                {
                    TryCleanupFailedCompositionStep(hotkeyService.Dispose, logger, "dispose-hotkeys");
                }
                if (windowManager != null)
                {
                    await TryCleanupFailedCompositionStepAsync(() => windowManager.DisposeAsync().AsTask(), logger, "dispose-window-manager");
                }

                if (trayService != null)
                {
                    TryCleanupFailedCompositionStep(trayService.Dispose, logger, "dispose-tray");
                }
                if (runtimeServices != null)
                {
                    await TryCleanupFailedCompositionStepAsync(() => runtimeServices.DisposeAsync().AsTask(), logger, "dispose-runtime-services");
                }

                if (overlayService != null)
                {
                    TryCleanupFailedCompositionStep(overlayService.Dispose, logger, "dispose-overlay");
                }

                TryCleanupFailedCompositionStep(DeviceCacheHelper.DisposeSingleton, logger, "dispose-device-cache");
                TryCleanupFailedCompositionStep(ComThreadingHelper.DisposeCoreAudioExecutor, logger, "dispose-core-audio");
                throw;
            }
        }

        private static async Task TryCleanupFailedCompositionStepAsync(
            Func<Task> cleanup,
            Logger logger,
            string step)
        {
            try
            {
                await cleanup();
            }
            catch (Exception ex)
            {
                logger.Warning("AppRuntimeHost", () => $"runtime-composition-cleanup-failed | step={step} error={ex.GetType().Name}", nameof(ComposeAsync), ex);
            }
        }

        private static void TryCleanupFailedCompositionStep(Action? cleanup, Logger logger, string step)
        {
            if (cleanup == null)
            {
                return;
            }

            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                logger.Warning("AppRuntimeHost", () => $"runtime-composition-cleanup-failed | step={step} error={ex.GetType().Name}", nameof(ComposeAsync), ex);
            }
        }

        private async Task InitializeAsync()
        {
            long started = Stopwatch.GetTimestamp();
            _hotkeyService.InitializeInfrastructure();
            _hotkeyBindings.Wire();
            RegisterGlobalEventHandlers();
            RegisterSystemEventHandlers();

            WindowThemeResolver.SetApplicationThemeProvider(() => _appVm.Theme);
            // A windowless launch has no MainWindow constructor to install the initial theme dictionary.
            // Seed application resources before constructing the detached tray ContextMenu so a saved
            // System theme still resolves AudioPilot's styles on the first menu open.
            WindowThemeResolver.ApplyApplicationMainWindowTheme(_appVm.Theme);
            _ = MediaKeyHelper.PrewarmSystemMediaCommandsAsync();

            AppRuntimeStartupInitializationOutcome startupOutcome =
                await _startupResumeCoordinator.InitializeAsync(nameof(InitializeAsync));
            EnsureStartupSucceeded(startupOutcome);
            // Reassert the loaded value even when it equals AppViewModel's enum default and therefore did
            // not run the property setter during settings hydration.
            WindowThemeResolver.ApplyApplicationMainWindowTheme(_appVm.Theme);
            bool trayReady = _trayService.EnsureVisible();

            if (!trayReady && !_windowManager.IsCreated)
            {
                _logger.Warning("AppRuntimeHost", "tray-unavailable | action=force-main-window");
                if (!await _windowManager.ShowAsync())
                {
                    throw new InvalidOperationException("Neither the tray icon nor the main window could be presented.");
                }
            }

            _initialized = true;
            if (trayReady)
            {
                _trayService.SchedulePresentationPrewarm();
            }

            _logger.Info(
                "AppRuntimeHost",
                () => $"runtime-host-ready | trayReady={trayReady} mainWindowCreated={_windowManager.IsCreated} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }

        internal static void EnsureStartupSucceeded(AppRuntimeStartupInitializationOutcome startupOutcome)
        {
            if (startupOutcome != AppRuntimeStartupInitializationOutcome.Succeeded)
            {
                throw new AppRuntimeStartupAbortedException(startupOutcome);
            }
        }

        private AppRuntimeHotkeyBindings CreateHotkeyBindings()
        {
            return new AppRuntimeHotkeyBindings(
                _hotkeyService,
                onToggleAppVisibility: () => DispatchUiHotkeyActionAsync(
                    async () => _ = await _appVm.ToggleWindowVisibilityAsync(),
                    "Toggle window visibility hotkey action error"),
                onMediaShowCurrentTrack: () => RunBackgroundHotkeyAction(_appVm.ShowCurrentTrackFromCli, "Show current track hotkey action error"),
                onMediaPlayPause: () => RunBackgroundHotkeyAction(_appVm.MediaPlayPauseFromHotkey, "Play/pause hotkey action error"),
                onMediaNextTrack: () => RunBackgroundHotkeyAction(_appVm.MediaNextTrackFromHotkey, "Next track hotkey action error"),
                onMediaPreviousTrack: () => RunBackgroundHotkeyAction(_appVm.MediaPreviousTrackFromHotkey, "Previous track hotkey action error"),
                onMuteMic: () => ToggleFlagWithOverlay(
                    () => _appVm.MuteMic = !_appVm.MuteMic,
                    () => _appVm.MuteMic,
                    "Microphone muted",
                    "Microphone unmuted"),
                onMuteSound: () => ToggleFlagWithOverlay(
                    () => _appVm.MuteSound = !_appVm.MuteSound,
                    () => _appVm.MuteSound,
                    "Sound muted",
                    "Sound unmuted"),
                onDeafen: () => ToggleFlagWithOverlay(
                    () => _appVm.Deafen = !_appVm.Deafen,
                    () => _appVm.Deafen,
                    "Deafened",
                    "Undeafened"),
                onListenToInput: () => RunBackgroundHotkeyAction(() => _ = _appVm.ToggleListenToInputFromCli(), "Listen hotkey action error"),
                onMasterVolumeUp: () => RunBackgroundHotkeyAction(() => _ = _appVm.StepMasterVolumeUpFromCli(), "Master volume up hotkey action error"),
                onMasterVolumeDown: () => RunBackgroundHotkeyAction(() => _ = _appVm.StepMasterVolumeDownFromCli(), "Master volume down hotkey action error"),
                onMicVolumeUp: () => RunBackgroundHotkeyAction(() => _ = _appVm.StepMicVolumeUpFromCli(), "Microphone volume up hotkey action error"),
                onMicVolumeDown: () => RunBackgroundHotkeyAction(() => _ = _appVm.StepMicVolumeDownFromCli(), "Microphone volume down hotkey action error"),
                onInputSwitch: () => RunHotkeyActionAsync(() => _appVm.SwitchInputDevicesAsync().AsTask(), "Input hotkey handler error", "Error switching input devices."),
                onOutputSwitch: () => RunHotkeyActionAsync(() => _appVm.SwitchDevicesAsync(_appVm.MuteMic, _appVm.MuteSound, _appVm.Deafen).AsTask(), "Hotkey handler error", "Error switching devices."),
                onInputReverseSwitch: () => RunHotkeyActionAsync(() => _appVm.SwitchInputDevicesAsync(reverse: true).AsTask(), "Reverse input hotkey handler error", "Error switching input devices."),
                onOutputReverseSwitch: () => RunHotkeyActionAsync(() => _appVm.SwitchDevicesAsync(_appVm.MuteMic, _appVm.MuteSound, _appVm.Deafen, reverse: true).AsTask(), "Reverse hotkey handler error", "Error switching devices."));
        }

        private void DispatchUiHotkeyAction(Action action)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            AppDispatcherHelper.Dispatch(
                _application.Dispatcher,
                _logger,
                action,
                "Hotkey action error",
                nameof(DispatchUiHotkeyAction));
        }

        private void DispatchUiHotkeyActionAsync(Func<Task> action, string errorMessage)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            AppDispatcherHelper.DispatchAsync(
                _application.Dispatcher,
                _logger,
                async () =>
                {
                    if (!IsShutdownRequested())
                    {
                        await action();
                    }
                },
                errorMessage,
                nameof(DispatchUiHotkeyActionAsync));
        }

        private void RunBackgroundHotkeyAction(Action action, string errorMessage)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Error("AppRuntimeHost", errorMessage, nameof(RunBackgroundHotkeyAction), ex);
            }
        }

        private void ToggleFlagWithOverlay(
            Action toggle,
            Func<bool> readState,
            string enabledMessage,
            string disabledMessage)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            DispatchUiHotkeyAction(() =>
            {
                toggle();
                bool enabled = readState();
                _overlayService.Show(
                    enabled ? OverlayActionStateKind.Disabled : OverlayActionStateKind.Enabled,
                    enabled ? enabledMessage : disabledMessage);
            });
        }

        private void RunHotkeyActionAsync(Func<Task> action, string logMessage, string userMessage)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            _ = AppDispatcherHelper.ExecuteAsync(
                action,
                _logger,
                _application.Dispatcher,
                _dialogs,
                logMessage,
                userMessage,
                nameof(RunHotkeyActionAsync));
        }

        private void RegisterGlobalEventHandlers()
        {
            if (_globalEventHandlersRegistered)
            {
                return;
            }

            _application.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            _audioService.DeviceStateChanged += OnAudioDeviceStateChanged;
            _globalEventHandlersRegistered = true;
        }

        private void RegisterSystemEventHandlers()
        {
            if (_systemEventHandlersRegistered)
            {
                return;
            }

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _inputLanguageManager = InputLanguageManagerHelper.TryGetCurrent();
            _inputLanguageManager?.InputLanguageChanged += OnInputLanguageChanged;
            _systemEventHandlersRegistered = true;
        }

        private void OnAudioDeviceStateChanged()
        {
            if (IsShutdownRequested())
            {
                if (Interlocked.Exchange(ref _hotplugIgnoredDuringShutdownLogged, 1) == 0)
                {
                    _logger.Warning("AppRuntimeHost", "Ignoring hotplug signal during shutdown", nameof(OnAudioDeviceStateChanged));
                }
                return;
            }

            _ = RunAudioTestLifecycleActionAsync(
                () => _appVm.ReconcileAudioEndpointTestDevicesAsync(CancellationToken.None),
                "audio-test-hotplug-reconcile");

            int pendingSignals = Interlocked.Increment(ref _pendingHotplugSignals);
            int debounceDelayMs = AppHotplugRefreshHelper.ResolveDebounceMs(
                pendingSignals,
                RuntimeTuningConfig.HotplugRefreshDebounceMs,
                _windowManager.IsVisible);
            CancellationTokenSource debounceCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
                ref _hotplugRefreshDebounceCts,
                out bool replacedPrevious);
            if (replacedPrevious)
            {
                Interlocked.Increment(ref _hotplugSuppressedRefreshes);
            }

            _ = AppDispatcherHelper.InvokeAsync(
                _application.Dispatcher,
                _logger,
                () => AppHotplugRefreshHelper.ExecuteAsync(
                    debounceDelayMs,
                    new AppHotplugRefreshDependencies(
                        IsShutdownRequested,
                        static (delayMs, token) => Task.Delay(delayMs, token),
                        () => Interlocked.Exchange(ref _pendingHotplugSignals, 0),
                        extras => Interlocked.Add(ref _hotplugCoalescedEvents, extras),
                        _appVm.RefreshDevicesForHotplugAsync,
                        token => AppHotplugRefreshHelper.WaitForSettlementAsync(
                            innerToken => _appVm.WaitForMixerRefreshSettlementAsync(innerToken),
                            token),
                        _hotplugOverlayCoordinator.ProcessPostRefresh,
                        _appVm.ExecuteDeviceChangeTriggeredRoutinesAsync,
                        () => Interlocked.Increment(ref _hotplugAppliedRefreshes),
                        () => Interlocked.CompareExchange(ref _hotplugCoalescedEvents, 0, 0),
                        () => Interlocked.CompareExchange(ref _hotplugSuppressedRefreshes, 0, 0),
                        AppConstants.Timing.HotplugDiagnosticsLogEveryNAppliedRefreshes),
                    _logger,
                    nameof(OnAudioDeviceStateChanged),
                    debounceCts.Token),
                "Failed to schedule hotplug refresh",
                nameof(OnAudioDeviceStateChanged));
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            bool appearanceChanged = e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color;
            bool localeChanged = e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Locale;
            if (!appearanceChanged && !localeChanged)
            {
                return;
            }

            if (!_application.Dispatcher.CheckAccess())
            {
                AppDispatcherHelper.Dispatch(
                    _application.Dispatcher,
                    _logger,
                    () => OnUserPreferenceChanged(sender, e),
                    "Failed to apply a Windows preference change",
                    nameof(OnUserPreferenceChanged));
                return;
            }

            if (appearanceChanged)
            {
                // The detached tray menu resolves brushes from application resources even when the
                // main window has never been created. Reapply those resources for System-theme and
                // high-contrast changes instead of updating only an existing window.
                WindowThemeResolver.ApplyApplicationMainWindowTheme(_appVm.Theme);
            }

            if (localeChanged)
            {
                _appVm.RefreshHotkeyDisplayLabels();
            }
        }

        private void OnInputLanguageChanged(object sender, InputLanguageEventArgs e)
        {
            if (!IsShutdownRequested())
            {
                _appVm.RefreshHotkeyDisplayLabels();
            }
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            if (e.Mode == PowerModes.Suspend)
            {
                _ = RunAudioTestLifecycleActionAsync(
                    () => _appVm.StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Suspend),
                    "audio-test-suspend-stop");
            }

            _startupResumeCoordinator.HandlePowerModeChanged(e, nameof(OnPowerModeChanged));
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (IsShutdownRequested() || e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff))
            {
                return;
            }

            _ = RunAudioTestLifecycleActionAsync(
                () => _appVm.StopAudioEndpointTestAsync(AudioEndpointTestStopReason.SessionLocked),
                "audio-test-session-lock-stop");
        }

        private async Task RunAudioTestLifecycleActionAsync(Func<Task> action, string operation)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (IsShutdownRequested())
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("AppRuntimeHost", () => $"{operation}-failed | error={ex.GetType().Name}", nameof(RunAudioTestLifecycleActionAsync), ex);
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger.Fatal("AppRuntimeHost", "Unhandled exception occurred", nameof(OnUnhandledException), ex);
            }

            if (e.IsTerminating)
            {
                _ = _dialogs.ShowErrorAsync("A fatal error occurred and AudioPilot must close. Please check AudioPilot.log for details.", DialogText.Captions.FatalError);
            }
        }

        private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException or TaskCanceledException)
            {
                _logger.Warning("AppRuntimeHost", "Ignoring recoverable dispatcher cancellation exception", nameof(OnDispatcherUnhandledException), e.Exception);
                e.Handled = true;
                return;
            }

            _logger.Fatal("AppRuntimeHost", () => $"fatal-dispatcher-exception | error={e.Exception.GetType().Name}", nameof(OnDispatcherUnhandledException), e.Exception);
            e.Handled = true;
            try
            {
                await _dialogs.ShowErrorAsync("A fatal error occurred and the app will close. Please check AudioPilot.log for details.", DialogText.Captions.FatalError);
            }
            catch (Exception dialogEx)
            {
                _logger.Warning(
                    "AppRuntimeHost",
                    () => $"fatal-dispatcher-dialog-failed | error={dialogEx.GetType().Name}",
                    nameof(OnDispatcherUnhandledException),
                    dialogEx);
            }

            await ObserveShutdownRequestAsync("dispatcher-fatal-error");
        }

        internal Task RequestShutdownAsync(string reason)
        {
            lock (_shutdownSync)
            {
                return _shutdownTask ??= ShutdownCoreAsync(reason);
            }
        }

        internal void BeginEmergencyShutdown()
        {
            string opId = $"emergency-shutdown:{Guid.NewGuid():N}";
            _ = TryBeginShutdownAdmissionBarrier(opId);
        }

        private void ObserveShutdownRequest(string reason)
        {
            _ = ObserveShutdownRequestAsync(reason);
        }

        private async Task ObserveShutdownRequestAsync(string reason)
        {
            try
            {
                await RequestShutdownAsync(reason);
            }
            catch (Exception ex)
            {
                LifecycleFallbackDiagnostics.Write("AppRuntimeHost", "Observed shutdown failed", nameof(ObserveShutdownRequestAsync), ex);
            }
        }

        private async Task ShutdownCoreAsync(string reason)
        {
            string opId = $"shutdown:{Guid.NewGuid():N}";
            if (!TryBeginShutdownAdmissionBarrier(opId))
            {
                return;
            }

            _logger.Info("AppRuntimeHost", () => $"runtime-shutdown-start | opId={opId} reason={reason}");

            bool allTrackedStepsDrained = true;
            AppShutdownStepResult closeWindows = await AwaitShutdownStepAsync(CloseApplicationWindowsAsync(), "close-windows", opId);
            allTrackedStepsDrained &= CanDisposeDependentResources(closeWindows);

            AppShutdownStepResult windowManager = default;
            if (CanDisposeDependentResources(closeWindows))
            {
                windowManager = await AwaitShutdownStepAsync(_windowManager.DisposeAsync().AsTask(), "dispose-window-manager", opId);
                allTrackedStepsDrained &= CanDisposeDependentResources(windowManager);
            }
            else
            {
                LogSkippedShutdownDependencies("close-windows", "dispose-window-manager,app-viewmodel-and-runtime", opId);
            }

            bool presentationDrained = CanDisposeDependentResources(closeWindows)
                && CanDisposeDependentResources(windowManager);
            AppShutdownStepResult appViewModel = default;
            bool appViewModelDrained = false;
            if (presentationDrained)
            {
                appViewModel = await AwaitShutdownStepAsync(_appVm.CleanupAsync(), "app-viewmodel-cleanup", opId);
                appViewModelDrained = CanDisposeDependentResources(appViewModel);
                allTrackedStepsDrained &= appViewModelDrained;
            }
            else
            {
                appViewModelDrained = false;
                if (CanDisposeDependentResources(closeWindows))
                {
                    LogSkippedShutdownDependencies("dispose-window-manager", "app-viewmodel-and-runtime", opId);
                }
            }

            AppShutdownStepResult runtimeServices = default;
            bool runtimeServicesDrained = false;
            if (appViewModelDrained)
            {
                TryShutdownAction(_hotkeyService.Dispose, "dispose-hotkeys", opId);
                runtimeServices = await AwaitShutdownStepAsync(_runtimeServices.DisposeAsync().AsTask(), "dispose-runtime-services", opId);
                runtimeServicesDrained = CanDisposeDependentResources(runtimeServices);
                allTrackedStepsDrained &= runtimeServicesDrained;
                TryShutdownAction(_overlayService.Dispose, "dispose-overlay", opId);

                if (runtimeServicesDrained)
                {
                    TryShutdownAction(DeviceCacheHelper.DisposeSingleton, "dispose-device-cache", opId);
                    TryShutdownAction(ComThreadingHelper.DisposeCoreAudioExecutor, "dispose-core-audio", opId);
                }
                else
                {
                    LogSkippedShutdownDependencies("dispose-runtime-services", "dispose-device-cache,dispose-core-audio", opId);
                }
            }
            else if (presentationDrained)
            {
                LogSkippedShutdownDependencies("app-viewmodel-cleanup", "dispose-hotkeys-and-runtime", opId);
            }

            TryShutdownAction(_trayService.Dispose, "dispose-tray", opId);
            AppShutdownStepResult singleInstance = await AwaitShutdownStepAsync(_singleInstance.DisposeAsync().AsTask(), "dispose-single-instance", opId);
            allTrackedStepsDrained &= CanDisposeDependentResources(singleInstance);

            WindowThemeResolver.SetApplicationThemeProvider(null);

            if (_initialized)
            {
                if (allTrackedStepsDrained)
                {
                    AppShutdownStepResult dialogs = await AwaitShutdownStepAsync(_dialogs.DisposeAsync().AsTask(), "dispose-dialogs", opId);
                    allTrackedStepsDrained &= CanDisposeDependentResources(dialogs);
                    if (CanDisposeDependentResources(dialogs))
                    {
                        AppShutdownStepResult fallbackDialogs = await AwaitShutdownStepAsync(AppDialogServiceProvider.DisposeFallbackAsync().AsTask(), "dispose-fallback-dialogs", opId);
                        allTrackedStepsDrained &= CanDisposeDependentResources(fallbackDialogs);
                    }
                    else
                    {
                        LogSkippedShutdownDependencies("dispose-dialogs", "dispose-fallback-dialogs,dispose-logger", opId);
                    }
                }

                if (!allTrackedStepsDrained)
                {
                    _logger.Warning("AppRuntimeHost", () => $"runtime-shutdown-resources-retained | opId={opId} reason=unfinished-owner");
                }
            }

            _logger.Info("AppRuntimeHost", () => $"runtime-shutdown-complete | opId={opId} allStepsDrained={allTrackedStepsDrained}");
            if (_initialized && allTrackedStepsDrained)
            {
                await AwaitShutdownStepAsync(_logger.DisposeAsync().AsTask(), "dispose-logger", opId);
            }

            // A partially initialized host is cleaned up by CreateAndInitializeAsync, then the startup
            // pipeline owns its error presentation and final exit code. Do not shut the dispatcher down
            // from underneath that caller.
            if (_initialized
                && !_application.Dispatcher.HasShutdownStarted
                && !_application.Dispatcher.HasShutdownFinished)
            {
                if (_application.Dispatcher.CheckAccess())
                {
                    _application.Shutdown();
                }
                else
                {
                    await _application.Dispatcher.InvokeAsync(_application.Shutdown, DispatcherPriority.Send);
                }
            }
        }

        private bool TryBeginShutdownAdmissionBarrier(string opId)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            {
                return false;
            }

            TryShutdownAction(_windowManager.BeginShutdown, "begin-window-shutdown", opId);
            TryShutdownAction(_trayService.BeginShutdown, "begin-tray-shutdown", opId);
            TryShutdownAction(_hotkeyBindings.Unwire, "unwire-hotkeys", opId);
            TryShutdownAction(_singleInstance.BeginShutdown, "begin-single-instance-shutdown", opId);
            TryShutdownAction(DetachEventProducers, "detach-event-producers", opId);
            TryShutdownAction(
                () => AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _hotplugRefreshDebounceCts),
                "cancel-hotplug-refresh",
                opId);
            TryShutdownAction(_startupResumeCoordinator.Dispose, "dispose-startup-resume-coordinator", opId);
            return true;
        }

        private async Task CloseApplicationWindowsAsync()
        {
            if (!_application.Dispatcher.CheckAccess())
            {
                await _application.Dispatcher.InvokeAsync(CloseApplicationWindowsOnDispatcher, DispatcherPriority.Send);
            }
            else
            {
                CloseApplicationWindowsOnDispatcher();
            }

            await _windowManager.CloseForShutdownAsync();
        }

        private void CloseApplicationWindowsOnDispatcher()
        {
            MainWindow? mainWindow = _windowManager.CurrentWindow;
            Window[] windows = [.. _application.Windows.Cast<Window>()];
            foreach (Window window in windows)
            {
                if (ReferenceEquals(window, mainWindow))
                {
                    continue;
                }

                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    _logger.Warning("AppRuntimeHost", () => $"secondary-window-close-failed | type={window.GetType().Name}", nameof(CloseApplicationWindowsOnDispatcher), ex);
                }
            }
        }

        private async Task<AppShutdownStepResult> AwaitShutdownStepAsync(Task task, string step, string opId)
        {
            AppShutdownStepResult result = await EvaluateShutdownStepAsync(
                task,
                Task.Delay(AppConstants.Timing.ShutdownStepTimeoutMs));
            if (result.Outcome == AppShutdownStepOutcome.TimedOut)
            {
                _logger.Warning("AppRuntimeHost", () => $"runtime-shutdown-step-timeout | step={step} opId={opId}");
                _ = task.ContinueWith(
                    lateTask => _logger.Warning("AppRuntimeHost", () => $"runtime-shutdown-step-late-fault | step={step} opId={opId}", nameof(AwaitShutdownStepAsync), lateTask.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            else if (result.Outcome == AppShutdownStepOutcome.Faulted)
            {
                _logger.Warning("AppRuntimeHost", () => $"runtime-shutdown-step-failed | step={step} opId={opId}", nameof(AwaitShutdownStepAsync), result.Exception);
            }

            return result;
        }

        internal static async Task<AppShutdownStepResult> EvaluateShutdownStepAsync(Task task, Task timeoutTask)
        {
            ArgumentNullException.ThrowIfNull(task);
            ArgumentNullException.ThrowIfNull(timeoutTask);

            Task completed = await Task.WhenAny(task, timeoutTask);
            if (!ReferenceEquals(completed, task))
            {
                return new AppShutdownStepResult(AppShutdownStepOutcome.TimedOut);
            }

            try
            {
                await task;
                return new AppShutdownStepResult(AppShutdownStepOutcome.Completed);
            }
            catch (Exception ex)
            {
                return new AppShutdownStepResult(AppShutdownStepOutcome.Faulted, ex);
            }
        }

        internal static bool CanDisposeDependentResources(AppShutdownStepResult ownerResult)
        {
            return ownerResult.Outcome != AppShutdownStepOutcome.TimedOut;
        }

        private void LogSkippedShutdownDependencies(string ownerStep, string skippedSteps, string opId)
        {
            _logger.Warning(
                "AppRuntimeHost",
                () => $"runtime-shutdown-dependent-steps-skipped | ownerStep={ownerStep} skippedSteps={skippedSteps} opId={opId}");
        }

        private void TryShutdownAction(Action action, string step, string opId)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Warning("AppRuntimeHost", () => $"runtime-shutdown-step-failed | step={step} opId={opId}", nameof(TryShutdownAction), ex);
            }
        }

        private void DetachEventProducers()
        {
            if (_globalEventHandlersRegistered)
            {
                _application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                _audioService.DeviceStateChanged -= OnAudioDeviceStateChanged;
                _globalEventHandlersRegistered = false;
            }

            if (_systemEventHandlersRegistered)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _inputLanguageManager?.InputLanguageChanged -= OnInputLanguageChanged;
                _inputLanguageManager = null;
                _systemEventHandlersRegistered = false;
            }
        }

        private bool IsShutdownRequested() => Volatile.Read(ref _shutdownStarted) != 0;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            if (!IsShutdownRequested())
            {
                await RequestShutdownAsync("runtime-dispose");
            }
            else if (_shutdownTask != null)
            {
                await _shutdownTask;
            }
        }
    }
}
