using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct RoutineHotkeyRegistrationResult(
        int RegisteredGroupCount,
        int FailedGroupCount,
        int ActiveRoutineCount)
    {
        public bool HasFailures => FailedGroupCount > 0;

        public IReadOnlyDictionary<string, int> AttemptedHotkeyIdsByRoutineId { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    internal readonly record struct SwitchHotkeyRegistrationResult(
        bool OutputSwitchRegistered,
        bool InputSwitchRegistered,
        bool OutputReverseSwitchRegistered,
        bool InputReverseSwitchRegistered,
        bool OutputSwitchAttempted = true,
        bool InputSwitchAttempted = true,
        bool OutputReverseSwitchAttempted = true,
        bool InputReverseSwitchAttempted = true)
    {
        public bool HasFailures =>
            (OutputSwitchAttempted && !OutputSwitchRegistered) ||
            (InputSwitchAttempted && !InputSwitchRegistered) ||
            (OutputReverseSwitchAttempted && !OutputReverseSwitchRegistered) ||
            (InputReverseSwitchAttempted && !InputReverseSwitchRegistered);
    }

    internal readonly record struct HotkeyRegistrationResult(
        bool ToggleAppVisibilityRegistered,
        bool MediaHotkeysRegistered,
        bool MuteHotkeysRegistered,
        bool ListenToInputRegistered,
        bool VolumeStepHotkeysRegistered,
        bool OutputSwitchRegistered,
        bool InputSwitchRegistered,
        bool OutputReverseSwitchRegistered,
        bool InputReverseSwitchRegistered,
        bool ToggleAppVisibilityAttempted = true,
        bool MediaHotkeysAttempted = true,
        bool MuteHotkeysAttempted = true,
        bool ListenToInputAttempted = true,
        bool VolumeStepHotkeysAttempted = true,
        bool OutputSwitchAttempted = true,
        bool InputSwitchAttempted = true,
        bool OutputReverseSwitchAttempted = true,
        bool InputReverseSwitchAttempted = true,
        bool ShowAudioStatusRegistered = true,
        bool ShowAudioStatusAttempted = false)
    {
        public int FailedCount =>
            (ToggleAppVisibilityAttempted && !ToggleAppVisibilityRegistered ? 1 : 0) +
            (ShowAudioStatusAttempted && !ShowAudioStatusRegistered ? 1 : 0) +
            (MediaHotkeysAttempted && !MediaHotkeysRegistered ? 1 : 0) +
            (MuteHotkeysAttempted && !MuteHotkeysRegistered ? 1 : 0) +
            (ListenToInputAttempted && !ListenToInputRegistered ? 1 : 0) +
            (VolumeStepHotkeysAttempted && !VolumeStepHotkeysRegistered ? 1 : 0) +
            (OutputSwitchAttempted && !OutputSwitchRegistered ? 1 : 0) +
            (InputSwitchAttempted && !InputSwitchRegistered ? 1 : 0) +
            (OutputReverseSwitchAttempted && !OutputReverseSwitchRegistered ? 1 : 0) +
            (InputReverseSwitchAttempted && !InputReverseSwitchRegistered ? 1 : 0);

        public SwitchHotkeyRegistrationResult SwitchResult => new(
            OutputSwitchRegistered,
            InputSwitchRegistered,
            OutputReverseSwitchRegistered,
            InputReverseSwitchRegistered,
            OutputSwitchAttempted,
            InputSwitchAttempted,
            OutputReverseSwitchAttempted,
            InputReverseSwitchAttempted);
    }

    internal sealed class AppHotkeyRegistrationCoordinator(HotkeyService hotkeys, Logger logger)
    {
        private readonly record struct RoutineHotkeyRegistrationState(
            int HotkeyId,
            string NormalizedHotkey,
            AudioRoutine RoutineSnapshot);

        private readonly record struct PendingRoutineHotkeyRegistration(
            string NormalizedRoutineId,
            AudioRoutine Routine,
            string NormalizedHotkey,
            RoutineHotkeyRegistrationState? PreviousState);

        private readonly HotkeyService _hotkeys = hotkeys;
        private readonly Logger _logger = logger;
        private readonly Dictionary<string, RoutineHotkeyRegistrationState> _registeredRoutineHotkeysByRoutineId = new(StringComparer.OrdinalIgnoreCase);

        private static string FormatHotkeyForLog(string? hotkey) => LogPrivacy.Label(hotkey);

        private static string FormatRoutineIdForLog(string? routineId) => LogPrivacy.Id(routineId);

        public HotkeyRegistrationResult RegisterAll(Settings settings, bool unregisterAllFirst = false)
        {
            if (unregisterAllFirst)
            {
                _hotkeys.UnregisterAllHotkeys();
                _registeredRoutineHotkeysByRoutineId.Clear();
            }

            string outputSwitchHotkey = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.SwitchHotkey : string.Empty;
            string outputReverseSwitchHotkey = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.ReverseSwitchHotkey : string.Empty;
            string inputSwitchHotkey = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.SwitchHotkey : string.Empty;
            string inputReverseSwitchHotkey = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.ReverseSwitchHotkey : string.Empty;

            bool outputSwitchRegistered = _hotkeys.RegisterOutputSwitchHotkey(outputSwitchHotkey);
            bool inputSwitchRegistered = _hotkeys.RegisterInputSwitchHotkey(inputSwitchHotkey);
            bool outputReverseSwitchRegistered = _hotkeys.RegisterOutputReverseSwitchHotkey(outputReverseSwitchHotkey);
            bool inputReverseSwitchRegistered = _hotkeys.RegisterInputReverseSwitchHotkey(inputReverseSwitchHotkey);
            bool toggleAppVisibilityRegistered = _hotkeys.RegisterToggleAppVisibilityHotkey(settings.Hotkeys.App.ToggleAppVisibility);
            bool showAudioStatusRegistered = _hotkeys.RegisterShowAudioStatusHotkey(settings.Hotkeys.App.ShowAudioStatus);
            bool mediaHotkeysRegistered = _hotkeys.RegisterMediaHotkeys(
                settings.Hotkeys.Media.ShowCurrentTrack,
                settings.Hotkeys.Media.PlayPause,
                settings.Hotkeys.Media.NextTrack,
                settings.Hotkeys.Media.PreviousTrack, settings.Hotkeys.Media.SeekForward, settings.Hotkeys.Media.SeekBackward);
            bool muteHotkeysRegistered = _hotkeys.RegisterMuteHotkeys(
                settings.Hotkeys.Mute.Mic,
                settings.Hotkeys.Mute.Sound,
                settings.Hotkeys.Mute.Deafen, settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty, settings.Hotkeys.Mute.HoldToMute);
            bool listenToInputRegistered = _hotkeys.RegisterListenToInputHotkey(settings.Hotkeys.Listen.ListenToInput);
            bool volumeStepHotkeysRegistered = _hotkeys.RegisterVolumeStepHotkeys(
                settings.Hotkeys.Volume.MasterUp,
                settings.Hotkeys.Volume.MasterDown,
                settings.Hotkeys.Volume.MicUp,
                settings.Hotkeys.Volume.MicDown, settings.Hotkeys.Volume.ForegroundUp, settings.Hotkeys.Volume.ForegroundDown, settings.Hotkeys.Volume.ForegroundMute);

            return new HotkeyRegistrationResult(
                toggleAppVisibilityRegistered,
                mediaHotkeysRegistered,
                muteHotkeysRegistered,
                listenToInputRegistered,
                volumeStepHotkeysRegistered,
                outputSwitchRegistered,
                inputSwitchRegistered,
                outputReverseSwitchRegistered,
                inputReverseSwitchRegistered,
                ShowAudioStatusRegistered: showAudioStatusRegistered,
                ShowAudioStatusAttempted: true);
        }

        public HotkeyRegistrationResult RegisterChangedGlobalHotkeys(Settings? previousSettings, Settings settings)
        {
            bool toggleAppVisibilityAttempted = previousSettings == null || !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.App.ToggleAppVisibility, settings.Hotkeys.App.ToggleAppVisibility);
            bool showAudioStatusAttempted = previousSettings == null || !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.App.ShowAudioStatus, settings.Hotkeys.App.ShowAudioStatus);
            bool mediaAttempted = previousSettings == null ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Media.ShowCurrentTrack, settings.Hotkeys.Media.ShowCurrentTrack) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Media.PlayPause, settings.Hotkeys.Media.PlayPause) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Media.NextTrack, settings.Hotkeys.Media.NextTrack) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Media.PreviousTrack, settings.Hotkeys.Media.PreviousTrack) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Media.SeekForward, settings.Hotkeys.Media.SeekForward) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Media.SeekBackward, settings.Hotkeys.Media.SeekBackward);
            bool muteAttempted = previousSettings == null || previousSettings.Hotkeys.Mute.PushToTalkEnabled != settings.Hotkeys.Mute.PushToTalkEnabled ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Mute.Mic, settings.Hotkeys.Mute.Mic) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Mute.Sound, settings.Hotkeys.Mute.Sound) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Mute.Deafen, settings.Hotkeys.Mute.Deafen) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Mute.PushToTalk, settings.Hotkeys.Mute.PushToTalk) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Mute.HoldToMute, settings.Hotkeys.Mute.HoldToMute);
            bool listenAttempted = previousSettings == null || !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Listen.ListenToInput, settings.Hotkeys.Listen.ListenToInput);
            bool volumeAttempted = previousSettings == null ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.MasterUp, settings.Hotkeys.Volume.MasterUp) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.MasterDown, settings.Hotkeys.Volume.MasterDown) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.MicUp, settings.Hotkeys.Volume.MicUp) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.MicDown, settings.Hotkeys.Volume.MicDown) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.ForegroundUp, settings.Hotkeys.Volume.ForegroundUp) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.ForegroundDown, settings.Hotkeys.Volume.ForegroundDown) ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.Hotkeys.Volume.ForegroundMute, settings.Hotkeys.Volume.ForegroundMute);
            SwitchHotkeyRegistrationResult switchResult = RegisterChangedSwitchHotkeysCore(
                previousSettings,
                settings);
            bool toggleAppVisibilityRegistered = toggleAppVisibilityAttempted
                ? _hotkeys.RegisterToggleAppVisibilityHotkey(settings.Hotkeys.App.ToggleAppVisibility)
                : IsCurrentLogicalHotkeyRegistered(settings.Hotkeys.App.ToggleAppVisibility, AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId);
            bool showAudioStatusRegistered = showAudioStatusAttempted
                ? _hotkeys.RegisterShowAudioStatusHotkey(settings.Hotkeys.App.ShowAudioStatus)
                : IsCurrentLogicalHotkeyRegistered(settings.Hotkeys.App.ShowAudioStatus, AppConstants.Hotkeys.ShowAudioStatusHotkeyId);
            bool mediaHotkeysRegistered = mediaAttempted
                ? _hotkeys.RegisterMediaHotkeys(
                    settings.Hotkeys.Media.ShowCurrentTrack,
                    settings.Hotkeys.Media.PlayPause,
                    settings.Hotkeys.Media.NextTrack,
                    settings.Hotkeys.Media.PreviousTrack, settings.Hotkeys.Media.SeekForward, settings.Hotkeys.Media.SeekBackward)
                : AreCurrentLogicalHotkeysRegistered(
                    (settings.Hotkeys.Media.ShowCurrentTrack, AppConstants.Hotkeys.MediaShowCurrentTrackId),
                    (settings.Hotkeys.Media.PlayPause, AppConstants.Hotkeys.MediaPlayPauseId),
                    (settings.Hotkeys.Media.NextTrack, AppConstants.Hotkeys.MediaNextTrackId),
                    (settings.Hotkeys.Media.PreviousTrack, AppConstants.Hotkeys.MediaPrevTrackId),
                    (settings.Hotkeys.Media.SeekForward, AppConstants.Hotkeys.MediaSeekForwardId),
                    (settings.Hotkeys.Media.SeekBackward, AppConstants.Hotkeys.MediaSeekBackwardId));
            bool muteHotkeysRegistered = muteAttempted
                ? _hotkeys.RegisterMuteHotkeys(
                    settings.Hotkeys.Mute.Mic,
                    settings.Hotkeys.Mute.Sound,
                    settings.Hotkeys.Mute.Deafen, settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty, settings.Hotkeys.Mute.HoldToMute)
                : AreCurrentLogicalHotkeysRegistered(
                    (settings.Hotkeys.Mute.Mic, AppConstants.Hotkeys.MuteMicId),
                    (settings.Hotkeys.Mute.Sound, AppConstants.Hotkeys.MuteSoundId),
                    (settings.Hotkeys.Mute.Deafen, AppConstants.Hotkeys.DeafenId),
                    (settings.Hotkeys.Mute.PushToTalkEnabled ? settings.Hotkeys.Mute.PushToTalk : string.Empty, AppConstants.Hotkeys.PushToTalkHotkeyId),
                    (settings.Hotkeys.Mute.HoldToMute, AppConstants.Hotkeys.HoldToMuteHotkeyId));
            bool listenToInputRegistered = listenAttempted
                ? _hotkeys.RegisterListenToInputHotkey(settings.Hotkeys.Listen.ListenToInput)
                : IsCurrentLogicalHotkeyRegistered(settings.Hotkeys.Listen.ListenToInput, AppConstants.Hotkeys.ListenToInputHotkeyId);
            bool volumeStepHotkeysRegistered = volumeAttempted
                ? _hotkeys.RegisterVolumeStepHotkeys(
                    settings.Hotkeys.Volume.MasterUp,
                    settings.Hotkeys.Volume.MasterDown,
                    settings.Hotkeys.Volume.MicUp,
                    settings.Hotkeys.Volume.MicDown, settings.Hotkeys.Volume.ForegroundUp, settings.Hotkeys.Volume.ForegroundDown, settings.Hotkeys.Volume.ForegroundMute)
                : AreCurrentLogicalHotkeysRegistered(
                    (settings.Hotkeys.Volume.MasterUp, AppConstants.Hotkeys.MasterVolumeUpHotkeyId),
                    (settings.Hotkeys.Volume.MasterDown, AppConstants.Hotkeys.MasterVolumeDownHotkeyId),
                    (settings.Hotkeys.Volume.MicUp, AppConstants.Hotkeys.MicVolumeUpHotkeyId),
                    (settings.Hotkeys.Volume.MicDown, AppConstants.Hotkeys.MicVolumeDownHotkeyId),
                    (settings.Hotkeys.Volume.ForegroundUp, AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId),
                    (settings.Hotkeys.Volume.ForegroundDown, AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId),
                    (settings.Hotkeys.Volume.ForegroundMute, AppConstants.Hotkeys.ForegroundMuteHotkeyId));

            return new HotkeyRegistrationResult(
                toggleAppVisibilityRegistered,
                mediaHotkeysRegistered,
                muteHotkeysRegistered,
                listenToInputRegistered,
                volumeStepHotkeysRegistered,
                switchResult.OutputSwitchRegistered,
                switchResult.InputSwitchRegistered,
                switchResult.OutputReverseSwitchRegistered,
                switchResult.InputReverseSwitchRegistered,
                toggleAppVisibilityAttempted,
                mediaAttempted,
                muteAttempted,
                listenAttempted,
                volumeAttempted,
                switchResult.OutputSwitchAttempted,
                switchResult.InputSwitchAttempted,
                switchResult.OutputReverseSwitchAttempted,
                switchResult.InputReverseSwitchAttempted,
                ShowAudioStatusRegistered: showAudioStatusRegistered,
                ShowAudioStatusAttempted: showAudioStatusAttempted);
        }

        public SwitchHotkeyRegistrationResult RegisterChangedSwitchHotkeys(Settings? previousSettings, Settings settings)
        {
            return RegisterChangedSwitchHotkeysCore(previousSettings, settings);
        }

        public RoutineHotkeyRegistrationResult RegisterRoutineHotkeys(IReadOnlyList<AudioRoutine>? routines, Action<AudioRoutine> activateRoutine)
        {
            ArgumentNullException.ThrowIfNull(activateRoutine);

            if (routines == null || routines.Count == 0)
            {
                UnregisterRoutineHotkeys();

                return new RoutineHotkeyRegistrationResult(0, 0, 0);
            }

            Dictionary<string, RoutineHotkeyRegistrationState> previousStates = new(_registeredRoutineHotkeysByRoutineId, StringComparer.OrdinalIgnoreCase);

            List<AudioRoutine> activeRoutines = [.. routines
                .Where(static routine => routine.Enabled)
                .OrderBy(static routine => routine.DisplayOrder)
                .ThenBy(static routine => routine.Name, StringComparer.OrdinalIgnoreCase)];

            int registeredGroupCount = 0;
            int failedGroupCount = 0;
            List<int> registeredRoutineIds = [];
            List<PendingRoutineHotkeyRegistration> pendingRegistrations = [];
            HashSet<string> registeredHotkeys = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> attemptedHotkeyIdsByRoutineId = new(StringComparer.OrdinalIgnoreCase);
            int acceptedRoutineCount = 0;

            foreach (AudioRoutine routine in RoutineHotkeyGroups.Representatives(activeRoutines))
            {
                if (RoutineHotkeyGroups.Validate(routine, routines) is { } groupError)
                {
                    failedGroupCount++;
                    _logger.Warning("AppHotkeyRegistrationCoordinator", () => $"routine-hotkey-register-skipped | reason=invalid-cycle-group routineId={FormatRoutineIdForLog(routine.Id)} detail={groupError}");
                    continue;
                }
                if (!TryNormalizeHotkey(routine.Hotkey, out string hotkey))
                {
                    continue;
                }

                if (acceptedRoutineCount >= AppConstants.Hotkeys.RoutineHotkeyIdMaxCount)
                {
                    failedGroupCount++;
                    _logger.Warning("AppHotkeyRegistrationCoordinator", () => $"routine-hotkey-register-skipped | reason=max-routine-hotkeys-exceeded hotkey={FormatHotkeyForLog(hotkey)}");
                    continue;
                }

                if (!registeredHotkeys.Add(hotkey))
                {
                    failedGroupCount++;
                    _logger.Warning("AppHotkeyRegistrationCoordinator", () => $"routine-hotkey-register-skipped | reason=duplicate-routine-hotkey hotkey={FormatHotkeyForLog(hotkey)} routineId={FormatRoutineIdForLog(routine.Id)}");
                    continue;
                }

                acceptedRoutineCount++;
                string normalizedRoutineId = RoutineHotkeyGroups.BindingKey(routine);
                previousStates.TryGetValue(normalizedRoutineId, out RoutineHotkeyRegistrationState previousState);
                pendingRegistrations.Add(new PendingRoutineHotkeyRegistration(
                    normalizedRoutineId,
                    routine,
                    hotkey,
                    previousState.HotkeyId > 0 ? previousState : null));
            }

            Dictionary<string, RoutineHotkeyRegistrationState> nextStates = new(StringComparer.OrdinalIgnoreCase);
            HashSet<int> usedIds = [];

            foreach (PendingRoutineHotkeyRegistration pending in pendingRegistrations)
            {
                if (pending.PreviousState is not RoutineHotkeyRegistrationState previousState ||
                    !string.Equals(previousState.NormalizedHotkey, pending.NormalizedHotkey, StringComparison.OrdinalIgnoreCase) ||
                    !AreEquivalentRoutineHotkeyBinding(previousState.RoutineSnapshot, pending.Routine))
                {
                    continue;
                }

                nextStates[pending.NormalizedRoutineId] = previousState;
                usedIds.Add(previousState.HotkeyId);
                registeredRoutineIds.Add(previousState.HotkeyId);
                attemptedHotkeyIdsByRoutineId[pending.Routine.Id] = previousState.HotkeyId;
                registeredGroupCount++;
            }

            foreach (PendingRoutineHotkeyRegistration pending in pendingRegistrations)
            {
                if (nextStates.ContainsKey(pending.NormalizedRoutineId))
                {
                    continue;
                }

                int preferredId = pending.PreviousState?.HotkeyId ?? 0;
                int id = ResolveRoutineHotkeyId(preferredId, usedIds);
                attemptedHotkeyIdsByRoutineId[pending.Routine.Id] = id;

                bool registered = _hotkeys.RegisterDynamicHotkey(
                    id,
                    pending.NormalizedHotkey,
                    () => activateRoutine(pending.Routine),
                    $"Routine ({pending.NormalizedHotkey})");

                if (!registered)
                {
                    failedGroupCount++;
                    _logger.Warning("AppHotkeyRegistrationCoordinator", () => $"routine-hotkey-register-failed | hotkey={FormatHotkeyForLog(pending.NormalizedHotkey)} routineId={FormatRoutineIdForLog(pending.Routine.Id)}");
                    continue;
                }

                nextStates[pending.NormalizedRoutineId] = new RoutineHotkeyRegistrationState(
                    id,
                    pending.NormalizedHotkey,
                    pending.Routine.Clone());
                registeredRoutineIds.Add(id);
                registeredGroupCount++;
            }

            HashSet<int> activeRoutineHotkeyIds = [.. nextStates.Values.Select(static state => state.HotkeyId)];

            foreach ((string routineId, RoutineHotkeyRegistrationState previousState) in previousStates)
            {
                if (nextStates.ContainsKey(routineId) || activeRoutineHotkeyIds.Contains(previousState.HotkeyId))
                {
                    continue;
                }

                _hotkeys.UnregisterHotkey(previousState.HotkeyId);
            }

            _registeredRoutineHotkeysByRoutineId.Clear();
            foreach ((string routineId, RoutineHotkeyRegistrationState state) in nextStates)
            {
                _registeredRoutineHotkeysByRoutineId[routineId] = state;
            }

            foreach (PendingRoutineHotkeyRegistration pending in pendingRegistrations)
                if (attemptedHotkeyIdsByRoutineId.TryGetValue(pending.Routine.Id, out int attemptedId))
                    foreach (AudioRoutine member in activeRoutines)
                        if (string.Equals(RoutineHotkeyGroups.BindingKey(member), pending.NormalizedRoutineId, StringComparison.OrdinalIgnoreCase))
                            attemptedHotkeyIdsByRoutineId[member.Id] = attemptedId;

            if (registeredRoutineIds.Count > 0)
            {
                _hotkeys.LogRegistrationGroupDeliverySummary("routine", [.. registeredRoutineIds]);
            }

            return new RoutineHotkeyRegistrationResult(registeredGroupCount, failedGroupCount, activeRoutines.Count)
            {
                AttemptedHotkeyIdsByRoutineId = attemptedHotkeyIdsByRoutineId,
            };
        }

        public void UnregisterRoutineHotkeys()
        {
            foreach (RoutineHotkeyRegistrationState state in _registeredRoutineHotkeysByRoutineId.Values)
            {
                _hotkeys.UnregisterHotkey(state.HotkeyId);
            }

            _registeredRoutineHotkeysByRoutineId.Clear();
        }

        public void LogApplySettingsResults(HotkeyRegistrationResult result)
        {
            if (result.ToggleAppVisibilityAttempted && result.ToggleAppVisibilityRegistered)
            {
                _logger.Info("AppViewModel", "Show / Hide App hotkey registered");
            }
            else if (result.ToggleAppVisibilityAttempted)
            {
                _logger.Warning("AppViewModel", "Failed to register Show / Hide App hotkey");
            }

            if (result.ShowAudioStatusAttempted)
            {
                _logger.Log(result.ShowAudioStatusRegistered ? LogLevel.Info : LogLevel.Warning, "AppViewModel", $"audio-status-hotkey-register | success={result.ShowAudioStatusRegistered}");
            }

            if (result.MediaHotkeysAttempted && result.MediaHotkeysRegistered)
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterSuccess} | group=media");
            }
            else if (result.MediaHotkeysAttempted)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterFailed} | group=media");
            }

            if (result.MuteHotkeysAttempted && result.MuteHotkeysRegistered)
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterSuccess} | group=mute");
            }
            else if (result.MuteHotkeysAttempted)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterFailed} | group=mute");
            }

            if (result.ListenToInputAttempted && result.ListenToInputRegistered)
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterSuccess} | group=listen");
            }
            else if (result.ListenToInputAttempted)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterFailed} | group=listen");
            }

            if (result.VolumeStepHotkeysAttempted && result.VolumeStepHotkeysRegistered)
            {
                _logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterSuccess} | group=volume-step");
            }
            else if (result.VolumeStepHotkeysAttempted)
            {
                _logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.HotkeysRegisterFailed} | group=volume-step");
            }

            LogSwitchOnlyFailure(result.SwitchResult);
        }

        public void LogSwitchOnlyFailure(SwitchHotkeyRegistrationResult result, string? context = null)
        {
            if (!result.HasFailures)
            {
                return;
            }

            string contextPrefix = string.IsNullOrWhiteSpace(context)
                ? string.Empty
                : $"context={context} ";

            _logger.Warning(
                "AppViewModel",
                () => $"{AppConstants.Audio.LogEvents.ViewModel.SwitchHotkeysRegisterFailed} | {contextPrefix}output={result.OutputSwitchRegistered} input={result.InputSwitchRegistered} outputReverse={result.OutputReverseSwitchRegistered} inputReverse={result.InputReverseSwitchRegistered}");
        }

        public void LogRoutineRegistrationResult(RoutineHotkeyRegistrationResult result, string? context = null)
        {
            if (!result.HasFailures)
            {
                return;
            }

            string contextPrefix = string.IsNullOrWhiteSpace(context)
                ? string.Empty
                : $"context={context} ";

            _logger.Warning(
                "AppHotkeyRegistrationCoordinator",
                () => $"routine-hotkeys-register-failed | {contextPrefix}registeredGroups={result.RegisteredGroupCount} failedGroups={result.FailedGroupCount} activeRoutines={result.ActiveRoutineCount}");
        }

        private static bool TryNormalizeHotkey(string? rawHotkey, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(rawHotkey))
            {
                return false;
            }

            var parser = new HotkeyViewModel();
            if (!parser.LoadFromString(rawHotkey))
            {
                return false;
            }

            normalized = parser.ToHotkeyString();
            return !string.IsNullOrWhiteSpace(normalized);
        }

        private SwitchHotkeyRegistrationResult RegisterChangedSwitchHotkeysCore(
            Settings? previousSettings,
            Settings settings)
        {
            string outputSwitchHotkey = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.SwitchHotkey : string.Empty;
            string outputReverseSwitchHotkey = settings.DeviceSwitching.Output.HotkeysEnabled ? settings.DeviceSwitching.Output.ReverseSwitchHotkey : string.Empty;
            string inputSwitchHotkey = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.SwitchHotkey : string.Empty;
            string inputReverseSwitchHotkey = settings.DeviceSwitching.Input.HotkeysEnabled ? settings.DeviceSwitching.Input.ReverseSwitchHotkey : string.Empty;

            bool outputSwitchAttempted = previousSettings == null ||
                previousSettings.DeviceSwitching.Output.HotkeysEnabled != settings.DeviceSwitching.Output.HotkeysEnabled ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.DeviceSwitching.Output.SwitchHotkey, settings.DeviceSwitching.Output.SwitchHotkey);
            bool inputSwitchAttempted = previousSettings == null ||
                previousSettings.DeviceSwitching.Input.HotkeysEnabled != settings.DeviceSwitching.Input.HotkeysEnabled ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.DeviceSwitching.Input.SwitchHotkey, settings.DeviceSwitching.Input.SwitchHotkey);
            bool outputReverseSwitchAttempted = previousSettings == null ||
                previousSettings.DeviceSwitching.Output.HotkeysEnabled != settings.DeviceSwitching.Output.HotkeysEnabled ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.DeviceSwitching.Output.ReverseSwitchHotkey, settings.DeviceSwitching.Output.ReverseSwitchHotkey);
            bool inputReverseSwitchAttempted = previousSettings == null ||
                previousSettings.DeviceSwitching.Input.HotkeysEnabled != settings.DeviceSwitching.Input.HotkeysEnabled ||
                !AppViewModel.AreHotkeyStringsEquivalent(previousSettings.DeviceSwitching.Input.ReverseSwitchHotkey, settings.DeviceSwitching.Input.ReverseSwitchHotkey);

            bool outputSwitchRegistered = outputSwitchAttempted
                ? _hotkeys.RegisterOutputSwitchHotkey(outputSwitchHotkey)
                : IsCurrentLogicalHotkeyRegistered(outputSwitchHotkey, AppConstants.Hotkeys.OutputSwitchHotkeyId);
            bool inputSwitchRegistered = inputSwitchAttempted
                ? _hotkeys.RegisterInputSwitchHotkey(inputSwitchHotkey)
                : IsCurrentLogicalHotkeyRegistered(inputSwitchHotkey, AppConstants.Hotkeys.InputSwitchHotkeyId);
            bool outputReverseSwitchRegistered = outputReverseSwitchAttempted
                ? _hotkeys.RegisterOutputReverseSwitchHotkey(outputReverseSwitchHotkey)
                : IsCurrentLogicalHotkeyRegistered(outputReverseSwitchHotkey, AppConstants.Hotkeys.OutputReverseSwitchHotkeyId);
            bool inputReverseSwitchRegistered = inputReverseSwitchAttempted
                ? _hotkeys.RegisterInputReverseSwitchHotkey(inputReverseSwitchHotkey)
                : IsCurrentLogicalHotkeyRegistered(inputReverseSwitchHotkey, AppConstants.Hotkeys.InputReverseSwitchHotkeyId);

            return new SwitchHotkeyRegistrationResult(
                outputSwitchRegistered,
                inputSwitchRegistered,
                outputReverseSwitchRegistered,
                inputReverseSwitchRegistered,
                outputSwitchAttempted,
                inputSwitchAttempted,
                outputReverseSwitchAttempted,
                inputReverseSwitchAttempted);
        }

        private static bool AreEquivalentRoutineHotkeyBinding(AudioRoutine left, AudioRoutine right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                left.Enabled == right.Enabled &&
                string.Equals(left.HotkeyCycleGroup, right.HotkeyCycleGroup, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.OutputDeviceId, right.OutputDeviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.OutputDeviceName, right.OutputDeviceName, StringComparison.Ordinal) &&
                string.Equals(left.InputDeviceId, right.InputDeviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.InputDeviceName, right.InputDeviceName, StringComparison.Ordinal) &&
                left.MasterVolumePercent == right.MasterVolumePercent &&
                left.MicVolumePercent == right.MicVolumePercent &&
                left.CommunicationsOutput == right.CommunicationsOutput && left.CommunicationsInput == right.CommunicationsInput &&
                left.OutputMuteAction == right.OutputMuteAction && left.InputMuteAction == right.InputMuteAction &&
                left.TriggerFingerprint == right.TriggerFingerprint &&
                left.Conditions == right.Conditions && left.TriggerDevice == right.TriggerDevice && left.DeviceTransition == right.DeviceTransition &&
                left.TriggerKind == right.TriggerKind &&
                string.Equals(left.TriggerAppPath, right.TriggerAppPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(left.TargetAppPath, right.TargetAppPath, StringComparison.OrdinalIgnoreCase) &&
                left.SwitchOutputPerApp == right.SwitchOutputPerApp &&
                left.RestorePreviousAudioOnDeactivate == right.RestorePreviousAudioOnDeactivate &&
                left.EnforceTargetsOnDeviceChange == right.EnforceTargetsOnDeviceChange;
        }

        private static int ResolveRoutineHotkeyId(int preferredId, HashSet<int> usedIds)
        {
            if (preferredId >= AppConstants.Hotkeys.RoutineHotkeyIdBase &&
                preferredId < AppConstants.Hotkeys.RoutineHotkeyIdBase + AppConstants.Hotkeys.RoutineHotkeyIdMaxCount &&
                usedIds.Add(preferredId))
            {
                return preferredId;
            }

            for (int offset = 0; offset < AppConstants.Hotkeys.RoutineHotkeyIdMaxCount; offset++)
            {
                int candidateId = AppConstants.Hotkeys.RoutineHotkeyIdBase + offset;
                if (usedIds.Add(candidateId))
                {
                    return candidateId;
                }
            }

            throw new InvalidOperationException("No routine hotkey ids available.");
        }

        private bool IsCurrentLogicalHotkeyRegistered(string? configuredHotkey, int id)
        {
            if (string.IsNullOrWhiteSpace(configuredHotkey))
            {
                return true;
            }

            HotkeyRegistrationOutcome outcome = _hotkeys.GetLastRegistrationOutcome(id);
            return outcome.Kind is HotkeyRegistrationOutcomeKind.Registered or HotkeyRegistrationOutcomeKind.Fallback;
        }

        private bool AreCurrentLogicalHotkeysRegistered(params (string? Hotkey, int Id)[] registrations)
        {
            foreach ((string? hotkey, int id) in registrations)
            {
                if (!IsCurrentLogicalHotkeyRegistered(hotkey, id))
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] NormalizeStrings(IEnumerable<string>? values)
        {
            return values?
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? [];
        }
    }
}
