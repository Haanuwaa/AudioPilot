using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    internal readonly record struct RoutineProcessSnapshot(
        int ProcessId,
        string ExecutablePath,
        int? ParentProcessId = null,
        string? AppUserModelId = null,
        long? StartTimeUtcTicks = null,
        string? ProcessName = null,
        bool IdentityUnavailable = false);

    [Flags]
    internal enum RoutineProcessSnapshotCaptureOptions
    {
        None = 0,
        IncludeParentProcessId = 1,
        IncludeAppUserModelId = 2,
        Full = IncludeParentProcessId | IncludeAppUserModelId,
    }

    internal interface IRoutineProcessSnapshotProvider
    {
        RoutineProcessSnapshot? TryCapture(int processId, RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full);
        List<RoutineProcessSnapshot> CaptureAll(RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full);
    }

    internal sealed class RoutineProcessSnapshotProvider(Logger? logger = null) : IRoutineProcessSnapshotProvider
    {
        private readonly Logger _logger = logger ?? Logger.Instance;

        public RoutineProcessSnapshot? TryCapture(int processId, RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        {
            try
            {
                return TryCreateSnapshot(processId, options, windowsByPid: null);
            }
            catch
            {
                return null;
            }
        }

        public List<RoutineProcessSnapshot> CaptureAll(RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        {
            Stopwatch? stopwatch = _logger.IsEnabled(LogLevel.Debug)
                ? Stopwatch.StartNew()
                : null;
            var snapshots = new List<RoutineProcessSnapshot>();
            Dictionary<uint, List<IntPtr>>? windowsByPid = options.HasFlag(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId)
                ? AudioDeviceHelper.BuildWindowsByPidMap()
                : null;
            using Process current = Process.GetCurrentProcess();
            int sessionId = current.SessionId;

            ProcessEnumerationHelper.EnumerateProcesses(process =>
            {
                try
                {
                    RoutineProcessSnapshot? snapshot = TryCreateSnapshot(process.Id, options, windowsByPid);
                    if (snapshot.HasValue)
                    {
                        snapshots.Add(snapshot.Value);
                        return;
                    }
                }
                catch { }
                try { if (process.SessionId != sessionId) return; } catch { }
                try { if (process.HasExited) return; } catch { }
                string? name = null;
                try { name = process.ProcessName; } catch { }
                snapshots.Add(new(process.Id, string.Empty, ProcessName: name, IdentityUnavailable: true));
            });

            if (stopwatch != null)
            {
                stopwatch.Stop();
                _logger.Debug(
                    "RoutineProcessSnapshotProvider",
                    () => $"process-snapshot-capture-completed | options={options} count={snapshots.Count} durationMs={stopwatch.Elapsed.TotalMilliseconds:F1}",
                    nameof(CaptureAll));
            }

            return snapshots;
        }

        private static RoutineProcessSnapshot? TryCreateSnapshot(
            int processId,
            RoutineProcessSnapshotCaptureOptions options,
            Dictionary<uint, List<IntPtr>>? windowsByPid)
        {
            bool includeParent = options.HasFlag(RoutineProcessSnapshotCaptureOptions.IncludeParentProcessId);
            bool includeAumid = options.HasFlag(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId);
            var (nativePath, startTimeUtcTicks, parentProcessId, appUserModelId) =
                ProcessEnumerationHelper.TryGetRoutineProcessIdentity(processId, includeParent, includeAumid);
            string? executablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(nativePath);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            if (includeAumid && string.IsNullOrWhiteSpace(appUserModelId))
            {
                appUserModelId = windowsByPid != null
                    ? (windowsByPid.TryGetValue((uint)processId, out List<IntPtr>? windows)
                        ? AudioDeviceHelper.GetProcessAppUserModelId(windows)
                        : null)
                    : AudioDeviceHelper.GetProcessAppUserModelId(processId);
            }

            return new RoutineProcessSnapshot(
                processId,
                executablePath,
                parentProcessId is > 0 ? parentProcessId : null,
                appUserModelId,
                startTimeUtcTicks);
        }
    }
}
