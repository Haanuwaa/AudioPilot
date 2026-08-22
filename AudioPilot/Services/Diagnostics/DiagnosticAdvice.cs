using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Diagnostics;

internal sealed record DiagnosticReviewTarget(int TabIndex, string? SearchQuery = null);

/// <summary>Static next steps for recorded states, with no probes or automatic configuration changes.</summary>
internal sealed record DiagnosticAdvice(string Summary, string NextStep, DiagnosticReviewTarget? Target = null)
{
    public bool CanReview => Target != null;

    internal static IReadOnlyList<DiagnosticAdvice> ForHealth(RuntimeHealthSnapshot health)
    {
        var advice = new List<DiagnosticAdvice>();
        if (health.Hotkeys is { } hotkeys)
            foreach (var key in hotkeys.Registrations.Where(static key => key.LastOutcome is not ("None" or "Registered" or "Fallback")).Take(8))
                advice.Add(ForHotkey(key));
        if (health.Audio is { } audio)
        {
            if (audio.RefreshPending || audio.Stale)
                advice.Add(new("Audio information is cached",
                    "Allow a pending device refresh to finish, or use Refresh on Output/Input. Then refresh this diagnostic snapshot.",
                    new(audio.InputDefault == "Unavailable" && audio.OutputDefault == "Available" ? 1 : 0)));
            else if (audio.OutputDefault == "Unavailable" || audio.InputDefault == "Unavailable")
                advice.Add(new("A default audio device is unavailable",
                    "If you need this audio direction, connect and enable a device in Windows, then review its switch order.",
                    new(audio.OutputDefault == "Unavailable" ? 0 : 1)));
        }
        if (health.Settings is { LastSaveSucceeded: false })
            advice.Add(new("Your last settings save did not complete",
                "Keep your unsaved edits. Check that AudioPilot's data folder is writable, then retry Save settings or Apply settings.", new(3)));
        if (health.Logging is { Level: not "None", LastWriteSucceeded: false })
            advice.Add(new("The last log-file write failed",
                "Check available disk space and file access. A later successful write clears this failure.", new(3, "Log level")));
        else if (health.Logging is { Level: "None" })
            advice.Add(new("File logging is disabled",
                "Support bundles still include runtime snapshots. If logs are needed, choose Info or Debug, reproduce the issue, then export a bundle.", new(3, "Log level")));
        if (health.Logging is { RedactionEnabled: false })
            advice.Add(new("Sensitive log redaction is disabled",
                "This window still copies and exports redacted data. Review existing local logs before sharing them.", new(3, "Redact sensitive log content")));
        if (health.Recovery is { Succeeded: false })
            advice.Add(new("Recovery after sleep was incomplete",
                "Wait for devices to reconnect and review shortcut registration. If controls remain unavailable, restart AudioPilot.", new(3)));
        if (health.RoutineWork is { AutomaticPaused: true, HasAutomaticRoutines: not false })
            advice.Add(new("Automatic routines are paused",
                "Use Resume in the tray menu or Routines when ready. Manual commands remain available.", new(2)));
        if (health.ProcessMonitorMode == "polling")
            advice.Add(new("Process monitoring is using its polling fallback",
                "Application routines can continue using polling. Run the process monitor test only if investigating missed start/stop events.", new(2)));
        return [.. advice.Take(12)];
    }

    private static DiagnosticAdvice ForHotkey(HotkeyRegistrationSnapshot key)
    {
        DiagnosticReviewTarget target = key.ActionId is AppConstants.Hotkeys.OutputSwitchHotkeyId or AppConstants.Hotkeys.OutputReverseSwitchHotkeyId ? new(0)
            : key.ActionId is AppConstants.Hotkeys.InputSwitchHotkeyId or AppConstants.Hotkeys.InputReverseSwitchHotkeyId ? new(1)
            : key.ActionId is >= AppConstants.Hotkeys.RoutineHotkeyIdBase and < AppConstants.Hotkeys.RoutineHotkeyIdBase + AppConstants.Hotkeys.RoutineHotkeyIdMaxCount ? new(2) : new(3, Query(key.ActionId));
        string step = key.LastOutcome switch
        {
            "ExternalConflict" or "Duplicate" => "This shortcut is already in use. Choose another shortcut or free the existing assignment, then save your changes.",
            "Reserved" => "Windows reserves this shortcut. Choose a different combination and save your changes.",
            "ParseError" or "Unsupported" => "Choose a supported shortcut using the capture field, then save your changes.",
            "KeyboardHookUnavailable" or "MouseHookUnavailable" => "The required input hook was unavailable. Review the assignment; restart AudioPilot if the problem persists.",
            _ => "Review this shortcut's assignment and save again. The technical result remains in Hotkey registration.",
        };
        return new(HotkeyDisplayFormatter.FormatActionName(key.ActionId), step, target);

        static string? Query(int id) => id switch
        {
            AppConstants.Hotkeys.MuteMicId => "Microphone mute",
            AppConstants.Hotkeys.MuteSoundId => "Output mute",
            AppConstants.Hotkeys.DeafenId => "Deafen",
            AppConstants.Hotkeys.MediaPlayPauseId => "Play/pause",
            AppConstants.Hotkeys.MediaNextTrackId => "Next track",
            AppConstants.Hotkeys.MediaPrevTrackId => "Previous track",
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
