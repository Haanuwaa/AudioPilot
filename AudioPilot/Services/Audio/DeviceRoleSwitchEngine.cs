using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal static class DeviceRoleSwitchEngine
    {
        private static Task<T> RunOnIsolatedComThreadAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComThreadingHelper.ThrowIfComInitializationFailed(operationName);
                return operation();
            }, cancellationToken);
        }

        public static Task<bool> TrySwitchOutputRolesAsync(
            string targetDeviceId,
            IReadOnlyList<NRole> outputRoles,
            Action<string, NRole> applyConfiguredRoles,
            Func<NRole, string?> getDefaultPlaybackDevice,
            Logger logger,
            string opId,
            string contextMethod,
            CancellationToken cancellationToken = default)
        {
            return TrySwitchRolesAsync(targetDeviceId, null, outputRoles, applyConfiguredRoles, getDefaultPlaybackDevice,
                logger, opId, contextMethod, true, false, false, cancellationToken);
        }

        public static Task<bool> TrySwitchInputRolesAsync(
            string targetDeviceId,
            string targetName,
            IReadOnlyList<NRole> inputRoles,
            Action<string, NRole> applyConfiguredRoles,
            Func<NRole, string?> getDefaultRecordingDevice,
            Logger logger,
            string opId,
            string contextMethod,
            bool emitVerifyRetryWarning,
            bool traceComRetry,
            CancellationToken cancellationToken = default)
        {
            return TrySwitchRolesAsync(targetDeviceId, targetName, inputRoles, applyConfiguredRoles, getDefaultRecordingDevice,
                logger, opId, contextMethod, false, emitVerifyRetryWarning, traceComRetry, cancellationToken);
        }

        private static async Task<bool> TrySwitchRolesAsync(
            string targetDeviceId,
            string? targetName,
            IReadOnlyList<NRole> roles,
            Action<string, NRole> applyRole,
            Func<NRole, string?> getDefaultDeviceId,
            Logger logger,
            string opId,
            string contextMethod,
            bool isOutput,
            bool emitVerifyRetryWarning,
            bool traceComRetry,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
            ArgumentNullException.ThrowIfNull(roles);
            ArgumentNullException.ThrowIfNull(applyRole);
            ArgumentNullException.ThrowIfNull(getDefaultDeviceId);
            if (roles.Count == 0) return false;

            Dictionary<NRole, string?> originalAssignments = await RunOnIsolatedComThreadAsync(
                () => roles.Distinct().ToDictionary(role => role, getDefaultDeviceId),
                nameof(TrySwitchRolesAsync), cancellationToken);

            bool success = false;
            bool applyAttempted = false;
            var stopwatch = Stopwatch.StartNew();
            double applyMs = 0;
            double verifyMs = 0;
            double retryDelayMs = 0;
            int attemptsUsed = 0;
            string result = "verify-failed";

            try
            {
                for (int attempt = 1; attempt <= RuntimeTuningConfig.SwitchMaxRetries && !success; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attemptsUsed = attempt;
                    try
                    {
                        (bool Verified, double ApplyMs, double VerifyMs) =
                            await RunOnIsolatedComThreadAsync(() =>
                            {
                                var phaseStopwatch = Stopwatch.StartNew();
                                applyAttempted = true;
                                foreach (NRole role in roles) applyRole(targetDeviceId, role);
                                double measuredApplyMs = phaseStopwatch.Elapsed.TotalMilliseconds;
                                bool verified = true;
                                foreach (NRole role in roles)
                                {
                                    if (!string.Equals(getDefaultDeviceId(role), targetDeviceId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        verified = false;
                                    }
                                }

                                return (verified, measuredApplyMs, phaseStopwatch.Elapsed.TotalMilliseconds - measuredApplyMs);
                            }, nameof(TrySwitchRolesAsync), cancellationToken);

                        success = Verified;
                        applyMs += ApplyMs;
                        verifyMs += VerifyMs;
                        if (!success && attempt < RuntimeTuningConfig.SwitchMaxRetries)
                        {
                            if (isOutput && logger.IsEnabled(LogLevel.Debug))
                                logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.VerifyRetry} | opId={opId} attempt={attempt}");
                            else if (!isOutput && emitVerifyRetryWarning)
                                logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.Retry} | opId={opId} attempt={attempt}");

                            double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                            await Task.Delay(RuntimeTuningConfig.SwitchRetryDelayMs, cancellationToken);
                            retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                        }
                    }
                    catch (COMException ex) when (attempt < RuntimeTuningConfig.SwitchMaxRetries)
                    {
                        result = "com-retry";
                        if (isOutput || !traceComRetry)
                        {
                            string eventName = isOutput ? AppConstants.Audio.LogEvents.OutputSwitch.ComRetry : AppConstants.Audio.LogEvents.InputSwitch.ComRetry;
                            logger.Warning("AudioDeviceService", () => $"{eventName} | opId={opId} attempt={attempt}", contextMethod, ex);
                        }
                        else if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.Trace("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.InputSwitch.ComRetry} | opId={opId} attempt={attempt} target={LogPrivacy.Device(targetName)} targetId={LogPrivacy.Id(targetDeviceId)}");
                        }

                        double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                        await Task.Delay(RuntimeTuningConfig.SwitchRetryMaxDelayMs, cancellationToken);
                        retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                    }
                    catch (Exception ex) when (attempt < RuntimeTuningConfig.SwitchMaxRetries)
                    {
                        result = ex.GetType().Name;
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            string eventName = isOutput ? AppConstants.Audio.LogEvents.OutputSwitch.VerifyFailed : AppConstants.Audio.LogEvents.InputSwitch.VerifyFailed;
                            logger.Trace("AudioDeviceService", () => $"{eventName} | opId={opId} attempt={attempt} reason={ex.GetType().Name}");
                        }

                        double delayStartMs = stopwatch.Elapsed.TotalMilliseconds;
                        await Task.Delay(RuntimeTuningConfig.SwitchRetryDelayMs, cancellationToken);
                        retryDelayMs += stopwatch.Elapsed.TotalMilliseconds - delayStartMs;
                    }
                }

                if (success) result = "success";
                return success;
            }
            finally
            {
                if (!success && applyAttempted)
                    await TryRollbackAsync(originalAssignments, applyRole, logger, opId, contextMethod);

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    string eventName = isOutput ? AppConstants.Audio.LogEvents.OutputSwitch.EnginePhases : AppConstants.Audio.LogEvents.InputSwitch.EnginePhases;
                    logger.Debug("AudioDeviceService", () => $"{eventName} | opId={opId} attempts={attemptsUsed} applyMs={applyMs:F1} verifyMs={verifyMs:F1} retryDelayMs={retryDelayMs:F1} totalMs={stopwatch.Elapsed.TotalMilliseconds:F1} result={result}");
                }
            }
        }

        private static async Task TryRollbackAsync(
            IReadOnlyDictionary<NRole, string?> originalAssignments,
            Action<string, NRole> applyRole,
            Logger logger,
            string opId,
            string contextMethod)
        {
            try
            {
                await RunOnIsolatedComThreadAsync(() =>
                {
                    foreach ((NRole role, string? originalDeviceId) in originalAssignments)
                    {
                        if (!string.IsNullOrWhiteSpace(originalDeviceId)) applyRole(originalDeviceId, role);
                    }

                    return true;
                }, nameof(TryRollbackAsync), CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                logger.Warning("AudioDeviceService", () => $"Role switch rollback failed | opId={opId}", contextMethod, rollbackException);
            }
        }
    }
}
