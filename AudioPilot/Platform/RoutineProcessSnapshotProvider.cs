using System.Diagnostics;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    internal readonly record struct RoutineProcessSnapshot(
        int ProcessId,
        string ExecutablePath,
        int? ParentProcessId = null,
        string? AppUserModelId = null);

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
                using Process process = Process.GetProcessById(processId);
                return TryCreateSnapshot(process, options);
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

            ProcessEnumerationHelper.EnumerateProcesses(process =>
            {
                RoutineProcessSnapshot? snapshot = TryCreateSnapshot(process, options);
                if (snapshot.HasValue)
                {
                    snapshots.Add(snapshot.Value);
                }
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

        private static RoutineProcessSnapshot? TryCreateSnapshot(Process process, RoutineProcessSnapshotCaptureOptions options)
        {
            string? executablePath = RoutineTriggerPathHelper.NormalizeExecutablePath(process.MainModule?.FileName);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return null;
            }

            int? parentProcessId = options.HasFlag(RoutineProcessSnapshotCaptureOptions.IncludeParentProcessId)
                ? AudioDeviceHelper.GetParentPid(process.Id)
                : null;
            string? appUserModelId = options.HasFlag(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId)
                ? AudioDeviceHelper.GetProcessAppUserModelId(process.Id)
                : null;
            return new RoutineProcessSnapshot(
                process.Id,
                executablePath,
                parentProcessId is > 0 ? parentProcessId : null,
                appUserModelId);
        }
    }
}
