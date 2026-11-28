using AudioPilot.Constants;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Diagnostics;

internal sealed record DiagnosticReviewTarget(int TabIndex, string? SearchQuery = null);

/// <summary>Static next steps for recorded states, with no probes or automatic configuration changes.</summary>
internal sealed record DiagnosticAdvice(string Summary, string NextStep, DiagnosticReviewTarget? Target = null)
{
    public bool CanReview => Target != null;

    internal static IReadOnlyList<DiagnosticAdvice> ForHealth(RuntimeHealthSnapshot health, AudioStatusSnapshot? checkedDefaults = null, LocalizationService? language = null)
    {
        var localization = language ?? LocalizationService.English;
        var advice = new List<DiagnosticAdvice>();
        if (health.AudioWorker is { Busy: true, ActiveOperationElapsedMs: >= 5000 })
            advice.Add(new(localization["Advice_AnAudioCallIsTakingLongerThan"],
                localization["Advice_QueuedAudioWorkMayBeWaitingFor"]));
        if (health.Hotkeys is { } hotkeys)
            foreach (var key in hotkeys.Registrations.Where(static key => key.LastOutcome is not ("None" or "Registered" or "Fallback")).Take(8))
                advice.Add(ForHotkey(key, localization));
        bool outputMissing = checkedDefaults != null ? checkedDefaults.Output.Kind == AudioEndpointStatusKind.NoDevice
            : health.Audio is { Stale: false, RefreshPending: false, OutputDefault: "Unavailable" };
        bool inputMissing = checkedDefaults != null ? checkedDefaults.Input.Kind == AudioEndpointStatusKind.NoDevice
            : health.Audio is { Stale: false, RefreshPending: false, InputDefault: "Unavailable" };
        if (outputMissing || inputMissing)
            advice.Add(new(localization["Advice_ADefaultAudioDeviceIsUnavailable"],
                localization["Advice_IfYouNeedThisAudioDirectionConnect"], new(outputMissing ? 0 : 1)));
        if (health.Settings is { LastSaveSucceeded: false })
            advice.Add(new(localization["Advice_YourLastSettingsSaveDidNotComplete"],
                localization["Advice_KeepYourUnsavedEditsCheckThatAudioPilot"], new(3)));
        if (health.Logging is { Level: not "None" } logging && (logging.FileDirectoryError != null || logging.LastWriteSucceeded == false))
            advice.Add(new(localization[logging.FileDirectoryError == null ? "Advice_TheLastLogFileWriteFailed" : "Diag_LogDirectoryUnavailable"],
                localization["Advice_CheckAvailableDiskSpaceAndFileAccess"], new(3, "Log level")));
        else if (health.Logging is { Level: "None" })
            advice.Add(new(localization["Advice_FileLoggingIsDisabled"],
                localization["Advice_SupportBundlesStillIncludeRuntimeSnapshotsIf"], new(3, "Log level")));
        if (health.Logging is { RedactionEnabled: false })
            advice.Add(new(localization["Advice_SensitiveLogRedactionIsDisabled"],
                localization["Advice_ThisWindowStillCopiesAndExportsRedacted"], new(3, "Redact sensitive log content")));
        if (health.Recovery is { Succeeded: false })
            advice.Add(new(localization["Advice_RecoveryAfterSleepWasIncomplete"],
                localization["Advice_WaitForDevicesToReconnectAndReview"], new(3)));
        if (health.RoutineWork is { AutomaticPaused: true, HasAutomaticRoutines: not false })
            advice.Add(new(localization["Advice_AutomaticRoutinesArePaused"],
                localization["Advice_UseResumeInTheTrayMenuOr"], new(2)));
        if (health.ProcessMonitorMode == "polling")
            advice.Add(new(localization["Advice_ProcessMonitoringIsUsingItsPollingFallback"],
                localization["Advice_ApplicationRoutinesCanContinueUsingPollingRun"], new(2)));
        return [.. advice.Take(12)];
    }

    private static DiagnosticAdvice ForHotkey(HotkeyRegistrationSnapshot key, LocalizationService localization)
    {
        DiagnosticReviewTarget target = key.ActionId == AppConstants.Hotkeys.OutputSwitchHotkeyId ? new(0)
            : key.ActionId == AppConstants.Hotkeys.InputSwitchHotkeyId ? new(1)
            : key.ActionId is >= AppConstants.Hotkeys.RoutineHotkeyIdBase and < AppConstants.Hotkeys.RoutineHotkeyIdBase + AppConstants.Hotkeys.RoutineHotkeyIdMaxCount ? new(2) : new(3, Query(key.ActionId));
        string step = key.LastOutcome switch
        {
            "ExternalConflict" or "Duplicate" => localization["Advice_ThisShortcutIsAlreadyInUseChoose"],
            "Reserved" => localization["Advice_WindowsReservesThisShortcutChooseADifferent"],
            "ParseError" or "Unsupported" => localization["Advice_ChooseASupportedShortcutUsingTheCapture"],
            "KeyboardHookUnavailable" or "MouseHookUnavailable" => localization["Advice_TheRequiredInputHookWasUnavailableReview"],
            _ => localization["Advice_ReviewThisShortcutSAssignmentAndSave"],
        };
        return new(HotkeySyntax.FormatActionName(key.ActionId), step, target);

        static string? Query(int id) => id switch
        {
            AppConstants.Hotkeys.ToggleAppVisibilityHotkeyId => "Show/hide app",
            AppConstants.Hotkeys.ShowAudioStatusHotkeyId => "Show audio status",
            AppConstants.Hotkeys.QuickDevicePickerHotkeyId => "Device picker",
            AppConstants.Hotkeys.VolumeMixerHotkeyId => "Quick mixer",
            AppConstants.Hotkeys.MediaShowCurrentTrackId => "Show current track",
            AppConstants.Hotkeys.MediaPlayPauseId => "Play/pause",
            AppConstants.Hotkeys.MediaNextTrackId => "Next track",
            AppConstants.Hotkeys.MediaPrevTrackId => "Previous track",
            AppConstants.Hotkeys.MediaSeekForwardId => "Seek forward",
            AppConstants.Hotkeys.MediaSeekBackwardId => "Seek backward",
            AppConstants.Hotkeys.MuteMicId => "Microphone mute",
            AppConstants.Hotkeys.MuteSoundId => "Output mute",
            AppConstants.Hotkeys.DeafenId => "Deafen",
            AppConstants.Hotkeys.PushToTalkHotkeyId => "Push-to-talk",
            AppConstants.Hotkeys.HoldToMuteHotkeyId => "Hold-to-mute",
            AppConstants.Hotkeys.MasterVolumeUpHotkeyId => "Output volume up",
            AppConstants.Hotkeys.MasterVolumeDownHotkeyId => "Output volume down",
            AppConstants.Hotkeys.MicVolumeUpHotkeyId => "Microphone up",
            AppConstants.Hotkeys.MicVolumeDownHotkeyId => "Microphone down",
            AppConstants.Hotkeys.ForegroundVolumeUpHotkeyId => "Foreground app volume up",
            AppConstants.Hotkeys.ForegroundVolumeDownHotkeyId => "Foreground app volume down",
            AppConstants.Hotkeys.ForegroundMuteHotkeyId => "Foreground app mute",
            AppConstants.Hotkeys.ListenToInputHotkeyId => "Listen hotkey",
            AppConstants.Hotkeys.OutputReverseSwitchHotkeyId => "Output reverse",
            AppConstants.Hotkeys.InputReverseSwitchHotkeyId => "Input reverse",
            _ => null,
        };
    }

    internal static DiagnosticAdvice? ForOperation(string? code, string kind, string action, bool issue)
    {
        int audioTab = action.Contains("input", StringComparison.Ordinal) || action.Contains("mic", StringComparison.Ordinal) ? 1 : 0;
        return code switch
        {
            "switch-not-completed" => new("A switch was not confirmed",
                "Reconnection or a queued retry may still be pending. Check the current device before repeating the command.", new(audioTab)),
            "switch-failed" => new("The switch encountered an error",
                "Check the device connection and current Windows defaults before retrying.", new(audioTab)),
            "mute-change-partial" => new("Only part of the mute change was applied",
                "Check both microphone and output states before trying again.", new(audioTab)),
            "mute-change-failed" => new("The requested mute state could not be confirmed",
                "Check the current device and mute state before repeating the command.", new(audioTab)),
            "media-command-unconfirmed" => new("Playback change was not confirmed",
                "Check the player before retrying; an accepted command may already have taken effect."),
            _ when issue && kind == "routine" => new("Review the routine's result",
                "Check its last-run details and use Check routine to review current requirements and targets.", new(2)),
            _ when issue => new("Review this operation before retrying",
                "Check its recorded outcome and current app/device state. Copy this redacted operation when reporting the issue."),
            _ => null,
        };
    }
}
