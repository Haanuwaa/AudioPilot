using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Updates;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal interface IStartupViewModel
    {
        Task InitializeAsync(bool noSettingsFileExists);
        Settings? CurrentSettings { get; }
        IReadOnlyList<SettingsDiagnostic> GetConfigurationWarningDiagnosticsForUi();
        LocalizedText? GetConfigurationLoadWarningForUi();
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
        bool RegisterQuickDevicePickerHotkey(string? hotkey);
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
            public LocalizedText? GetConfigurationLoadWarningForUi() => _appVm.GetConfigurationLoadWarningForUi();
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
            public bool RegisterQuickDevicePickerHotkey(string? hotkey) => _hotkeyService.RegisterQuickDevicePickerHotkey(hotkey);
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
        private readonly Func<AppDialogRequest, CancellationToken, Task<AppDialogResult>> _showWarning;
        private readonly Action _onStartHiddenToTray;
        private readonly IUpdateStartupGate? _updateGate;

        internal AppStartupCoordinator(
            AppViewModel appVm,
            HotkeyService hotkeyService,
            IAppDialogService dialogs,
            Action? onStartHiddenToTray = null,
            IUpdateStartupGate? updateGate = null)
            : this(
                new AppViewModelAdapter(appVm),
                new HotkeyServiceAdapter(hotkeyService),
                Logger.Instance,
                showWarning: dialogs.ShowAsync,
                onStartHiddenToTray: onStartHiddenToTray,
                updateGate: updateGate)
        {
        }

        internal AppStartupCoordinator(
            IStartupViewModel appVm,
            IStartupHotkeyRegistrar hotkeyService,
            Logger logger,
            Func<AppDialogRequest, CancellationToken, Task<AppDialogResult>>? showWarning = null,
            Action? onStartHiddenToTray = null,
            IUpdateStartupGate? updateGate = null)
        {
            _appVm = appVm;
            _hotkeyService = hotkeyService;
            _logger = logger;
            _showWarning = showWarning ?? (static (_, _) => Task.FromResult(AppDialogResult.Acknowledged));
            _onStartHiddenToTray = onStartHiddenToTray ?? (() => { });
            _updateGate = updateGate;
        }

        /// <summary>
        /// Performs startup initialization, then decides whether to show window or minimize to tray.
        /// </summary>
        /// <remarks>
        /// Unconfigured setups are shown to guide first-time configuration, while configured setups default to tray
        /// flow for day-to-day usage.
        /// </remarks>
        public Task InitializeAsync(bool noSettingsFileExists) => InitializeAsync(noSettingsFileExists, static () => Task.CompletedTask, default);

        internal async Task InitializeAsync(bool noSettingsFileExists, Func<Task> publishShellAsync, CancellationToken cancellationToken)
        {
            string startupOpId = $"startup:{Guid.NewGuid():N}";
            var startupStopwatch = Stopwatch.StartNew();
            _logger.Info("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.Start} | opId={startupOpId} noSettingsFileExists={noSettingsFileExists}");
            cancellationToken.ThrowIfCancellationRequested();
            await _appVm.InitializeAsync(noSettingsFileExists);
            cancellationToken.ThrowIfCancellationRequested();
            double appVmInitMs = startupStopwatch.Elapsed.TotalMilliseconds;

            var settings = _appVm.CurrentSettings;
            if (settings == null)
            {
                if (_updateGate?.Restart == true) throw new InvalidOperationException("Restart settings are unavailable; validation cannot be committed.");
                _logger.Warning("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.SettingsUnavailable} | opId={startupOpId} action=show-window");
                _appVm.MarkStartupVisibilityResolved();
                _ = await _appVm.ShowWindowAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await publishShellAsync();
                return;
            }

            using ((_hotkeyService as IStartupHotkeyRegistrationLoggingScope)?.SuppressVerboseRegistrationLogs())
                RegisterHotkeys(settings, startupOpId);
            if (_updateGate?.Restart != true) _appVm.EnableRoutineAppStartMonitoring();
            double hotkeyRegistrationMs = startupStopwatch.Elapsed.TotalMilliseconds - appVmInitMs;

            bool configured =
                (settings.DeviceSwitching.Output.HotkeysEnabled &&
                    (!string.IsNullOrEmpty(settings.DeviceSwitching.Output.SwitchHotkey) ||
                     !string.IsNullOrEmpty(settings.DeviceSwitching.Output.ReverseSwitchHotkey))) ||
                (settings.DeviceSwitching.Input.HotkeysEnabled &&
                    (!string.IsNullOrEmpty(settings.DeviceSwitching.Input.SwitchHotkey) ||
                     !string.IsNullOrEmpty(settings.DeviceSwitching.Input.ReverseSwitchHotkey))) ||
                settings.Routines.Items.Any(static routine => routine.Enabled) ||
                settings.DeviceSwitching.Output.CycleDevices.Count > 0 ||
                settings.DeviceSwitching.Input.CycleDevices.Count > 0 ||
                HasConfiguredControls(settings.Hotkeys);
            bool startHiddenToTray = _updateGate?.Restart == true ? !_updateGate.Visible : configured && !_appVm.HasInteractiveShowRequest;
            _appVm.MarkStartupVisibilityResolved();
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

            cancellationToken.ThrowIfCancellationRequested();
            await publishShellAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (_updateGate?.Restart == true)
            {
                await _updateGate.WaitForCommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _appVm.EnableRoutineAppStartMonitoring();
            }

            var diagnostics = _appVm.GetConfigurationWarningDiagnosticsForUi();
            LocalizedText? loadWarning = _appVm.GetConfigurationLoadWarningForUi();
            IReadOnlyList<string> warnings = BuildStartupWarningMessages(
                settings, diagnostics, loadWarning,
                out int suppressedWarningCount,
                out int warningCount);
            if (warnings.Count > 0 && !noSettingsFileExists)
            {
                string summary = warningCount == 1 ? "One setting needs attention." : $"{warningCount} settings warnings need attention.";
                if (_updateGate?.Restart != true)
                {
                    string message = string.Join("\n\n", warnings);
                    await _showWarning(AppDialogRequest.Acknowledge(message, DialogText.Captions.SettingsWarnings,
                        AppDialogKind.Warning, allowCopy: true, summary: summary) with
                    {
                        SummaryResource = warningCount == 1 ? new("Startup_OneSettingNeedsAttention")
                            : new("Startup_SettingsNeedAttention", warningCount),
                        MessagePresentation = language => language.EffectiveLanguage == "en" ? message
                            : string.Join("\n\n", BuildStartupWarningMessages(settings, diagnostics, loadWarning, out _, out _, language)),
                    }, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _logger.Warning("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.SettingsWarnings} | opId={startupOpId} count={warningCount} suppressed={suppressedWarningCount}");
            }
            else if (suppressedWarningCount > 0 && !noSettingsFileExists)
            {
                _logger.Debug("AppStartupCoordinator", () => $"startup-settings-warnings-suppressed | opId={startupOpId} count={suppressedWarningCount} reason=disconnected-device-startup-warnings-disabled");
            }

            if (_updateGate?.Restart != true) await _appVm.ExecuteAudioPilotStartupRoutinesAsync(showOverlay: true);
            cancellationToken.ThrowIfCancellationRequested();

            startupStopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.Complete} | opId={startupOpId} appVmInitMs={appVmInitMs:F1} hotkeyRegisterMs={hotkeyRegistrationMs:F1} totalMs={startupStopwatch.Elapsed.TotalMilliseconds:F1} configured={configured} action={startupAction}");
            }
        }

        private static bool HasConfiguredControls(HotkeysSettings hotkeys)
        {
            ReadOnlySpan<string> controls =
            [
                hotkeys.App.ShowAudioStatus, hotkeys.App.QuickDevicePicker,
                hotkeys.Media.ShowCurrentTrack, hotkeys.Media.PlayPause, hotkeys.Media.NextTrack,
                hotkeys.Media.PreviousTrack, hotkeys.Media.SeekForward, hotkeys.Media.SeekBackward,
                hotkeys.Mute.Mic, hotkeys.Mute.Sound, hotkeys.Mute.Deafen, hotkeys.Mute.HoldToMute,
                hotkeys.Mute.PushToTalkEnabled ? hotkeys.Mute.PushToTalk : string.Empty,
                hotkeys.Volume.MasterUp, hotkeys.Volume.MasterDown, hotkeys.Volume.MicUp, hotkeys.Volume.MicDown,
                hotkeys.Volume.ForegroundUp, hotkeys.Volume.ForegroundDown, hotkeys.Volume.ForegroundMute,
                hotkeys.Listen.ListenToInput,
            ];
            foreach (string control in controls)
            {
                if (!string.IsNullOrWhiteSpace(control)) return true;
            }
            return false;
        }

        internal static IReadOnlyList<string> BuildStartupWarningMessages(
            Settings settings,
            IReadOnlyList<SettingsDiagnostic> diagnostics,
            LocalizedText? loadWarning,
            out int suppressedWarningCount,
            out int warningCount,
            LocalizationService? localization = null)
        {
            ArgumentNullException.ThrowIfNull(settings);
            suppressedWarningCount = 0;
            bool suppressDisconnectedDeviceWarnings = settings.Miscellaneous?.SuppressDeviceStartupWarnings == true;

            var visible = new List<SettingsDiagnostic>(diagnostics.Count);
            for (int index = 0; index < diagnostics.Count; index++)
            {
                SettingsDiagnostic diagnostic = diagnostics[index];
                if (suppressDisconnectedDeviceWarnings && IsDisconnectedDeviceStartupWarning(diagnostic))
                {
                    suppressedWarningCount++;
                    continue;
                }

                visible.Add(diagnostic);
            }

            var warnings = new List<string>();
            foreach (var group in visible.GroupBy(diagnostic => (diagnostic.Code, diagnostic.SuggestedAction)))
            {
                SettingsDiagnostic[] items = [.. group];
                string guidance = Render(group.Key.SuggestedAction, items[0].UiSuggestedAction);
                warnings.Add(items.Length == 1
                    ? $"{Render(items[0].Message, items[0].UiMessage)} {guidance}".Trim()
                    : $"{guidance}\n{string.Join("\n", items.Select(item => $"- {Render(item.Message, item.UiMessage)}"))}".TrimStart());
            }

            warningCount = visible.Count;
            if (loadWarning != null)
            {
                warnings.Add((localization ?? LocalizationService.English).Format(loadWarning));
                warningCount++;
            }

            return warnings;

            string Render(string text, LocalizedText? translated) => localization == null ? text
                : translated == null ? AppMessages.Translate(text, localization) : localization.Format(translated);
        }

        private static bool IsDisconnectedDeviceStartupWarning(SettingsDiagnostic diagnostic)
        {
            return string.Equals(diagnostic.Code, "output-cycle-disconnected-devices", StringComparison.Ordinal)
                || string.Equals(diagnostic.Code, "input-cycle-disconnected-devices", StringComparison.Ordinal);
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
            bool quickDevicePickerRegistered = _hotkeyService.RegisterQuickDevicePickerHotkey(settings.Hotkeys.App.QuickDevicePicker);
            bool mediaRegistered = _hotkeyService.RegisterMediaHotkeys(settings.Hotkeys.Media.ShowCurrentTrack, settings.Hotkeys.Media.PlayPause, settings.Hotkeys.Media.NextTrack, settings.Hotkeys.Media.PreviousTrack, settings.Hotkeys.Media.SeekForward, settings.Hotkeys.Media.SeekBackward);
            bool muteRegistered = _hotkeyService.RegisterMuteHotkeys(settings.Hotkeys.Mute.Mic, settings.Hotkeys.Mute.Sound, settings.Hotkeys.Mute.Deafen, settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty, settings.Hotkeys.Mute.HoldToMute);
            bool listenRegistered = _hotkeyService.RegisterListenToInputHotkey(settings.Hotkeys.Listen.ListenToInput);
            bool volumeStepRegistered = _hotkeyService.RegisterVolumeStepHotkeys(settings.Hotkeys.Volume.MasterUp, settings.Hotkeys.Volume.MasterDown, settings.Hotkeys.Volume.MicUp, settings.Hotkeys.Volume.MicDown, settings.Hotkeys.Volume.ForegroundUp, settings.Hotkeys.Volume.ForegroundDown, settings.Hotkeys.Volume.ForegroundMute);
            bool outputSwitchRegistered = _hotkeyService.RegisterOutputSwitchHotkey(outputSwitchHotkey);
            bool inputSwitchRegistered = _hotkeyService.RegisterInputSwitchHotkey(inputSwitchHotkey);
            bool outputReverseSwitchRegistered = _hotkeyService.RegisterOutputReverseSwitchHotkey(outputReverseSwitchHotkey);
            bool inputReverseSwitchRegistered = _hotkeyService.RegisterInputReverseSwitchHotkey(inputReverseSwitchHotkey);

            if (!toggleAppVisibilityRegistered || !showAudioStatusRegistered || !quickDevicePickerRegistered || !mediaRegistered || !muteRegistered || !listenRegistered || !volumeStepRegistered || !outputSwitchRegistered || !inputSwitchRegistered || !outputReverseSwitchRegistered || !inputReverseSwitchRegistered)
            {
                _logger.Warning("AppStartupCoordinator", () => $"{AppConstants.Audio.LogEvents.StartupCoordinator.HotkeysRegisterFailed} | opId={startupOpId} toggleAppVisibility={toggleAppVisibilityRegistered} audioStatus={showAudioStatusRegistered} quickDevicePicker={quickDevicePickerRegistered} media={mediaRegistered} mute={muteRegistered} listen={listenRegistered} volumeStep={volumeStepRegistered} output={outputSwitchRegistered} input={inputSwitchRegistered} outputReverse={outputReverseSwitchRegistered} inputReverse={inputReverseSwitchRegistered}");
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace(
                    "AppStartupCoordinator",
                    () => $"startup-hotkeys-register-summary | opId={startupOpId} toggleAppVisibility={toggleAppVisibilityRegistered} audioStatus={showAudioStatusRegistered} quickDevicePicker={quickDevicePickerRegistered} media={mediaRegistered} mute={muteRegistered} listen={listenRegistered} volumeStep={volumeStepRegistered} output={outputSwitchRegistered} input={inputSwitchRegistered} outputReverse={outputReverseSwitchRegistered} inputReverse={inputReverseSwitchRegistered}");
            }
        }
    }
}
