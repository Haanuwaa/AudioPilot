using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal enum SingleInstanceRecoveryPromptResult
    {
        Retry = 0,
        TerminateExistingAndContinue = 1,
        Cancel = 2,
    }

    internal readonly record struct SingleInstanceStartupRecoveryResult(
        bool ContinueStartup,
        int ExitCode,
        string? FailureReason = null);

    internal sealed class SingleInstanceStartupRecoveryCoordinator(
        SingleInstanceProcessRecoveryHelper processRecoveryHelper,
        Logger logger,
        IAppDialogService? dialogs = null,
        Func<Task<SingleInstanceRecoveryPromptResult>>? promptForRecovery = null,
        Func<string, string, Task<AppDialogResult>>? showError = null)
    {
        private readonly SingleInstanceProcessRecoveryHelper _processRecoveryHelper = processRecoveryHelper;
        private readonly Logger _logger = logger;
        private readonly IAppDialogService _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        private readonly Func<Task<SingleInstanceRecoveryPromptResult>>? _promptForRecovery = promptForRecovery;
        private readonly Func<string, string, Task<AppDialogResult>>? _showError = showError;

        internal async Task<SingleInstanceStartupRecoveryResult> ResolveAsync(Func<SingleInstanceAcquireResult> tryAcquire)
        {
            SingleInstanceRecoveryPromptResult promptResult = _promptForRecovery == null
                ? await PromptForRecoveryAsync()
                : await _promptForRecovery();
            return promptResult switch
            {
                SingleInstanceRecoveryPromptResult.Retry => await RetryAcquireAsync(tryAcquire),
                SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue => await TerminateAndReacquireAsync(tryAcquire),
                _ => new SingleInstanceStartupRecoveryResult(false, 0, "cancelled"),
            };
        }

        private async Task<SingleInstanceStartupRecoveryResult> RetryAcquireAsync(Func<SingleInstanceAcquireResult> tryAcquire)
        {
            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryRetry);
            }

            SingleInstanceAcquireResult retryResult = tryAcquire();
            if (retryResult.Acquired)
            {
                return new SingleInstanceStartupRecoveryResult(true, 0);
            }

            if (retryResult.ExistingHealthy)
            {
                return await ResolveResponsiveExistingInstanceAsync(retryResult);
            }

            await ShowErrorAsync(
                "AudioPilot is still not responding. Close the existing instance or try again later.",
                DialogText.Captions.StartupError);
            return new SingleInstanceStartupRecoveryResult(false, 4, "retry-unresponsive");
        }

        private async Task<SingleInstanceStartupRecoveryResult> TerminateAndReacquireAsync(Func<SingleInstanceAcquireResult> tryAcquire)
        {
            SingleInstanceAcquireResult preTerminationResult = tryAcquire();
            if (preTerminationResult.Acquired)
            {
                return new SingleInstanceStartupRecoveryResult(true, 0);
            }

            if (preTerminationResult.ExistingHealthy)
            {
                return await ResolveResponsiveExistingInstanceAsync(preTerminationResult);
            }

            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateStart);
            }

            SingleInstanceProcessRecoveryResult terminationResult = _processRecoveryHelper.TryTerminateMatchingExistingProcess();
            if (!terminationResult.Success)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateFailed} | reason={(terminationResult.FailureReason ?? "unknown")} matched={terminationResult.MatchedProcessCount}");
                }

                await ShowErrorAsync(
                    "AudioPilot could not terminate the unresponsive existing instance.",
                    DialogText.Captions.StartupError);
                return new SingleInstanceStartupRecoveryResult(false, 4, terminationResult.FailureReason ?? "terminate-failed");
            }

            if (_logger.IsEnabled(LogLevel.Info))
            {
                _logger.Info("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryTerminateSuccess} | matched={terminationResult.MatchedProcessCount}");
            }

            SingleInstanceAcquireResult reacquireResult = tryAcquire();
            if (reacquireResult.Acquired)
            {
                return new SingleInstanceStartupRecoveryResult(true, 0);
            }

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Warning("App", () => $"{AppConstants.Audio.LogEvents.SingleInstance.RecoveryReacquireFailed} | disposition={reacquireResult.Disposition} failureKind={reacquireResult.FailureKind}");
            }

            await ShowErrorAsync(
                "AudioPilot could not restart after terminating the unresponsive instance.",
                DialogText.Captions.StartupError);
            return new SingleInstanceStartupRecoveryResult(false, 4, "reacquire-failed");
        }

        private async Task<SingleInstanceStartupRecoveryResult> ResolveResponsiveExistingInstanceAsync(
            SingleInstanceAcquireResult result)
        {
            int exitCode = result.ResponseExitCode.GetValueOrDefault();
            if (exitCode == 0)
            {
                return new SingleInstanceStartupRecoveryResult(false, 0, "healthy-existing-instance");
            }

            await ShowErrorAsync(
                App.BuildActivationHandoffFailureMessage(result.ResponseErrorCode),
                DialogText.Captions.StartupError);
            return new SingleInstanceStartupRecoveryResult(false, exitCode, result.ResponseErrorCode ?? "activation-failed");
        }

        private async Task<SingleInstanceRecoveryPromptResult> PromptForRecoveryAsync()
        {
            AppDialogResult result = await _dialogs.ShowAsync(new AppDialogRequest(
                "AudioPilot appears to be running but is not responding.",
                DialogText.Captions.StartupError,
                AppDialogKind.Warning,
                [
                    new AppDialogAction("_Retry", AppDialogResult.Retry, AppDialogActionStyle.Primary, isDefault: true),
                    new AppDialogAction("_Terminate and continue", AppDialogResult.TerminateExisting, AppDialogActionStyle.Destructive),
                    new AppDialogAction("E_xit", AppDialogResult.Cancelled, AppDialogActionStyle.Secondary, isCancel: true),
                ]));

            return result switch
            {
                AppDialogResult.Retry => SingleInstanceRecoveryPromptResult.Retry,
                AppDialogResult.TerminateExisting => SingleInstanceRecoveryPromptResult.TerminateExistingAndContinue,
                _ => SingleInstanceRecoveryPromptResult.Cancel,
            };
        }

        private Task<AppDialogResult> ShowErrorAsync(string message, string caption)
        {
            return _showError == null
                ? _dialogs.ShowErrorAsync(message, caption)
                : _showError(message, caption);
        }
    }
}
