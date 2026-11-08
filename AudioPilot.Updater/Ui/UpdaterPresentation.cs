using AudioPilot.Localization;

namespace AudioPilot.Updater.Ui;

internal sealed record UpdaterPresentation(string Heading, string Message, string VersionSummary, string Progress, bool Indeterminate,
    bool CanBrowse, bool CanVerify, bool CanInstall, bool CanPause, bool CanResume, bool CanCancel, bool CanRecover)
{
    public static UpdaterPresentation From(UpdateJobState state, bool busy, bool selected, bool remote = false, LocalizationService? language = null)
    {
        var localization = language ?? LocalizationService.Default;
        bool initial = state.Phase is UpdateJobPhase.Idle or UpdateJobPhase.Available or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack or UpdateJobPhase.Current or UpdateJobPhase.Unavailable;
        var (heading, message) = state.Phase switch
        {
            UpdateJobPhase.Idle => remote ? (localization["Update_CheckForAnUpdate"], localization["Update_CheckSignedUpdateInformationNoPackageIs"]) : (localization["Update_ChooseAnUpdatePackage"], localization["Update_SelectTheLocalPackagePreparedForThis"]),
            UpdateJobPhase.Current => (state.Reason == "no-release" ? localization["Update_NoReleaseAvailable"] : localization["Update_YouAreUpToDate"], state.Reason == "no-release" ? localization["Update_NoStableReleaseIsAvailableInThis"] : localization["Update_TheInstalledVersionIsCurrentNothingWas"]),
            UpdateJobPhase.Unavailable => (localization["Update_AManualUpdateIsNeeded"], state.Reason == "bootstrap-required" ? localization["Update_TheLatestReleaseNeedsANewerUpdater"] : localization["Update_TheLatestReleaseHasNoPackageMatching"]),
            UpdateJobPhase.Checking => (localization["Update_CheckingUpdateInformation"], localization["Update_CheckingTrustedMetadataAndTheInstalledVersion"]),
            UpdateJobPhase.Available => (localization["Update_UpdateAvailable"], localization["Update_SignedUpdateInformationIsReadyDownloadOnly"]),
            UpdateJobPhase.Staging => (remote ? localization["Update_DownloadingThePackage"] : localization["Update_CopyingThePackage"], localization["Update_YouCanPauseAndResumeThisTransfer"]),
            UpdateJobPhase.Paused => (remote ? localization["Update_DownloadPaused"] : localization["Update_CopyPaused"], localization["Update_YourProgressIsSavedResumeToFinish"]),
            UpdateJobPhase.Verifying => (localization["Update_VerifyingThePackage"], localization["Update_CheckingTheCompletePackageBeforeAudioPilotIs"]),
            UpdateJobPhase.Ready => (localization["Update_ReadyToInstall"], localization["Update_ThePackageIsVerifiedChooseUpdateAnd"]),
            UpdateJobPhase.Preparing => (localization["Update_PreparingAudioPilot"], localization["Update_SavingPendingChangesAndCheckingThatMicrophone"]),
            UpdateJobPhase.Deferred => (localization["Update_UpdateIsWaiting"], DeferredMessage(state.Reason, localization)),
            UpdateJobPhase.Installing => (localization["Update_UpdatingAudioPilot"], localization["Update_KeepWindowsRunningClosingThisWindowLets"]),
            UpdateJobPhase.Recovering => (localization["Update_RestoringAudioPilot"], localization["Update_FinishingRecoveryBeforeAudioPilotResumes"]),
            UpdateJobPhase.Completed => (localization["Update_AudioPilotWasUpdated"], localization["Update_TheUpdateWasCommittedAndAudioPilotRestarted"]),
            UpdateJobPhase.RolledBack => (localization["Update_AudioPilotWasRestored"], localization["Update_TheUpdateWasNotAppliedYourPrevious"]),
            UpdateJobPhase.Cancelled => (localization["Update_UpdateCancelled"], localization["Update_ManagedStagingFilesWereRemovedTheOriginal"]),
            UpdateJobPhase.RecoveryRequired => (localization["Update_RecoveryNeedsAttention"], state.Reason == "installer-reboot-required"
                ? localization["Update_InstallerRebootRecovery"] : localization["Update_RetryRecoveryForThisWorkspaceTheUpdater"]),
            UpdateJobPhase.Failed when state.FailurePhase == UpdateJobPhase.Checking || remote && state.Artifact == null =>
                (localization["Update_CouldNotCheckForUpdates"], FeedFailureMessage(state.Reason, localization)),
            UpdateJobPhase.Failed when AudioPilot.Updater.Trust.UpdateFeedException.IsKnown(state.Reason ?? "") =>
                (localization["Update_TheUpdateCouldNotContinue"], FeedFailureMessage(state.Reason, localization)),
            UpdateJobPhase.Failed when state.FailurePhase == UpdateJobPhase.Preparing =>
                (localization["Update_AudioPilotCouldNotPrepareToRestart"], localization["Update_NoInstallationWasStartedReviewDetailsResolve"]),
            UpdateJobPhase.Failed when remote && state.Reason == nameof(TimeoutException) =>
                (localization["Update_DownloadStalled"], localization["Update_ThePackageDownloadStoppedReceivingDataRetry"]),
            _ => (localization["Update_TheUpdateCouldNotContinue"], remote ? localization["Update_RetryTheDownloadOrCancelThisAttempt"] : localization["Update_CancelThisAttemptToSelectAPackage"])
        };
        string versionSummary = state.Phase switch
        {
            UpdateJobPhase.Idle => localization["Update_NotChecked"],
            UpdateJobPhase.Checking => localization["Update_CheckingUpdateInformation"],
            UpdateJobPhase.Current => heading,
            _ => state.Version ?? heading
        };
        string progress = state.Artifact is { } artifact ? localization.FormatKey("Update_000Of100", Math.Min(state.Bytes, artifact.Length) / 1048576d, artifact.Length / 1048576d) : "";
        return new(heading, message, versionSummary, progress, busy && state.Phase != UpdateJobPhase.Staging,
            !busy && initial, !busy && initial && selected, !busy && state.Phase is UpdateJobPhase.Ready or UpdateJobPhase.Deferred,
            busy && state.Phase is UpdateJobPhase.Staging or UpdateJobPhase.Verifying, !busy && (state.Phase == UpdateJobPhase.Paused || remote && state.Phase == UpdateJobPhase.Failed),
            state.Phase is UpdateJobPhase.Checking or UpdateJobPhase.Available or UpdateJobPhase.Staging or UpdateJobPhase.Verifying or UpdateJobPhase.Preparing or UpdateJobPhase.Ready or UpdateJobPhase.Deferred or UpdateJobPhase.Paused or UpdateJobPhase.Failed,
            !busy && state.Phase == UpdateJobPhase.RecoveryRequired);
    }
    private static string FeedFailureMessage(string? reason, LocalizationService localization) => reason switch
    {
        "feed-expired" => localization["Update_SignedUpdateInformationHasExpiredCheckYour"],
        "feed-trust-state" => localization["Update_SavedUpdateTrustInformationCouldNotBe"],
        "feed-network" or "feed-unavailable" => localization["Update_TheUpdateServerCouldNotBeReached"],
        "feed-rate-limited" => localization["Update_TheUpdateServerIsLimitingRequestsWait"],
        "feed-access-denied" => localization["Update_TheUpdateServerDeniedAccessCheckNetwork"],
        "update-no-longer-offered" => localization["Update_ThisOlderPackageIsNoLongerOffered"],
        _ => localization["Update_UpdateInformationCouldNotBeVerifiedNothing"]
    };
    private static string DeferredMessage(string? reason, LocalizationService localization) => reason switch
    {
        "microphone-owned" or "microphone-control-active" or "microphone-hold-active" => localization["Update_ReleaseMicrophoneHoldControlsOrDisablePush"],
        "routine-work-active" => localization["Update_ARoutineIsStillRunningTryAgain"],
        "settings-busy" => localization["Update_SettingsAreBeingSavedTryAgainIn"],
        "unsaved-changes" => localization["Update_SaveOrDiscardPendingChangesInAudioPilot"],
        "dialog-active" or "interaction-active" => localization["Update_FinishTheOpenDialogOrActiveInteraction"],
        "resume-in-progress" => localization["Update_AudioPilotIsRecoveringFromSleepTryAgain"],
        "installer-busy" => localization["Update_InstallerBusy"],
        _ => localization["Update_AudioPilotIsNotReadyToRestartSafely"]
    };
}
