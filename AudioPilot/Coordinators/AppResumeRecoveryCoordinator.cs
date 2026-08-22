using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal readonly record struct ResumeRecoveryExecutionResult(
        bool Succeeded,
        int HotkeyAttempts,
        int HotkeyFailedCount);

    internal readonly record struct ResumeRecoveryExecutionDependencies(
        Func<Task> RecoverAudioAsync,
        Func<string, Task<(AppViewModel.ResumeHotkeyRegistrationResult Result, int Attempts)>> RegisterHotkeysAsync,
        Func<Task> RefreshDevicesAsync);

    internal static class AppResumeRecoveryCoordinator
    {
        public static string ResolveOperationId(string? resumeOpId)
        {
            return string.IsNullOrWhiteSpace(resumeOpId)
                ? $"resume:{Guid.NewGuid():N}"
                : resumeOpId;
        }

        public static bool ShouldRetryHotkeyRegistration(AppViewModel.ResumeHotkeyRegistrationResult result, int attempt)
        {
            return attempt == 1 && !result.AllSucceeded;
        }

        public static async Task<(AppViewModel.ResumeHotkeyRegistrationResult Result, int Attempts)> RegisterHotkeysAsync(
            Func<Task<AppViewModel.ResumeHotkeyRegistrationResult>> registerAttemptAsync,
            int retryDelayMs,
            ILogger logger,
            string resumeOpId,
            CancellationToken cancellationToken = default)
        {
            AppViewModel.ResumeHotkeyRegistrationResult registrationResult = await registerAttemptAsync();
            int attempts = 1;

            if (ShouldRetryHotkeyRegistration(registrationResult, attempts))
            {
                await Task.Delay(retryDelayMs, cancellationToken);
                registrationResult = await registerAttemptAsync();
                attempts = 2;
            }

            logger.Info(
                "AppViewModel",
                $"{AppConstants.Audio.LogEvents.ResumeRecovery.HotkeysRegister} | opId={resumeOpId} attempts={attempts} failedCount={registrationResult.FailedCount} toggleAppVisibility={registrationResult.ToggleAppVisibilityRegistered} media={registrationResult.MediaHotkeysRegistered} mute={registrationResult.MuteHotkeysRegistered} listen={registrationResult.ListenToInputRegistered} volumeStep={registrationResult.VolumeStepHotkeysRegistered} output={registrationResult.OutputSwitchRegistered} input={registrationResult.InputSwitchRegistered} outputReverse={registrationResult.OutputReverseSwitchRegistered} inputReverse={registrationResult.InputReverseSwitchRegistered} routines={registrationResult.RoutineHotkeysRegistered}");

            return (registrationResult, attempts);
        }

        /// <summary>
        /// Executes the coordinated resume-recovery pipeline for audio state, hotkeys, and device refresh.
        /// </summary>
        /// <remarks>
        /// Audio recovery and device refresh run once per resume attempt. Hotkey registration may retry once on the
        /// first partial failure so transient resume races can settle before the final summary is logged.
        /// </remarks>
        public static async Task<ResumeRecoveryExecutionResult> ExecuteAsync(
            string opId,
            ResumeRecoveryExecutionDependencies dependencies,
            ILogger logger,
            string failureMethodName,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            bool recoverySucceeded = false;
            int hotkeyAttempts = 0;
            int hotkeyFailedCount = 0;

            logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Start} | opId={opId}");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dependencies.RecoverAudioAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var (result, attempts) = await dependencies.RegisterHotkeysAsync(opId);
                hotkeyAttempts = attempts;
                hotkeyFailedCount = result.FailedCount;
                cancellationToken.ThrowIfCancellationRequested();
                await dependencies.RefreshDevicesAsync();
                recoverySucceeded = result.AllSucceeded;
                if (recoverySucceeded)
                {
                    logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Success} | opId={opId}");
                }
                else
                {
                    logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Failed} | opId={opId} reason=hotkey-registration-partial failedCount={hotkeyFailedCount}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Skip} | opId={opId} reason=shutdown-canceled");
            }
            catch (Exception ex)
            {
                logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Failed} | opId={opId}", failureMethodName, ex);
            }
            finally
            {
                stopwatch.Stop();
                logger.Info(
                    "AppViewModel",
                    $"{AppConstants.Audio.LogEvents.ResumeRecovery.Summary} | opId={opId} durationMs={stopwatch.Elapsed.TotalMilliseconds:F1} success={recoverySucceeded} hotkeyAttempts={hotkeyAttempts} hotkeyFailedCount={hotkeyFailedCount}");
            }

            return new ResumeRecoveryExecutionResult(recoverySucceeded, hotkeyAttempts, hotkeyFailedCount);
        }
    }
}
