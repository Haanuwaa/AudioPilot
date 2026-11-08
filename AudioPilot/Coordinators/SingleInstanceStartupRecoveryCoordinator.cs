using AudioPilot.Constants;
using AudioPilot.Localization;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct SingleInstanceStartupRecoveryResult(
        bool ContinueStartup,
        int ExitCode,
        string? FailureReason = null);

    internal sealed class SingleInstanceStartupRecoveryCoordinator(
        Logger logger,
        IAppDialogService dialogs,
        Func<SingleInstanceOwnerIdentity, ISingleInstanceRecoveryTarget?>? openTarget = null)
    {
        private readonly Logger _logger = logger;
        private readonly IAppDialogService _dialogs = dialogs;
        private readonly Func<SingleInstanceOwnerIdentity, ISingleInstanceRecoveryTarget?> _openTarget =
            openTarget ?? (owner => SingleInstanceProcessRecoveryHelper.TryOpen(owner, logger));

        internal static async Task<SingleInstanceAcquireResult> WaitForStartupHandoffAsync(
            SingleInstanceAcquireResult result,
            Func<SingleInstanceAcquireResult> tryAcquire,
            TimeProvider? timeProvider = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsHeadlessOwner(result) && result.FailureKind != SingleInstanceSignalFailureKind.ConnectionFailed) return result;
            timeProvider ??= TimeProvider.System;
            long started = timeProvider.GetTimestamp();
            while (IsHeadlessOwner(result) || result.FailureKind == SingleInstanceSignalFailureKind.ConnectionFailed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = AppConstants.Timing.SingleInstanceHeadlessHandoffTimeoutMs
                    - (int)timeProvider.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0) return result;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, AppConstants.Timing.SingleInstanceListenerRetryDelayMs)),
                    timeProvider, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                remaining = AppConstants.Timing.SingleInstanceHeadlessHandoffTimeoutMs
                    - (int)timeProvider.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0) return result;
                result = tryAcquire();
            }
            return result;
        }

        private static bool IsHeadlessOwner(SingleInstanceAcquireResult result) =>
            result.ExistingHealthy && string.Equals(result.ResponseErrorCode, SingleInstanceHelper.HeadlessBusyErrorCode, StringComparison.Ordinal);

        internal static bool NeedsRecovery(SingleInstanceAcquireResult result) =>
            !result.Acquired && (!result.ExistingHealthy || result.ResponseErrorCode is "activation-expired" or "activation-failed"
                or "forwarded-command-expired" or "forwarded-command-outcome-unknown" or "runtime-host-unavailable" or "ui-host-unavailable");

        internal async Task<SingleInstanceStartupRecoveryResult> ResolveAsync(
            SingleInstanceAcquireResult initialResult,
            Func<SingleInstanceAcquireResult> tryAcquire,
            Func<CancellationToken, Task<SingleInstanceOwnerProbe>> probeOwner,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool acquisitionFailed = initialResult.Disposition == SingleInstanceAcquireDisposition.AcquisitionFailed;
            SingleInstanceOwnerProbe probe = acquisitionFailed ? default : await probeOwner(cancellationToken);
            using ISingleInstanceRecoveryTarget? target = probe is { State: SingleInstanceOwnerState.Unresponsive, Identity: { } owner }
                ? _openTarget(owner)
                : null;
            bool canTerminate = target is { HasExited: false };
            AppDialogResult choice = await PromptForRecoveryAsync(acquisitionFailed, probe.State, canTerminate, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            string failureReason;
            if (choice == AppDialogResult.Retry)
            {
                _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryRetry);
                failureReason = "retry-unresponsive";
            }
            else if (choice == AppDialogResult.TerminateExisting && canTerminate && target != null && probe.Identity is { } confirmedOwner)
            {
                string? recoveryFailure = await RecoverOwnerAsync(confirmedOwner, target, probeOwner, cancellationToken);
                failureReason = recoveryFailure ?? "reacquire-failed";
                if (recoveryFailure == null) _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryOwnerExited);
            }
            else
            {
                return new SingleInstanceStartupRecoveryResult(false, 0, "cancelled");
            }

            cancellationToken.ThrowIfCancellationRequested();
            SingleInstanceAcquireResult result = tryAcquire();
            if (choice == AppDialogResult.TerminateExisting)
                result = await WaitForStartupHandoffAsync(result, tryAcquire, cancellationToken: cancellationToken);
            if (result.Acquired) return new(true, 0);
            if (result.ExistingHealthy) return await ResolveResponsiveExistingInstanceAsync(result, cancellationToken);

            if (result.Disposition == SingleInstanceAcquireDisposition.AcquisitionFailed) failureReason = "acquisition-failed";
            _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryReacquireFailed} | reason={failureReason} disposition={result.Disposition} failureKind={result.FailureKind}");
            var message = new LocalizedText(failureReason switch
            {
                "acquisition-failed" => "Message_AudioPilotCouldNotCheckWhetherAnotherInstance",
                "terminate-failed" or "terminate-timeout" => "Message_AudioPilotCouldNotCloseTheUnresponsiveInstance",
                _ => "Message_AudioPilotCouldNotCompleteStartupCloseThe",
            });
            await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(message, DialogText.Captions.StartupError, AppDialogKind.Error), cancellationToken);
            return new(false, 4, failureReason);
        }

        private async Task<string?> RecoverOwnerAsync(SingleInstanceOwnerIdentity confirmedOwner,
            ISingleInstanceRecoveryTarget target, Func<CancellationToken, Task<SingleInstanceOwnerProbe>> probeOwner,
            CancellationToken cancellationToken)
        {
            try
            {
                if (target.HasExited) return null;
                var expected = new SingleInstanceOwnerProbe(SingleInstanceOwnerState.Unresponsive, confirmedOwner);
                if (await probeOwner(cancellationToken) != expected) return "owner-no-longer-verified";
                cancellationToken.ThrowIfCancellationRequested();
                if (target.HasExited) return null;

                _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateStart);
                if (target.RequestClose())
                {
                    if (await target.WaitForExitAsync(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceRecoveryGracefulCloseTimeoutMs), cancellationToken))
                        return null;

                    if (await probeOwner(cancellationToken) != expected) return "owner-no-longer-verified";
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (target.HasExited) return null;
                target.Terminate();
                return await target.WaitForExitAsync(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceRecoveryKillWaitTimeoutMs), cancellationToken)
                    ? null : "terminate-timeout";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateFailed, nameof(RecoverOwnerAsync), ex);
                return "terminate-failed";
            }
        }

        private async Task<SingleInstanceStartupRecoveryResult> ResolveResponsiveExistingInstanceAsync(
            SingleInstanceAcquireResult result, CancellationToken cancellationToken)
        {
            int exitCode = result.ResponseExitCode.GetValueOrDefault();
            if (exitCode == 0)
            {
                return new SingleInstanceStartupRecoveryResult(false, 0, "healthy-existing-instance");
            }

            await _dialogs.ShowAsync(AppDialogRequest.Acknowledge(App.BuildActivationHandoffFailureMessage(result.ResponseErrorCode),
                DialogText.Captions.StartupError, AppDialogKind.Error), cancellationToken);
            return new SingleInstanceStartupRecoveryResult(false, exitCode, result.ResponseErrorCode ?? "activation-failed");
        }

        private Task<AppDialogResult> PromptForRecoveryAsync(bool acquisitionFailed, SingleInstanceOwnerState state,
            bool canTerminate, CancellationToken cancellationToken)
        {
            var message = new LocalizedText(acquisitionFailed ? "Message_AudioPilotCouldNotCheckWhetherAnotherInstanceText" : state switch
            {
                SingleInstanceOwnerState.Starting => "Message_AudioPilotIsStillStartingTryAgainIn",
                SingleInstanceOwnerState.Stopping => "Message_AudioPilotIsShuttingDownTryAgainIn",
                SingleInstanceOwnerState.Headless => "Message_AnAudioPilotCommandIsStillRunningTry",
                SingleInstanceOwnerState.Unresponsive => "Message_TheRunningAudioPilotWindowIsNotResponding",
                SingleInstanceOwnerState.Responsive => "Message_AudioPilotIsRunningButCouldNotComplete",
                _ => "Message_AudioPilotCouldNotCompleteTheRequestTo",
            });
            if (canTerminate) message = new("Dialog_GuidanceAndDetails", message, new LocalizedText("Message_TerminatingItCanInterruptPendingChanges"));

            List<AppDialogAction> actions = [new("_Retry", AppDialogResult.Retry, AppDialogActionStyle.Primary, isDefault: true)
                { LabelText = new("Dialog_Retry") }];
            if (canTerminate) actions.Add(new("_Terminate and continue", AppDialogResult.TerminateExisting, AppDialogActionStyle.Destructive)
            { LabelText = new("Message_TerminateAndContinue") });
            actions.Add(new("E_xit", AppDialogResult.Cancelled, AppDialogActionStyle.Secondary, isCancel: true)
            { LabelText = new("Message_EXit") });
            return _dialogs.ShowAsync(new AppDialogRequest(
                LocalizationService.English.Format(message),
                DialogText.Captions.StartupError,
                AppDialogKind.Warning,
                actions)
            {
                MessageResource = message,
                CaptionResource = new("Dialog_StartupError"),
            }, cancellationToken);
        }
    }
}
