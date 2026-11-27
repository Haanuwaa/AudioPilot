namespace AudioPilot.Services.Routines;

/// <summary>Reuses process discovery only within one read-only request.</summary>
internal sealed class RoutineProcessSnapshotScope(IRoutineProcessSnapshotProvider source,
    RoutineProcessSnapshotCaptureOptions requiredOptions) : IRoutineProcessSnapshotProvider
{
    private readonly Dictionary<RoutineProcessSnapshotCaptureOptions, Lazy<List<RoutineProcessSnapshot>>> _snapshots = [];

    public RoutineProcessSnapshot? TryCapture(int processId, RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        => source.TryCapture(processId, options);

    public List<RoutineProcessSnapshot> CaptureAll(RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
    {
        options |= requiredOptions;
        if (!_snapshots.TryGetValue(options, out var snapshots))
            _snapshots[options] = snapshots = new(() => [.. source.CaptureAll(options)]);
        return snapshots.Value;
    }
}
