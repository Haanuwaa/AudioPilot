namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeRoutineProcessSnapshotProvider : IRoutineProcessSnapshotProvider
{
    private readonly Dictionary<int, RoutineProcessSnapshot> _snapshotsByProcessId = [];

    public int CaptureAllCallCount { get; private set; }
    public int TryCaptureCallCount { get; private set; }
    public List<RoutineProcessSnapshot> CaptureAllSnapshots { get; } = [];
    public List<RoutineProcessSnapshotCaptureOptions> CaptureAllOptionsHistory { get; } = [];
    public List<RoutineProcessSnapshotCaptureOptions> TryCaptureOptionsHistory { get; } = [];

    public RoutineProcessSnapshot? TryCapture(int processId, RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
    {
        TryCaptureCallCount++;
        TryCaptureOptionsHistory.Add(options);
        return _snapshotsByProcessId.TryGetValue(processId, out RoutineProcessSnapshot snapshot)
            ? snapshot
            : null;
    }

    public List<RoutineProcessSnapshot> CaptureAll(RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
    {
        CaptureAllCallCount++;
        CaptureAllOptionsHistory.Add(options);
        return [.. CaptureAllSnapshots];
    }

    public void SetTryCaptureResult(RoutineProcessSnapshot snapshot)
    {
        _snapshotsByProcessId[snapshot.ProcessId] = snapshot;
    }
}
