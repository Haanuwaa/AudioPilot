using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    internal readonly record struct SingleInstanceProcessInfo(
        int ProcessId,
        string ProcessName,
        string? ExecutablePath,
        bool HasMainWindow);

    internal readonly record struct SingleInstanceProcessIdentity(
        bool Exists,
        string? ExecutablePath);

    internal readonly record struct SingleInstanceProcessRecoveryResult(
        bool Success,
        int MatchedProcessCount,
        string? FailureReason = null);

    internal sealed class SingleInstanceProcessRecoveryHelper(
        Logger logger,
        Func<IReadOnlyList<SingleInstanceProcessInfo>>? enumerateProcesses = null,
        Func<int>? getCurrentProcessId = null,
        Func<string?>? getCurrentExecutablePath = null,
        Func<int, SingleInstanceProcessIdentity>? getProcessIdentity = null,
        Func<int, bool>? tryCloseMainWindow = null,
        Func<int, TimeSpan, bool>? waitForExit = null,
        Action<int>? killProcess = null)
    {
        private readonly Logger _logger = logger;
        private readonly Func<IReadOnlyList<SingleInstanceProcessInfo>> _enumerateProcesses = enumerateProcesses ?? EnumerateProcesses;
        private readonly Func<int> _getCurrentProcessId = getCurrentProcessId ?? (() => Environment.ProcessId);
        private readonly Func<string?> _getCurrentExecutablePath = getCurrentExecutablePath ?? ResolveCurrentExecutablePath;
        private readonly Func<int, SingleInstanceProcessIdentity> _getProcessIdentity = getProcessIdentity ?? GetProcessIdentity;
        private readonly Func<int, bool> _tryCloseMainWindow = tryCloseMainWindow ?? TryCloseMainWindow;
        private readonly Func<int, TimeSpan, bool> _waitForExit = waitForExit ?? WaitForExit;
        private readonly Action<int> _killProcess = killProcess ?? KillProcess;

        internal SingleInstanceProcessRecoveryResult TryTerminateMatchingExistingProcess()
        {
            string currentExecutablePath;
            try
            {
                currentExecutablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(_getCurrentExecutablePath());
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceProcessRecoveryHelper", "Failed to resolve the current executable", nameof(TryTerminateMatchingExistingProcess), ex);
                return new SingleInstanceProcessRecoveryResult(false, 0, "current-executable-unavailable");
            }

            if (string.IsNullOrWhiteSpace(currentExecutablePath))
            {
                return new SingleInstanceProcessRecoveryResult(false, 0, "current-executable-unavailable");
            }

            int currentProcessId;
            List<SingleInstanceProcessInfo> matches;
            try
            {
                currentProcessId = _getCurrentProcessId();
                matches = [.. _enumerateProcesses()
                    .Where(process => process.ProcessId != currentProcessId)
                    .Where(process => !string.IsNullOrWhiteSpace(process.ExecutablePath))
                    .Where(process => string.Equals(
                        RoutineTriggerPathHelper.NormalizeExecutablePath(process.ExecutablePath),
                        currentExecutablePath,
                        StringComparison.OrdinalIgnoreCase))];
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceProcessRecoveryHelper", "Failed to enumerate existing AudioPilot processes", nameof(TryTerminateMatchingExistingProcess), ex);
                return new SingleInstanceProcessRecoveryResult(false, 0, "process-enumeration-failed");
            }

            if (matches.Count == 0)
            {
                return new SingleInstanceProcessRecoveryResult(false, 0, "no-matching-process");
            }

            TimeSpan gracefulCloseTimeout = TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceRecoveryGracefulCloseTimeoutMs);
            TimeSpan killWaitTimeout = TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceRecoveryKillWaitTimeoutMs);

            foreach (SingleInstanceProcessInfo process in matches)
            {
                SingleInstanceProcessRecoveryResult? identityFailure = ValidateProcessIdentity(
                    process.ProcessId,
                    currentExecutablePath,
                    matches.Count,
                    out bool processExited);
                if (identityFailure.HasValue)
                {
                    return identityFailure.Value;
                }
                if (processExited)
                {
                    continue;
                }

                try
                {
                    if (process.HasMainWindow && _tryCloseMainWindow(process.ProcessId) && _waitForExit(process.ProcessId, gracefulCloseTimeout))
                    {
                        continue;
                    }

                    identityFailure = ValidateProcessIdentity(
                        process.ProcessId,
                        currentExecutablePath,
                        matches.Count,
                        out processExited);
                    if (identityFailure.HasValue)
                    {
                        return identityFailure.Value;
                    }
                    if (processExited)
                    {
                        continue;
                    }

                    _killProcess(process.ProcessId);

                    if (!_waitForExit(process.ProcessId, killWaitTimeout))
                    {
                        return new SingleInstanceProcessRecoveryResult(false, matches.Count, "terminate-timeout");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning("SingleInstanceProcessRecoveryHelper", "Failed to terminate matching AudioPilot process", nameof(TryTerminateMatchingExistingProcess), ex);
                    return new SingleInstanceProcessRecoveryResult(false, matches.Count, "terminate-failed");
                }
            }

            return new SingleInstanceProcessRecoveryResult(true, matches.Count);
        }

        private SingleInstanceProcessRecoveryResult? ValidateProcessIdentity(
            int processId,
            string expectedExecutablePath,
            int matchedProcessCount,
            out bool processExited)
        {
            processExited = false;

            SingleInstanceProcessIdentity identity;
            try
            {
                identity = _getProcessIdentity(processId);
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceProcessRecoveryHelper", "Failed to revalidate the existing process identity", nameof(TryTerminateMatchingExistingProcess), ex);
                return new SingleInstanceProcessRecoveryResult(false, matchedProcessCount, "process-identity-unavailable");
            }

            if (!identity.Exists)
            {
                processExited = true;
                return null;
            }

            string actualExecutablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(identity.ExecutablePath);
            if (string.IsNullOrWhiteSpace(actualExecutablePath))
            {
                return new SingleInstanceProcessRecoveryResult(false, matchedProcessCount, "process-identity-unavailable");
            }

            if (!string.Equals(actualExecutablePath, expectedExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "SingleInstanceProcessRecoveryHelper",
                    () => $"Existing-process identity changed before recovery | processId={processId}",
                    nameof(TryTerminateMatchingExistingProcess));
                return new SingleInstanceProcessRecoveryResult(false, matchedProcessCount, "process-identity-changed");
            }

            return null;
        }

        private static IReadOnlyList<SingleInstanceProcessInfo> EnumerateProcesses()
        {
            List<SingleInstanceProcessInfo> processes = [];
            ProcessEnumerationHelper.EnumerateProcesses(process =>
            {
                string? executablePath = null;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch
                {
                }

                processes.Add(new SingleInstanceProcessInfo(
                    process.Id,
                    process.ProcessName,
                    executablePath,
                    process.MainWindowHandle != IntPtr.Zero));
            });

            return processes;
        }

        private static string? ResolveCurrentExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Environment.ProcessPath;
            }

            try
            {
                using Process process = Process.GetCurrentProcess();
                return process.MainModule?.FileName;
            }
            catch
            {
                return Environment.ProcessPath;
            }
        }

        private static SingleInstanceProcessIdentity GetProcessIdentity(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return new SingleInstanceProcessIdentity(false, null);
                }

                return new SingleInstanceProcessIdentity(true, process.MainModule?.FileName);
            }
            catch (ArgumentException)
            {
                return new SingleInstanceProcessIdentity(false, null);
            }
            catch (InvalidOperationException)
            {
                return new SingleInstanceProcessIdentity(false, null);
            }
            catch
            {
                return new SingleInstanceProcessIdentity(true, null);
            }
        }

        private static bool TryCloseMainWindow(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.CloseMainWindow();
            }
            catch
            {
                return false;
            }
        }

        private static bool WaitForExit(int processId, TimeSpan timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void KillProcess(int processId)
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill();
        }
    }
}
