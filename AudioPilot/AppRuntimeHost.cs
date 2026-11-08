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
    internal sealed partial class AppRuntimeHost : IAsyncDisposable
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
        private SystemPowerNotifications? _powerNotifications;
        private readonly AppRuntimeHotkeyBindings _hotkeyBindings;
        private readonly AppViewModel _appVm;
        private readonly MicrophoneHoldService _microphoneHold;
        private readonly Lock _shutdownSync = new();
        private Task? _shutdownTask;
        private FatalErrorCoordinator? _fatalErrors;
        private bool _exceptionHandlersRegistered;
        private Task? _sessionEndingPreparationTask;
        private InputLanguageManager? _inputLanguageManager;
        private bool _systemEventHandlersRegistered;
        private bool _globalEventHandlersRegistered;
        private bool _startupShellReady;
        private Task<AppRuntimeStartupInitializationOutcome>? _startupWork;
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
            _microphoneHold = new MicrophoneHoldService(logger, feedback =>
            {
                if (!IsShutdownRequested())
                    _overlayService.Show(feedback.Muted switch
                    {
                        true => OverlayActionStateKind.Disabled,
                        false => OverlayActionStateKind.Enabled,
                        _ => OverlayActionStateKind.Neutral,
                    }, feedback.Title, feedback.Message, icon: feedback.Muted switch { true => OverlayIcon.MicrophoneMuted, false => OverlayIcon.Microphone, _ => OverlayIcon.Information });
            });
            _hotkeyBindings = CreateHotkeyBindings();
            _appVm.IsMicrophoneHoldControlling = () => _microphoneHold.IsControlling;
            _appVm.MicrophoneMuteChangeRequested += OnMicrophonePreferenceChanged;
            _appVm.PushToTalkModeChanged += OnPushToTalkModeChanged;
            _hotkeyService.CaptureRegistrationFailed += OnCaptureRegistrationFailed;
            _hotkeyService.CaptureRegistrationCompleted += OnCaptureRegistrationCompleted;
        }

        internal AppViewModel AppViewModel => _appVm;
        internal IAppMainWindowManager WindowManager => _windowManager;

        internal static async Task<AppRuntimeHost> CreateAndInitializeAsync(
            Application application,
            SingleInstanceHelper singleInstance,
            IAppDialogService dialogs,
            Logger logger,
            Action<AppRuntimeHost> publishHost,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(application);
            ArgumentNullException.ThrowIfNull(singleInstance);
            ArgumentNullException.ThrowIfNull(dialogs);

            AppRuntimeHost? host = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                host = await ComposeAsync(application, singleInstance, dialogs, logger);
                publishHost(host);
                cancellationToken.ThrowIfCancellationRequested();
                await host.InitializeAsync();
                cancellationToken.ThrowIfCancellationRequested();
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
                DeviceCacheHelper deviceCache = runtimeServices.InitializeDeviceCache();

                windowManager = new AppMainWindowManager(application, dialogs, logger,
                    new MainWindowPlacementService(AppDataPaths.GetWindowPlacementPath(), logger));
                trayService = new AppTrayIconService(application, dialogs, logger);
                AppShellService shell = new(windowManager, trayService);
                overlayService = new OverlayService(initiallyEnabled: false);
                mediaOverlayCommands = new MediaOverlayCommandService();
                MainWindowVisibilityCoordinator visibilityCoordinator = new(logger);

                AppViewModel? appViewModelReference = null;
                AppAudioCommandCoordinator audioCommandCoordinator = new(
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
                    audioCommandCoordinator: audioCommandCoordinator,
                    switchCoordinator: switchCoordinator,
                    shell: shell,
                    mixerFactory: () => new MixerViewModel(audioService, application.Dispatcher, AudioMixerMode.Output, logger, deviceCache),
                    inputMixerFactory: () => new MixerViewModel(audioService, application.Dispatcher, AudioMixerMode.Input, logger, deviceCache),
                    deviceCache: deviceCache,
                    overlay: overlayService,
                    dispatcher: application.Dispatcher,
                    routineBluetoothReconnectCoordinator: runtimeServices.BluetoothReconnectCoordinator.Value,
                    dialogService: dialogs);

                AppStartupCoordinator startupCoordinator = new(
                    appViewModel,
                    hotkeyService,
                    dialogs,
                    onStartHiddenToTray: visibilityCoordinator.MarkPendingAutoScrollOnNextShow,
                    updateGate: Services.Updates.AppUpdateLifecycleSession.Current);
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
                    message => Services.Updates.AppUpdateLifecycleSession.Current != null ? Task.CompletedTask
                        : dialogs.ShowErrorAsync(message, DialogText.Captions.StartupError, cancellationToken: appViewModel.ShutdownToken),
                    () => hostReference!.PublishStartupShellAsync(),
                    appViewModel.ShutdownToken);

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
                        () => host.RequestShutdownAsync("main-window-close")))
                { IsEnabled = !host.IsUpdateAdmissionBlocked });
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

        /// <summary>
        /// Initializes hotkeys and themed tray resources before publishing the tray icon.
        /// Theme resources are reapplied after settings load because a tray-only launch has no main window to initialize them.
        /// </summary>
        private async Task InitializeAsync()
        {
            long started = Stopwatch.GetTimestamp();
            _hotkeyService.InitializeInfrastructure();
            _hotkeyBindings.Wire();
            RegisterGlobalEventHandlers();
            RegisterSystemEventHandlers();

            WindowThemeResolver.SetApplicationThemeProvider(() => _appVm.Theme);
            WindowThemeResolver.ApplyApplicationTheme(_appVm.Theme);
            _ = MediaKeyHelper.PrewarmSystemMediaCommandsAsync();

            _startupWork = _startupResumeCoordinator.InitializeAsync(nameof(InitializeAsync));
            AppRuntimeStartupInitializationOutcome startupOutcome = await _startupWork;
            _appVm.ShutdownToken.ThrowIfCancellationRequested();
            EnsureStartupSucceeded(startupOutcome);
            if (Services.Updates.AppUpdateLifecycleSession.Current?.Restart == true)
                OnPushToTalkModeChanged(_appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled ?? false);
            _trayService.SetUpdateActionsSuspended(false);
            if (_windowManager.CurrentWindow is { } initializedWindow) initializedWindow.IsEnabled = true;
            _appVm.Updates.Start();
            _logger.Info(
                "AppRuntimeHost",
                () => $"runtime-host-ready | trayReady={_trayService.IsReady} mainWindowCreated={_windowManager.IsCreated} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }

        private async Task PublishStartupShellAsync()
        {
            CancellationToken token = _appVm.ShutdownToken;
            token.ThrowIfCancellationRequested();
            WindowThemeResolver.ApplyApplicationTheme(_appVm.Theme);
            _overlayService.PrepareForFirstUse();
            bool trayReady = await AppTrayStartupCoordinator.EnsureVisibleAsync(
                () => _trayService.EnsureVisible(logFailure: false), _logger, token);
            _trayService.SetUpdateActionsSuspended(_appVm.IsUpdateAdmissionBlocked);
            await AppTrayStartupCoordinator.EnsureWindowFallbackAsync(trayReady, _windowManager, _logger, token);
            token.ThrowIfCancellationRequested();

            _startupShellReady = true;
            if (Services.Updates.AppUpdateLifecycleSession.Current?.StartupBlocked == true && _runtimeServices.SettingsService.LastLoadUserWarning != null)
                throw new InvalidOperationException("A restart candidate requires settings recovery; validation cannot be committed.");
            if (Services.Updates.AppUpdateLifecycleSession.Current?.StartupBlocked == true && _appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled == true)
                throw new InvalidOperationException("A restart candidate cannot acquire push-to-talk ownership before commit.");
            _appVm.ReportHotkeyAvailability();
            OnPushToTalkModeChanged(_appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled ?? false);
            if (trayReady) _trayService.SchedulePresentationPrewarm();
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
                onShowAudioStatus: () => RunBackgroundHotkeyAction(_appVm.ShowAudioStatusFromHotkey, "Show audio status hotkey action error"),
                onMediaShowCurrentTrack: () => RunBackgroundHotkeyAction(_appVm.ShowCurrentTrackFromCli, "Show current track hotkey action error"),
                onMediaPlayPause: () => RunBackgroundHotkeyAction(_appVm.MediaPlayPauseFromHotkey, "Play/pause hotkey action error"),
                onMediaNextTrack: () => RunBackgroundHotkeyAction(_appVm.MediaNextTrackFromHotkey, "Next track hotkey action error"),
                onMediaPreviousTrack: () => RunBackgroundHotkeyAction(_appVm.MediaPreviousTrackFromHotkey, "Previous track hotkey action error"),
                onMediaSeekForward: () => RunBackgroundHotkeyAction(_appVm.MediaSeekForwardFromHotkey, "Seek forward hotkey action error"),
                onMediaSeekBackward: () => RunBackgroundHotkeyAction(_appVm.MediaSeekBackwardFromHotkey, "Seek backward hotkey action error"),
                onMuteMic: () => ToggleFlagWithOverlay(
                    () => _appVm.MuteMic = !_appVm.MuteMic,
                    () => _appVm.Deafen || _appVm.MuteMic,
                    "Microphone muted",
                    "Microphone unmuted"),
                onMuteSound: () => ToggleFlagWithOverlay(
                    () => _appVm.MuteSound = !_appVm.MuteSound,
                    () => _appVm.Deafen || _appVm.MuteSound,
                    "Sound muted",
                    "Sound unmuted"),
                onDeafen: () => ToggleFlagWithOverlay(
                    () => _appVm.Deafen = !_appVm.Deafen,
                    () => _appVm.Deafen,
                    "Deafened",
                    "Undeafened"),
                onListenToInput: () => RunBackgroundHotkeyAction(() => _ = _appVm.ToggleListenToInputFromCli(), "Listen hotkey action error"),
                onMasterVolumeUp: () => RunBackgroundHotkeyAction(() => _appVm.StepEndpointVolumeFromHotkey(playback: true, increase: true), "Master volume up hotkey action error"),
                onMasterVolumeDown: () => RunBackgroundHotkeyAction(() => _appVm.StepEndpointVolumeFromHotkey(playback: true, increase: false), "Master volume down hotkey action error"),
                onMicVolumeUp: () => RunBackgroundHotkeyAction(() => _appVm.StepEndpointVolumeFromHotkey(playback: false, increase: true), "Microphone volume up hotkey action error"),
                onMicVolumeDown: () => RunBackgroundHotkeyAction(() => _appVm.StepEndpointVolumeFromHotkey(playback: false, increase: false), "Microphone volume down hotkey action error"),
                onInputSwitch: () => RunCycleHotkey(output: false, reverse: false),
                onOutputSwitch: () => RunCycleHotkey(output: true, reverse: false),
                onInputReverseSwitch: () => RunCycleHotkey(output: false, reverse: true),
                onOutputReverseSwitch: () => RunCycleHotkey(output: true, reverse: true),
                onForegroundVolumeUp: () => _appVm.ChangeForegroundAudioFromHotkey(1),
                onForegroundVolumeDown: () => _appVm.ChangeForegroundAudioFromHotkey(-1),
                onForegroundMute: () => _appVm.ChangeForegroundAudioFromHotkey(0),
                onMicrophoneHold: (muted, held) =>
                {
                    if (!IsUserWorkBlocked() && (muted || !_appVm.Deafen))
                        _microphoneHold.Begin(muted, () => held() && !IsUserWorkBlocked() && (muted || !_appVm.Deafen));
                },
                onMicrophoneHoldCancelled: () => _microphoneHold.Cancel(),
                onQuickDevicePicker: () => DispatchUiHotkeyAction(_appVm.ToggleQuickDevicePicker));
        }

        private void OnMicrophonePreferenceChanged(bool muted)
        {
            _microphoneHold.Cancel(abandonRestoration: true, mutePreference: muted);
        }

        private void OnCaptureRegistrationCompleted()
        {
            _appVm.ReportHotkeyAvailability();
            if (_appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled == true &&
                _hotkeyService.GetLastRegistrationOutcome(AppConstants.Hotkeys.PushToTalkHotkeyId).Kind
                    is HotkeyRegistrationOutcomeKind.Registered or HotkeyRegistrationOutcomeKind.Fallback)
                OnPushToTalkModeChanged(true);
        }

        private void OnCaptureRegistrationFailed()
        {
            DispatchUiHotkeyAction(() =>
            {
                if (_appVm.CurrentSettings?.Hotkeys.Mute.PushToTalkEnabled == true &&
                    _hotkeyService.GetLastRegistrationOutcome(AppConstants.Hotkeys.PushToTalkHotkeyId).Kind
                        is not (HotkeyRegistrationOutcomeKind.Registered or HotkeyRegistrationOutcomeKind.Fallback))
                {
                    OnPushToTalkModeChanged(true);
                    return;
                }
                _overlayService.Show("Shortcut unavailable", "Another app may be using it. Check your hotkey settings.");
            }, guardCapture: false);
        }

        private void OnPushToTalkModeChanged(bool enabled)
        {
            if (!_startupShellReady || IsUserWorkBlocked()) return;
            bool registered = _hotkeyService.GetLastRegistrationOutcome(AppConstants.Hotkeys.PushToTalkHotkeyId).Kind
                is HotkeyRegistrationOutcomeKind.Registered or HotkeyRegistrationOutcomeKind.Fallback;
            _microphoneHold.SetPushToTalkEnabled(enabled && registered);
            if (enabled && !registered)
            {
                _logger.Warning("MicrophoneHold", "push-to-talk-unavailable | reason=hotkey-not-registered");
                _overlayService.Show("Push-to-talk unavailable - check its hotkey");
            }
        }

        private void DispatchUiHotkeyAction(Action action, bool guardCapture = true)
        {
            if (IsUserWorkBlocked())
            {
                return;
            }

            long generation = _hotkeyService.CaptureGeneration;
            AppDispatcherHelper.Dispatch(
                _application.Dispatcher,
                _logger,
                () =>
                {
                    if (!IsUserWorkBlocked() && (!guardCapture || _hotkeyService.CanDispatch(generation))) action();
                },
                "Hotkey action error",
                nameof(DispatchUiHotkeyAction));
        }

        private void DispatchUiHotkeyActionAsync(Func<Task> action, string errorMessage)
        {
            if (IsUserWorkBlocked())
            {
                return;
            }

            long generation = _hotkeyService.CaptureGeneration;
            AppDispatcherHelper.DispatchAsync(
                _application.Dispatcher,
                _logger,
                async () =>
                {
                    if (!IsUserWorkBlocked() && _hotkeyService.CanDispatch(generation))
                    {
                        await action();
                    }
                },
                errorMessage,
                nameof(DispatchUiHotkeyActionAsync));
        }

        private void RunBackgroundHotkeyAction(Action action, string errorMessage)
        {
            if (IsUserWorkBlocked())
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
            if (IsUserWorkBlocked())
            {
                return;
            }

            long generation = _hotkeyService.CaptureGeneration;
            DispatchUiHotkeyActionAsync(async () =>
            {
                var result = await _appVm.ChangeMuteStateAsync(toggle);
                if (IsUserWorkBlocked() || !_hotkeyService.CanDispatch(generation) || !result.IsCurrent
                    || result.Revision != _appVm.MuteIntentRevision) return;
                if (!result.Succeeded)
                {
                    _overlayService.Show(result.PartiallySucceeded ? "Mute change partially applied" : "Could not change mute state",
                        "Check the device states and try again.");
                    return;
                }
                bool enabled = readState();
                _overlayService.Show(
                    enabled ? OverlayActionStateKind.Disabled : OverlayActionStateKind.Enabled,
                    enabled ? enabledMessage : disabledMessage);
            }, "Mute hotkey action error");
        }

        private void RunCycleHotkey(bool output, bool reverse)
        {
            if (IsUserWorkBlocked())
            {
                return;
            }

            long generation = _hotkeyService.CaptureGeneration;
            _ = AppDispatcherHelper.ExecuteAsync(
                () => _appVm.SwitchCycleFromHotkeyAsync(output, reverse,
                    () => !IsUserWorkBlocked() && _hotkeyService.CanDispatch(generation)),
                _logger,
                _application.Dispatcher,
                _dialogs,
                "Device cycle hotkey handler error",
                output ? "Error switching output devices." : "Error switching input devices.",
                nameof(RunCycleHotkey));
        }

        private void RegisterGlobalEventHandlers()
        {
            if (!_exceptionHandlersRegistered)
            {
                _application.DispatcherUnhandledException += OnDispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                _exceptionHandlersRegistered = true;
            }
            if (_globalEventHandlersRegistered)
            {
                return;
            }

            _audioService.DeviceStateChanged += OnAudioDeviceStateChanged;
            _audioService.DefaultMicrophoneRolesChanged += _microphoneHold.RefreshEndpoints;
            _globalEventHandlersRegistered = true;
        }

        private void RegisterSystemEventHandlers()
        {
            if (_systemEventHandlersRegistered)
            {
                return;
            }

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            try { _powerNotifications = new(OnPowerModeChanged); }
            catch (Exception ex)
            {
                _logger.Warning("AppRuntimeHost", "native-power-notifications-unavailable | fallback=managed", nameof(RegisterSystemEventHandlers), ex);
                SystemEvents.PowerModeChanged += OnManagedPowerModeChanged;
            }
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

            _microphoneHold.RefreshEndpoints();
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

            _ = AppDebouncedBackgroundWorkCoordinator.ExecuteAsync(
                debounceCts,
                owned => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _hotplugRefreshDebounceCts, owned),
                debounceToken => AppDispatcherHelper.InvokeAsync(
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
                        debounceToken),
                    "Failed to schedule hotplug refresh",
                    nameof(OnAudioDeviceStateChanged)),
                CancellationToken.None);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (IsShutdownRequested())
            {
                return;
            }

            bool appearanceChanged = e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle;
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
                WindowThemeResolver.ApplyApplicationTheme(_appVm.Theme);
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

        private void OnManagedPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => OnPowerModeChanged(e.Mode);

        private void OnPowerModeChanged(PowerModes mode)
        {
            if (IsShutdownRequested()) return;
            _startupResumeCoordinator.HandlePowerModeChanged(mode, nameof(OnPowerModeChanged),
                onSuspend: CancelInteractiveAudioForSuspend, onResume: _microphoneHold.RefreshEndpoints);
        }

        private void CancelInteractiveAudioForSuspend()
        {
            _microphoneHold.Cancel();
            _ = RunAudioTestLifecycleActionAsync(
                () => _appVm.StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Suspend),
                "audio-test-suspend-stop");
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (IsShutdownRequested()) return;
            if (e.Reason == SessionSwitchReason.SessionUnlock) _appVm.RoutineSystemEvents.Unlock();
            if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff))
            {
                return;
            }

            _appVm.RoutineSystemEvents.LockSession();
            _microphoneHold.Cancel();
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
            if (!e.IsTerminating)
            {
                try { _logger.Fatal("AppRuntimeHost", "Unhandled exception occurred", nameof(OnUnhandledException), e.ExceptionObject as Exception); }
                catch (Exception) { }
                return;
            }
            Interlocked.Exchange(ref _shutdownStarted, 1);
            // A terminating thread may hold service or dispatcher locks; only the reporting worker may wait for I/O.
            try
            {
                Task<FatalLogWriteResult> reporting = _logger.ReportFatalAsync("AppRuntimeHost", "fatal-process-exception",
                    e.ExceptionObject as Exception ?? new InvalidOperationException("Unhandled non-exception failure."));
                _ = reporting.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception) { }
        }

        private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException or TaskCanceledException)
            {
                _logger.Warning("AppRuntimeHost", "Ignoring recoverable dispatcher cancellation exception", nameof(OnDispatcherUnhandledException), e.Exception);
                e.Handled = true;
                return;
            }

            e.Handled = true;
            _fatalErrors ??= new FatalErrorCoordinator(
                async () =>
                {
                    string opId = $"fatal-error:{Guid.NewGuid():N}";
                    _ = TryBeginShutdownAdmissionBarrier(opId);
                    await AwaitShutdownStepAsync(_microphoneHold.DisposeAsync().AsTask(), "microphone-hold-restore", opId);
                },
                error => _logger.ReportFatalAsync("AppRuntimeHost", "fatal-dispatcher-exception", error),
                message => _dialogs.ShowErrorAsync(message, DialogText.Captions.FatalError),
                () => RequestShutdownAsync("dispatcher-fatal-error"),
                ReportFatalHandlingFailure);
            try
            {
                await _fatalErrors.HandleAsync(e.Exception);
            }
            catch (Exception ex)
            {
                ReportFatalHandlingFailure("cleanup", ex);
                if (!AppDispatcherHelper.IsDispatcherUnavailable(_application.Dispatcher))
                {
                    try { _application.Shutdown(); }
                    catch (Exception shutdownError) { ReportFatalHandlingFailure("exit", shutdownError); }
                }
            }
        }

        private void ReportFatalHandlingFailure(string stage, Exception exception)
        {
            try { _logger.Warning("AppRuntimeHost", $"fatal-error-handling-failed | stage={stage}", exception: exception); }
            catch (Exception) { }
        }

        internal Task RequestShutdownAsync(string reason, bool exitApplication = true)
        {
            TaskCompletionSource completion;
            lock (_shutdownSync)
            {
                if (_shutdownTask != null) return _shutdownTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownTask = completion.Task;
            }
            _ = CompleteShutdownAsync(reason, exitApplication, completion);
            return completion.Task;
        }

        private async Task CompleteShutdownAsync(string reason, bool exitApplication, TaskCompletionSource completion)
        {
            try { await ShutdownCoreAsync(reason, exitApplication); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }

        internal void BeginEmergencyShutdown()
        {
            string opId = $"emergency-shutdown:{Guid.NewGuid():N}";
            _ = TryBeginShutdownAdmissionBarrier(opId);
        }

        internal Task PrepareForSessionEndingAsync()
        {
            lock (_shutdownSync) return _sessionEndingPreparationTask ??= PrepareForSessionEndingCoreAsync();
        }

        private async Task PrepareForSessionEndingCoreAsync()
        {
            string opId = $"session-ending:{Guid.NewGuid():N}";
            _ = TryBeginShutdownAdmissionBarrier(opId);
            // These independent priorities share the session-ending budget. A slow
            // settings write must not delay restoration of an active microphone lease.
            Task microphone = _microphoneHold.DisposeAsync().AsTask();
            Task settings = _appVm.FlushPendingAutoSaveForShutdownAsync(opId);
            await Task.WhenAll(microphone, settings);
        }

        /// <summary>
        /// Drains runtime work before releasing its dependencies. During failed startup, the caller retains ownership
        /// of dispatcher shutdown and the final exit code so error presentation can complete.
        /// </summary>
        private async Task ShutdownCoreAsync(string reason, bool exitApplication)
        {
            string opId = $"shutdown:{Guid.NewGuid():N}";
            _ = TryBeginShutdownAdmissionBarrier(opId);

            _logger.Info("AppRuntimeHost", () => $"runtime-shutdown-start | opId={opId} reason={reason}");

            _appVm.MicrophoneMuteChangeRequested -= OnMicrophonePreferenceChanged;
            _appVm.PushToTalkModeChanged -= OnPushToTalkModeChanged;
            _hotkeyService.CaptureRegistrationFailed -= OnCaptureRegistrationFailed;
            _hotkeyService.CaptureRegistrationCompleted -= OnCaptureRegistrationCompleted;
            AppShutdownStepResult microphoneHold = await AwaitShutdownStepAsync(_microphoneHold.DisposeAsync().AsTask(), "microphone-hold-restore", opId);
            bool microphoneHoldDrained = CanDisposeDependentResources(microphoneHold);
            if (microphoneHoldDrained) _appVm.IsMicrophoneHoldControlling = null;
            bool allTrackedStepsDrained = microphoneHoldDrained;
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

            Task<AppShutdownStepResult> finalAutoSave = AwaitShutdownStepAsync(
                _appVm.FlushPendingAutoSaveForShutdownAsync(opId), "flush-final-autosave", opId);
            bool startupDrained = _startupWork == null || CanDisposeDependentResources(
                await AwaitShutdownStepAsync(_startupWork, "drain-startup", opId));
            bool resumeDrained = CanDisposeDependentResources(
                await AwaitShutdownStepAsync(_startupResumeCoordinator.DrainResumeAsync(), "drain-resume", opId));
            bool autoSaveDrained = CanDisposeDependentResources(await finalAutoSave)
                && (!_updatePrepared || !_appVm.HasPendingUpdateEdits);
            allTrackedStepsDrained &= startupDrained && resumeDrained && autoSaveDrained;
            bool presentationDrained = CanDisposeDependentResources(closeWindows)
                && CanDisposeDependentResources(windowManager);
            AppShutdownStepResult appViewModel = default;
            bool appViewModelDrained = false;
            if (presentationDrained && startupDrained && resumeDrained && autoSaveDrained)
            {
                appViewModel = await AwaitShutdownStepAsync(_appVm.CleanupAsync(), "app-viewmodel-cleanup", opId);
                appViewModelDrained = CanDisposeDependentResources(appViewModel);
                allTrackedStepsDrained &= appViewModelDrained;
            }
            else
            {
                if (presentationDrained)
                    LogSkippedShutdownDependencies(!startupDrained ? "drain-startup" : !resumeDrained ? "drain-resume" : "flush-final-autosave", "app-viewmodel-and-runtime", opId);
                else if (CanDisposeDependentResources(closeWindows))
                    LogSkippedShutdownDependencies("dispose-window-manager", "app-viewmodel-and-runtime", opId);
            }

            AppShutdownStepResult runtimeServices = default;
            bool runtimeServicesDrained = false;
            if (appViewModelDrained)
            {
                TryShutdownAction(_hotkeyService.Dispose, "dispose-hotkeys", opId);
                runtimeServices = await AwaitShutdownStepAsync(_runtimeServices.DisposeAsync(releasePlatformResources: microphoneHoldDrained).AsTask(), "dispose-runtime-services", opId);
                runtimeServicesDrained = CanDisposeDependentResources(runtimeServices);
                allTrackedStepsDrained &= runtimeServicesDrained;
                TryShutdownAction(_overlayService.Dispose, "dispose-overlay", opId);

                if (!runtimeServicesDrained || !microphoneHoldDrained)
                {
                    LogSkippedShutdownDependencies("dispose-runtime-services", "dispose-device-cache,dispose-core-audio", opId);
                }
            }
            else if (presentationDrained && startupDrained && resumeDrained && autoSaveDrained)
            {
                LogSkippedShutdownDependencies("app-viewmodel-cleanup", "dispose-hotkeys-and-runtime", opId);
            }

            TryShutdownAction(_trayService.Dispose, "dispose-tray", opId);
            AppShutdownStepResult singleInstance = await AwaitShutdownStepAsync(_singleInstance.DisposeAsync().AsTask(), "dispose-single-instance", opId);
            allTrackedStepsDrained &= CanDisposeDependentResources(singleInstance);

            WindowThemeResolver.SetApplicationThemeProvider(null);

            if (exitApplication)
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
            if (exitApplication && allTrackedStepsDrained)
            {
                AppShutdownStepResult loggerDrain = await AwaitShutdownStepAsync(_logger.DisposeAsync().AsTask(), "dispose-logger", opId);
                allTrackedStepsDrained &= CanDisposeDependentResources(loggerDrain);
            }
            if (allTrackedStepsDrained) DetachExceptionHandlers();
            _lastShutdownDrained = allTrackedStepsDrained;

            if (exitApplication
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

            TryShutdownAction(_startupResumeCoordinator.Dispose, "dispose-startup-resume-coordinator", opId);
            TryShutdownAction(_appVm.BeginRuntimeShutdown, "begin-shell-command-shutdown", opId);
            TryShutdownAction(_windowManager.BeginShutdown, "begin-window-shutdown", opId);
            TryShutdownAction(_trayService.BeginShutdown, "begin-tray-shutdown", opId);
            TryShutdownAction(_hotkeyBindings.Unwire, "unwire-hotkeys", opId);
            TryShutdownAction(_singleInstance.BeginShutdown, "begin-single-instance-shutdown", opId);
            TryShutdownAction(DetachEventProducers, "detach-event-producers", opId);
            TryShutdownAction(
                () => AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _hotplugRefreshDebounceCts),
                "cancel-hotplug-refresh",
                opId);
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
            return ownerResult.Outcome == AppShutdownStepOutcome.Completed;
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
                _audioService.DeviceStateChanged -= OnAudioDeviceStateChanged;
                _audioService.DefaultMicrophoneRolesChanged -= _microphoneHold.RefreshEndpoints;
                _globalEventHandlersRegistered = false;
            }

            if (_systemEventHandlersRegistered)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (_powerNotifications is { } powerNotifications)
                {
                    _powerNotifications = null;
                    powerNotifications.Dispose();
                }
                else SystemEvents.PowerModeChanged -= OnManagedPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _inputLanguageManager?.InputLanguageChanged -= OnInputLanguageChanged;
                _inputLanguageManager = null;
                _systemEventHandlersRegistered = false;
            }
        }

        private void DetachExceptionHandlers()
        {
            if (!_exceptionHandlersRegistered) return;
            _application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            _exceptionHandlersRegistered = false;
        }

        private bool IsShutdownRequested() => Volatile.Read(ref _shutdownStarted) != 0;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            if (_shutdownTask is { } shutdown)
                await shutdown;
            else if (!IsShutdownRequested())
                await RequestShutdownAsync("runtime-dispose", exitApplication: false);
        }
    }
}
