using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct ResumeHotkeyRegistrationResult(
        bool ToggleAppVisibilityRegistered,
        bool MediaHotkeysRegistered,
        bool MuteHotkeysRegistered,
        bool ListenToInputRegistered,
        bool VolumeStepHotkeysRegistered,
        bool OutputSwitchRegistered,
        bool InputSwitchRegistered,
        bool OutputReverseSwitchRegistered,
        bool InputReverseSwitchRegistered,
        bool RoutineHotkeysRegistered = true)
    {
        public bool AllSucceeded =>
            ToggleAppVisibilityRegistered &&
            MediaHotkeysRegistered &&
            MuteHotkeysRegistered &&
            ListenToInputRegistered &&
            VolumeStepHotkeysRegistered &&
            OutputSwitchRegistered &&
            InputSwitchRegistered &&
            OutputReverseSwitchRegistered &&
            InputReverseSwitchRegistered &&
            RoutineHotkeysRegistered;

        public int FailedCount =>
            (ToggleAppVisibilityRegistered ? 0 : 1) +
            (MediaHotkeysRegistered ? 0 : 1) +
            (MuteHotkeysRegistered ? 0 : 1) +
            (ListenToInputRegistered ? 0 : 1) +
            (VolumeStepHotkeysRegistered ? 0 : 1) +
            (OutputSwitchRegistered ? 0 : 1) +
            (InputSwitchRegistered ? 0 : 1) +
            (OutputReverseSwitchRegistered ? 0 : 1) +
            (InputReverseSwitchRegistered ? 0 : 1) +
            (RoutineHotkeysRegistered ? 0 : 1);
    }

    internal readonly record struct ResumeRecoveryExecutionResult(
        bool Succeeded,
        int HotkeyAttempts,
        int HotkeyFailedCount);

    internal readonly record struct ResumeRecoveryExecutionDependencies(
        Func<Task> RecoverAudioAsync,
        Func<string, Task<(ResumeHotkeyRegistrationResult Result, int Attempts)>> RegisterHotkeysAsync,
        Func<Task> RefreshDevicesAsync);

    internal static class AppResumeRecoveryCoordinator
    {
        public static string ResolveOperationId(string? resumeOpId)
        {
            return string.IsNullOrWhiteSpace(resumeOpId)
                ? $"resume:{Guid.NewGuid():N}"
                : resumeOpId;
        }

        public static bool ShouldRetryHotkeyRegistration(ResumeHotkeyRegistrationResult result, int attempt)
        {
            return attempt == 1 && !result.AllSucceeded;
        }

        public static async Task<(ResumeHotkeyRegistrationResult Result, int Attempts)> RegisterHotkeysAsync(
            Func<Task<ResumeHotkeyRegistrationResult>> registerAttemptAsync,
            int retryDelayMs,
            ILogger logger,
            string resumeOpId,
            CancellationToken cancellationToken = default)
        {
            ResumeHotkeyRegistrationResult registrationResult = await registerAttemptAsync();
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
        /// A failed phase does not prevent the remaining phases from recovering. Shutdown cancellation stops the
        /// pipeline. Hotkey registration may retry once so transient resume races can settle before the summary.
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
                bool audioRecovered = await TryRunPhaseAsync("audio", dependencies.RecoverAudioAsync);
                bool hotkeysRecovered = await TryRunPhaseAsync("hotkeys", async () =>
                {
                    var (result, attempts) = await dependencies.RegisterHotkeysAsync(opId);
                    hotkeyAttempts = attempts;
                    hotkeyFailedCount = result.FailedCount;
                });
                bool devicesRefreshed = await TryRunPhaseAsync("devices", dependencies.RefreshDevicesAsync);
                recoverySucceeded = audioRecovered && hotkeysRecovered && devicesRefreshed && hotkeyFailedCount == 0;
                if (recoverySucceeded)
                {
                    logger.Info("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Success} | opId={opId}");
                }
                else if (hotkeyFailedCount > 0)
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

            async Task<bool> TryRunPhaseAsync(string phase, Func<Task> recoverAsync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await recoverAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.Warning("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ResumeRecovery.Failed} | opId={opId} phase={phase}", failureMethodName, ex);
                    return false;
                }
            }
        }
    }
}
