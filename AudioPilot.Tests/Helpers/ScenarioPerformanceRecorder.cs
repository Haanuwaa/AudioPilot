using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AudioPilot.Tests.Helpers;

/// <summary>Writes optional post-warm-up latency and resource trends without enforcing machine-dependent thresholds.</summary>
internal sealed class ScenarioPerformanceRecorder(string scenario, int sampleInterval = 20) : IDisposable
{
    private readonly string? _directory = Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR");
    private readonly List<double> _durations = [];
    private readonly List<object> _samples = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _allocated = GC.GetTotalAllocatedBytes();

    internal void Record(long started)
    {
        if (string.IsNullOrWhiteSpace(_directory)) return;
        _durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (_durations.Count <= 4 || _durations.Count % sampleInterval != 0) return;
        using var process = Process.GetCurrentProcess();
        _samples.Add(new
        {
            Cycle = _durations.Count,
            ElapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
            ManagedBytes = GC.GetTotalMemory(false),
            PrivateBytes = process.PrivateMemorySize64,
            Handles = process.HandleCount,
            AllocatedBytes = GC.GetTotalAllocatedBytes() - _allocated,
            Gen2Collections = GC.CollectionCount(2)
        });
    }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_directory) || _durations.Count == 0) return;
        double[] measured = [.. _durations.Skip(Math.Min(20, _durations.Count / 4)).Order()];
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, $"{scenario}.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Scenario = scenario,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Scope = "Controlled service/view-model work with synthetic inputs; no real audio-device writes.",
            MedianMs = measured[measured.Length / 2],
            P95Ms = measured[(int)((measured.Length - 1) * 0.95)],
            Samples = _samples,
        }));
    }
}
