namespace AudioPilot.Updater.Ui;

internal sealed record UpdaterPresentation(string Heading, string Message, string Progress, bool Indeterminate,
    bool CanBrowse, bool CanVerify, bool CanInstall, bool CanPause, bool CanResume, bool CanCancel, bool CanRecover)
{
    public static UpdaterPresentation From(UpdateJobState state, bool busy, bool selected, bool remote = false)
    {
        bool initial = state.Phase is UpdateJobPhase.Idle or UpdateJobPhase.Available or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack;
        var (heading, message) = state.Phase switch
        {
            UpdateJobPhase.Idle => remote ? ("Check for an update", "Check signed update information. No package is downloaded without your approval.") : ("Choose an update package", "Select the local package prepared for this test installation."),
            UpdateJobPhase.Checking => ("Checking update information", "Checking trusted metadata and the installed version."),
            UpdateJobPhase.Available => ("Update available", "Signed update information is ready. Download only starts after your confirmation."),
            UpdateJobPhase.Staging => (remote ? "Downloading the package" : "Copying the package", "You can pause and resume this transfer later."),
            UpdateJobPhase.Paused => (remote ? "Download paused" : "Copy paused", "Your progress is saved. Resume to finish verifying this package."),
            UpdateJobPhase.Verifying => ("Verifying the package", "Checking the complete package before AudioPilot is prepared for restart."),
            UpdateJobPhase.Ready => ("Ready to install", "The package is verified. Choose Install and restart when you are ready."),
            UpdateJobPhase.Preparing => ("Preparing AudioPilot", "Saving pending changes and checking that microphone control can be released safely."),
            UpdateJobPhase.Deferred => ("Update is waiting", DeferredMessage(state.Reason)),
            UpdateJobPhase.Installing => ("Updating AudioPilot", "Keep Windows running. Closing this window lets the update finish safely in the background."),
            UpdateJobPhase.Recovering => ("Restoring AudioPilot", "Finishing recovery before AudioPilot resumes."),
            UpdateJobPhase.Completed => ("AudioPilot was updated", "The update was committed and AudioPilot restarted successfully."),
            UpdateJobPhase.RolledBack => ("AudioPilot was restored", "The update was not applied. Your previous installation and settings were restored."),
            UpdateJobPhase.Cancelled => ("Update cancelled", "Managed staging files were removed. The original package was kept."),
            UpdateJobPhase.RecoveryRequired => ("Recovery needs attention", "Retry recovery for this workspace. The updater will not start another installation."),
            _ => ("The update could not continue", remote ? "Retry the download or cancel this attempt. Technical details are available below." : "Cancel this attempt to select a package again. Technical details are available below.")
        };
        string progress = state.Artifact is { } artifact ? $"{Math.Min(state.Bytes, artifact.Length) / 1048576d:0.0} of {artifact.Length / 1048576d:0.0} MB" : "";
        return new(heading, message, progress, busy && state.Phase != UpdateJobPhase.Staging,
            !busy && initial, !busy && initial && selected, !busy && state.Phase is UpdateJobPhase.Ready or UpdateJobPhase.Deferred,
            busy && state.Phase is UpdateJobPhase.Staging or UpdateJobPhase.Verifying, !busy && (state.Phase == UpdateJobPhase.Paused || remote && state.Phase == UpdateJobPhase.Failed),
            state.Phase is UpdateJobPhase.Checking or UpdateJobPhase.Available or UpdateJobPhase.Staging or UpdateJobPhase.Verifying or UpdateJobPhase.Preparing or UpdateJobPhase.Ready or UpdateJobPhase.Deferred or UpdateJobPhase.Paused or UpdateJobPhase.Failed,
            !busy && state.Phase == UpdateJobPhase.RecoveryRequired);
    }
    private static string DeferredMessage(string? reason) => reason switch
    {
        "microphone-owned" or "microphone-control-active" or "microphone-hold-active" => "Release microphone hold controls or disable push-to-talk, then try again. AudioPilot is still running.",
        "routine-work-active" => "A routine is still running. Try again when it finishes. AudioPilot is still running.",
        "settings-busy" => "Settings are being saved. Try again in a moment. AudioPilot is still running.",
        "unsaved-changes" => "Save or discard pending changes in AudioPilot, then try again.",
        "dialog-active" or "interaction-active" => "Finish the open dialog or active interaction in AudioPilot, then try again.",
        "resume-in-progress" => "AudioPilot is recovering from sleep. Try again when it is ready.",
        _ => "AudioPilot is not ready to restart safely. Resolve the condition shown in details, then try again."
    };
}
