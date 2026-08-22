using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Platform;
using AudioPilot.ViewModels;
using BenchmarkDotNet.Attributes;

namespace AudioPilot.Benchmarks;

[MemoryDiagnoser]
public class MixerBenchmarks
{
    [Params(1, 10, 100, 500)]
    public int SessionCount { get; set; }

    private Logger _logger = null!;
    private DeviceCacheHelper _cache = null!;
    private MixerViewModel _mixer = null!;
    private AudioSessionSnapshot[] _snapshots = [];
    private AudioSessionSnapshot[] _replacementSnapshots = [];
    private List<(string Id, float Volume, bool IsMuted)> _changed = [];
    private List<(string Id, float Volume, bool IsMuted)> _original = [];
    private bool _alternate;

    [GlobalSetup]
    public void SetUp()
    {
        _logger = Logger.CreateInMemoryForTests();
        _logger.MinimumLevel = LogLevel.None;
        // Only projection and grouping are measured. No audio service or native writer is supplied.
        _cache = new DeviceCacheHelper(null!, new DeviceCacheHelperRuntime
        {
            CaptureSnapshotsAsync = () => throw new InvalidOperationException("Unexpected device discovery."),
            MaterializeDeviceById = _ => throw new InvalidOperationException("Unexpected native device access."),
            GetFallbackPlaybackDevice = (_, _) => throw new InvalidOperationException("Unexpected playback access."),
            GetFallbackRecordingDevice = (_, _) => throw new InvalidOperationException("Unexpected recording access."),
            ProbeMuteState = (_, _, _) => throw new InvalidOperationException("Unexpected mute access."),
            RunRefreshWorkAsync = work => work()
        }, _logger);
        _mixer = new MixerViewModel(null!, Dispatcher.CurrentDispatcher, AudioMixerMode.Output, _logger, _cache);
        _snapshots = [.. Enumerable.Range(1, SessionCount).Select(index =>
            new AudioSessionSnapshot($"Application {index:D4}", InitialVolume(index), "Synthetic output", $"app-{index}", null,
                (uint)index, SessionInstanceId: $"session-{index}", EndpointId: "synthetic-output"))];
        _replacementSnapshots = [.. _snapshots];
        _replacementSnapshots[^1] = _snapshots[^1] with { ProcessId = 1_000_000, DisplayName = "Replacement application", SessionInstanceId = "replacement" };
        var rows = _snapshots.Select(snapshot => (Id(snapshot.ProcessId!.Value), CreateItem(snapshot))).ToList();
        Apply(null, rows, null, _snapshots);
        foreach (var row in _mixer.Sessions)
        {
            _mixer.DetachOwnedVolumeChangedHandler(Id(row.ProcessId!.Value), row);
            _mixer.ToggleApplicationSelection(row);
        }
        _changed = [.. _snapshots.Select(snapshot => (Id(snapshot.ProcessId!.Value), snapshot.Volume + 5, true))];
        _original = [.. _snapshots.Select(snapshot => (Id(snapshot.ProcessId!.Value), snapshot.Volume, false))];
        _alternate = false;
    }

    [Benchmark, STAThread]
    public int MaterializeMixerRows()
    {
        var sessions = _snapshots.Select(CreateItem).ToList();
        int count = sessions.Count;
        return count;
    }

    [Benchmark, STAThread]
    public int ProjectUnchanged() => Apply(null, null, null, _snapshots);

    [Benchmark, STAThread]
    public int ProjectChanged()
    {
        _alternate = !_alternate;
        return Apply(null, null, _alternate ? _changed : _original, _snapshots);
    }

    [Benchmark, STAThread]
    public int ProjectChurn()
    {
        _alternate = !_alternate;
        AudioSessionSnapshot incoming = (_alternate ? _replacementSnapshots : _snapshots)[^1];
        return Apply([Id(_alternate ? (uint)SessionCount : 1_000_000)],
            [(Id(incoming.ProcessId!.Value), CreateItem(incoming))], null, _alternate ? _replacementSnapshots : _snapshots);
    }

    [Benchmark, STAThread]
    public float ChangeSelectedGroup()
    {
        var anchor = _mixer.Sessions[0];
        _mixer.BeginGroupVolumeEdit(anchor);
        _alternate = !_alternate;
        anchor.Volume = InitialVolume(1) + (_alternate ? 5 : 0);
        _mixer.PropagateGroupVolume(anchor);
        _mixer.EndGroupVolumeEdit(anchor);
        return _mixer.Sessions[^1].Volume;
    }

    private int Apply(List<string>? remove, List<(string Id, AudioSessionItem Item)>? add,
        List<(string Id, float Volume, bool IsMuted)>? update, AudioSessionSnapshot[] snapshots)
    {
        Task<bool> pending = _mixer.ApplyRefreshResultsAsync(0, remove, add, update, CancellationToken.None, snapshots);
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        if (!pending.GetAwaiter().GetResult()) throw new InvalidOperationException("Mixer projection did not apply.");
        return _mixer.Sessions.Count;
    }

    private static string Id(uint processId) => $"pid:{processId}";
    private static float InitialVolume(int index) => 30 + index % 30;
    private static AudioSessionItem CreateItem(AudioSessionSnapshot snapshot) => new(snapshot.DisplayName, snapshot.Volume, false, false,
        processId: snapshot.ProcessId, sessionInstanceId: snapshot.SessionInstanceId, endpointId: snapshot.EndpointId);

    [GlobalCleanup]
    public void Cleanup()
    {
        try { _mixer?.Cleanup(); }
        finally { try { _cache?.Dispose(); } finally { _logger?.Dispose(); } }
    }

    internal static void ValidateCases()
    {
        foreach (int count in new[] { 1, 10, 100, 500 })
        {
            foreach (string scenario in new[] { "materialize", "unchanged", "changed", "churn", "group" })
            {
                var benchmark = new MixerBenchmarks { SessionCount = count };
                try
                {
                    benchmark.SetUp();
                    switch (scenario)
                    {
                        case "materialize": if (benchmark.MaterializeMixerRows() != count) throw new InvalidOperationException("Incorrect materialization."); break;
                        case "unchanged": benchmark.ProjectUnchanged(); break;
                        case "changed":
                            benchmark.ProjectChanged();
                            if (benchmark._mixer.Sessions.Any(row => !row.IsMuted || row.Volume != InitialVolume((int)row.ProcessId!.Value) + 5))
                                throw new InvalidOperationException("Incorrect changed-state projection.");
                            benchmark.ProjectChanged();
                            break;
                        case "churn":
                            benchmark.ProjectChurn();
                            if (!benchmark._mixer.Sessions.Any(row => row.ProcessId == 1_000_000)) throw new InvalidOperationException("Missing replacement row.");
                            benchmark.ProjectChurn();
                            break;
                        case "group":
                            benchmark.ChangeSelectedGroup();
                            if (benchmark._mixer.Sessions.Any(row => row.Volume != InitialVolume((int)row.ProcessId!.Value) + 5))
                                throw new InvalidOperationException("Grouped volume differences were not preserved.");
                            benchmark.ChangeSelectedGroup();
                            break;
                    }
                    if (benchmark._mixer.Sessions.Count != count) throw new InvalidOperationException("Unexpected mixer row count.");
                }
                finally { benchmark.Cleanup(); }
            }
        }
    }
}
