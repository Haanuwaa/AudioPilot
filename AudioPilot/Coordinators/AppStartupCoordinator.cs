using System.Diagnostics;
using System.Text;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal interface IStartupViewModel
    {
        Task InitializeAsync(bool noSettingsFileExists);
        Settings? CurrentSettings { get; }
        IReadOnlyList<SettingsDiagnostic> GetConfigurationWarningDiagnosticsForUi();
        string? GetConfigurationLoadWarningForUi();
        void EnableRoutineAppStartMonitoring();
        Task ExecuteAudioPilotStartupRoutinesAsync(bool showOverlay);
        bool HasInteractiveShowRequest { get; }
        void MarkStartupVisibilityResolved();
        Task<bool> ShowWindowAsync();
        Task<bool> StartHiddenToTrayAsync();
        void MinimizeWindow();
    }

    internal interface IStartupHotkeyRegistrar
    {
        bool RegisterToggleAppVisibilityHotkey(string? hotkey);
        bool RegisterShowAudioStatusHotkey(string? hotkey);
        bool RegisterMediaHotkeys(string? showCurrent, string? playPause, string? nextTrack, string? previousTrack, string? seekForward = null, string? seekBackward = null);
        bool RegisterMuteHotkeys(string? muteMic, string? muteSound, string? deafen, string? pushToTalk, string? holdToMute);
        bool RegisterListenToInputHotkey(string? hotkey);
        bool RegisterVolumeStepHotkeys(string? masterUp, string? masterDown, string? micUp, string? micDown, string? foregroundUp, string? foregroundDown, string? foregroundMute);
        bool RegisterOutputSwitchHotkey(string? hotkey);
        bool RegisterInputSwitchHotkey(string? hotkey);
        bool RegisterOutputReverseSwitchHotkey(string? hotkey);
        bool RegisterInputReverseSwitchHotkey(string? hotkey);
    }

    internal interface IStartupHotkeyRegistrationLoggingScope
    {
        IDisposable SuppressVerboseRegistrationLogs();
    }

    public sealed class AppStartupCoordinator
    {
        private sealed class AppViewModelAdapter(AppViewModel appVm) : IStartupViewModel
        {
            private readonly AppViewModel _appVm = appVm;

            public Task InitializeAsync(bool noSettingsFileExists) => _appVm.InitializeAsync(noSettingsFileExists);
            public Settings? CurrentSettings => _appVm.CurrentSettings;
            public IReadOnlyList<SettingsDiagnostic> GetConfigurationWarningDiagnosticsForUi() => _appVm.GetConfigurationWarningDiagnosticsForUi();
            public string? GetConfigurationLoadWarningForUi() => _appVm.GetConfigurationLoadWarningForUi();
            public void EnableRoutineAppStartMonitoring() => _appVm.EnableRoutineAppStartMonitoring();
            public Task ExecuteAudioPilotStartupRoutinesAsync(bool showOverlay) => _appVm.ExecuteAudioPilotStartupRoutinesAsync(showOverlay);
            public bool HasInteractiveShowRequest => _appVm.HasInteractiveShowRequest;
            public void MarkStartupVisibilityResolved() => _appVm.MarkStartupVisibilityResolved();
            public Task<bool> ShowWindowAsync() => _appVm.ShowWindowAsync();
            public Task<bool> StartHiddenToTrayAsync() => _appVm.StartHiddenToTrayAsync();
            public void MinimizeWindow() => _appVm.MinimizeWindow();
        }

        private sealed class HotkeyServiceAdapter(HotkeyService hotkeyService) : IStartupHotkeyRegistrar, IStartupHotkeyRegistrationLoggingScope
        {
            private readonly HotkeyService _hotkeyService = hotkeyService;

            public bool RegisterToggleAppVisibilityHotkey(string? hotkey) => _hotkeyService.RegisterToggleAppVisibilityHotkey(hotkey);
            public bool RegisterShowAudioStatusHotkey(string? hotkey) => _hotkeyService.RegisterShowAudioStatusHotkey(hotkey);
            public bool RegisterMediaHotkeys(string? showCurrent, string? playPause, string? nextTrack, string? previousTrack, string? seekForward = null, string? seekBackward = null) => _hotkeyService.RegisterMediaHotkeys(showCurrent, playPause, nextTrack, previousTrack, seekForward, seekBackward);
            public bool RegisterMuteHotkeys(string? muteMic, string? muteSound, string? deafen, string? pushToTalk, string? holdToMute) => _hotkeyService.RegisterMuteHotkeys(muteMic, muteSound, deafen, pushToTalk, holdToMute);
            public bool RegisterListenToInputHotkey(string? hotkey) => _hotkeyService.RegisterListenToInputHotkey(hotkey);
            public bool RegisterVolumeStepHotkeys(string? masterUp, string? masterDown, string? micUp, string? micDown, string? foregroundUp, string? foregroundDown, string? foregroundMute) => _hotkeyService.RegisterVolumeStepHotkeys(masterUp, masterDown, micUp, micDown, foregroundUp, foregroundDown, foregroundMute);
            public bool RegisterOutputSwitchHotkey(string? hotkey) => _hotkeyService.RegisterOutputSwitchHotkey(hotkey);
            public bool RegisterInputSwitchHotkey(string? hotkey) => _hotkeyService.RegisterInputSwitchHotkey(hotkey);
            public bool RegisterOutputReverseSwitchHotkey(string? hotkey) => _hotkeyService.RegisterOutputReverseSwitchHotkey(hotkey);
            public bool RegisterInputReverseSwitchHotkey(string? hotkey) => _hotkeyService.RegisterInputReverseSwitchHotkey(hotkey);
            public IDisposable SuppressVerboseRegistrationLogs() => _hotkeyService.SuppressVerboseRegistrationLogs();
        }

        private readonly IStartupViewModel _appVm;
        private readonly IStartupHotkeyRegistrar _hotkeyService;
        private readonly Logger _logger;
        private readonly Func<string, string, Task<AppDialogResult>> _showWarning;
        private readonly Action _onStartHiddenToTray;

        internal AppStartupCoordinator(
            AppViewModel appVm,
            HotkeyService hotkeyService,
            IAppDialogService dialogs,
            Action? onStartHiddenToTray = null)
            : this(
                new AppViewModelAdapter(appVm),
                new HotkeyServiceAdapter(hotkeyService),
                Logger.Instance,
                showWarning: (message, caption) => dialogs.ShowWarningAsync(message, caption),
                onStartHiddenToTray: onStartHiddenToTray)
        {
        }

        internal AppStartupCoordinator(
            IStartupViewModel appVm,
            IStartupHotkeyRegistrar hotkeyService,
            Logger logger,
            Func<string, string, Task<AppDialogResult>>? showWarning = null,
            Action? onStartHiddenToTray = null)
        {
            _appVm = appVm;
            _hotkeyService = hotkeyService;
            _logger = logger;
            _showWarning = showWarning ?? (static (_, _) => Task.FromResult(AppDialogResult.Acknowledged));
            _onStartHiddenToTray = onStartHiddenToTray ?? (() => { });
        }

        /// <summary>
        /// Performs startup initialization, then decides whether to show window or minimize to tray.
        /// </summary>
        /// <remarks>
        /// Unconfigured setups are shown to guide first-time configuration, while configured setups default to tray
        /// flow for day-to-day usage.
        /// </remarks>
        public async Task InitializeAsync(bool noSettingsFileExists)
        {
            string startupOpId = $"startup:{Guid.NewGuid():N}";
            var startupStopwatch = Stopwatch.StartNew();
            _logger.Info("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.Start} | opId={startupOpId} noSettingsFileExists={noSettingsFileExists}");
            await _appVm.InitializeAsync(noSettingsFileExists);
            double appVmInitMs = startupStopwatch.Elapsed.TotalMilliseconds;

            var settings = _appVm.CurrentSettings;
            if (settings == null)
            {
                _logger.Warning("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.SettingsUnavailable} | opId={startupOpId} action=show-window");
                _ = await _appVm.ShowWindowAsync();
                return;
            }

            using IDisposable? verboseRegistrationLogScope = (_hotkeyService as IStartupHotkeyRegistrationLoggingScope)?.SuppressVerboseRegistrationLogs();
            RegisterHotkeys(settings, startupOpId);
            _appVm.EnableRoutineAppStartMonitoring();
            double hotkeyRegistrationMs = startupStopwatch.Elapsed.TotalMilliseconds - appVmInitMs;

            IReadOnlyList<string> warnings = BuildStartupWarningMessages(
                settings,
                _appVm.GetConfigurationWarningDiagnosticsForUi(),
                _appVm.GetConfigurationLoadWarningForUi(),
                out int suppressedWarningCount);
            if (warnings.Count > 0 && !noSettingsFileExists)
            {
                var warningBuilder = new StringBuilder();
                warningBuilder.Append("Some settings need attention:\n\n");
                for (int index = 0; index < warnings.Count; index++)
                {
                    if (index > 0)
                    {
                        warningBuilder.Append('\n');
                    }

                    warningBuilder.Append("- ");
                    warningBuilder.Append(warnings[index]);
                }

                string warningMessage = warningBuilder.ToString();

                await _showWarning(warningMessage, DialogText.Captions.SettingsWarnings);
                _logger.Warning("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.SettingsWarnings} | opId={startupOpId} count={warnings.Count} suppressed={suppressedWarningCount}");
            }
            else if (suppressedWarningCount > 0 && !noSettingsFileExists)
            {
                _logger.Debug("AppStartupCoordinator", () => $"startup-settings-warnings-suppressed | opId={startupOpId} count={suppressedWarningCount} reason=disconnected-device-startup-warnings-disabled");
            }

            bool configured =
                (settings.DeviceSwitching.Output.HotkeysEnabled &&
                    (!string.IsNullOrEmpty(settings.DeviceSwitching.Output.SwitchHotkey) ||
                     !string.IsNullOrEmpty(settings.DeviceSwitching.Output.ReverseSwitchHotkey))) ||
                (settings.DeviceSwitching.Input.HotkeysEnabled &&
                    (!string.IsNullOrEmpty(settings.DeviceSwitching.Input.SwitchHotkey) ||
                     !string.IsNullOrEmpty(settings.DeviceSwitching.Input.ReverseSwitchHotkey))) ||
                settings.Routines.Items.Any(static routine => routine.Enabled) ||
                settings.DeviceSwitching.Output.CycleDevices.Count > 0 ||
                settings.DeviceSwitching.Input.CycleDevices.Count > 0;
            bool startHiddenToTray = configured && !_appVm.HasInteractiveShowRequest;
            _appVm.MarkStartupVisibilityResolved();
            await _appVm.ExecuteAudioPilotStartupRoutinesAsync(showOverlay: true);

            string startupAction = startHiddenToTray
                ? "start-hidden-to-tray"
                : "show-window";

            if (startHiddenToTray)
            {
                _onStartHiddenToTray();
                _ = await _appVm.StartHiddenToTrayAsync();
            }
            else
            {
                _ = await _appVm.ShowWindowAsync();
            }

            startupStopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.Complete} | opId={startupOpId} appVmInitMs={appVmInitMs:F1} hotkeyRegisterMs={hotkeyRegistrationMs:F1} totalMs={startupStopwatch.Elapsed.TotalMilliseconds:F1} configured={configured} action={startupAction}");
            }
        }

        internal static IReadOnlyList<string> BuildStartupWarningMessages(
            Settings settings,
            IReadOnlyList<SettingsDiagnostic> diagnostics,
            string? loadWarning,
            out int suppressedWarningCount)
        {
            ArgumentNullException.ThrowIfNull(settings);
            suppressedWarningCount = 0;
            bool suppressDisconnectedDeviceWarnings = settings.Miscellaneous?.SuppressDeviceStartupWarnings == true;

            var warnings = new List<string>(diagnostics.Count + (string.IsNullOrWhiteSpace(loadWarning) ? 0 : 1));
            for (int index = 0; index < diagnostics.Count; index++)
            {
                SettingsDiagnostic diagnostic = diagnostics[index];
                if (suppressDisconnectedDeviceWarnings && IsDisconnectedDeviceStartupWarning(diagnostic))
                {
                    suppressedWarningCount++;
                    continue;
                }

                warnings.Add(FormatDiagnosticForUi(diagnostic));
            }

            if (!string.IsNullOrWhiteSpace(loadWarning))
            {
                warnings.Add(loadWarning);
            }

            return warnings;
        }

        private static bool IsDisconnectedDeviceStartupWarning(SettingsDiagnostic diagnostic)
        {
            return string.Equals(diagnostic.Code, "output-cycle-disconnected-devices", StringComparison.Ordinal)
                || string.Equals(diagnostic.Code, "input-cycle-disconnected-devices", StringComparison.Ordinal);
        }

        private static string FormatDiagnosticForUi(SettingsDiagnostic warning)
        {
            return $"{warning.Message} {warning.SuggestedAction}".Trim();
        }

        /// <summary>
        /// Registers all configured hotkey groups and reports partial registration failures.
        /// </summary>
        private void RegisterHotkeys(Settings settings, string startupOpId)
        {
            string outputSwitchHotkey = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.SwitchHotkey : string.Empty;
            string outputReverseSwitchHotkey = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.ReverseSwitchHotkey : string.Empty;
            string inputSwitchHotkey = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.SwitchHotkey : string.Empty;
            string inputReverseSwitchHotkey = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.ReverseSwitchHotkey : string.Empty;

            bool toggleAppVisibilityRegistered = _hotkeyService.RegisterToggleAppVisibilityHotkey(settings.Hotkeys.App.ToggleAppVisibility);
            bool showAudioStatusRegistered = _hotkeyService.RegisterShowAudioStatusHotkey(settings.Hotkeys.App.ShowAudioStatus);
            bool mediaRegistered = _hotkeyService.RegisterMediaHotkeys(settings.Hotkeys.Media.ShowCurrentTrack, settings.Hotkeys.Media.PlayPause, settings.Hotkeys.Media.NextTrack, settings.Hotkeys.Media.PreviousTrack, settings.Hotkeys.Media.SeekForward, settings.Hotkeys.Media.SeekBackward);
            bool muteRegistered = _hotkeyService.RegisterMuteHotkeys(settings.Hotkeys.Mute.Mic, settings.Hotkeys.Mute.Sound, settings.Hotkeys.Mute.Deafen, settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty, settings.Hotkeys.Mute.HoldToMute);
            bool listenRegistered = _hotkeyService.RegisterListenToInputHotkey(settings.Hotkeys.Listen.ListenToInput);
            bool volumeStepRegistered = _hotkeyService.RegisterVolumeStepHotkeys(settings.Hotkeys.Volume.MasterUp, settings.Hotkeys.Volume.MasterDown, settings.Hotkeys.Volume.MicUp, settings.Hotkeys.Volume.MicDown, settings.Hotkeys.Volume.ForegroundUp, settings.Hotkeys.Volume.ForegroundDown, settings.Hotkeys.Volume.ForegroundMute);
            bool outputSwitchRegistered = _hotkeyService.RegisterOutputSwitchHotkey(outputSwitchHotkey);
            bool inputSwitchRegistered = _hotkeyService.RegisterInputSwitchHotkey(inputSwitchHotkey);
            bool outputReverseSwitchRegistered = _hotkeyService.RegisterOutputReverseSwitchHotkey(outputReverseSwitchHotkey);
            bool inputReverseSwitchRegistered = _hotkeyService.RegisterInputReverseSwitchHotkey(inputReverseSwitchHotkey);

            if (!toggleAppVisibilityRegistered || !showAudioStatusRegistered || !mediaRegistered || !muteRegistered || !listenRegistered || !volumeStepRegistered || !outputSwitchRegistered || !inputSwitchRegistered || !outputReverseSwitchRegistered || !inputReverseSwitchRegistered)
            {
                _logger.Warning("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.HotkeysRegisterFailed} | opId={startupOpId} toggleAppVisibility={toggleAppVisibilityRegistered} audioStatus={showAudioStatusRegistered} media={mediaRegistered} mute={muteRegistered} listen={listenRegistered} volumeStep={volumeStepRegistered} output={outputSwitchRegistered} input={inputSwitchRegistered} outputReverse={outputReverseSwitchRegistered} inputReverse={inputReverseSwitchRegistered}");
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace(
                    "AppStartupCoordinator",
                    () => $"startup-hotkeys-register-summary | opId={startupOpId} toggleAppVisibility={toggleAppVisibilityRegistered} audioStatus={showAudioStatusRegistered} media={mediaRegistered} mute={muteRegistered} listen={listenRegistered} volumeStep={volumeStepRegistered} output={outputSwitchRegistered} input={inputSwitchRegistered} outputReverse={outputReverseSwitchRegistered} inputReverse={inputReverseSwitchRegistered}");
            }
        }
    }
}
