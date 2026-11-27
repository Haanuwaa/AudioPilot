using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioPilot.Models;
using AudioPilot.Platform;
using AudioPilot.Services.Audio.Metering;
using NAudio.CoreAudioApi;

namespace AudioPilot.Benchmarks;

// Explicitly opt-in hardware probe. No playback, capture stream, setter, routing,
// default-device change, microphone ownership, or user configuration is involved.
internal static class MeteringProbe
{
    internal static int Run(string outputDirectory, int seconds)
    {
        Directory.CreateDirectory(outputDirectory);
        var (Complete, Values) = ComThreadingHelper.RunOnCoreAudioThread(ReadControls);
        var native = new List<object>();
        foreach (int frequency in new[] { 10, 20, 30, 60 })
            native.Add(Measure(() => new CoreAudioMeterBackend(), frequency, seconds));
        // A read-only comparison for the additional control getters. Compare costs
        // only when source inventories match the normal 60 Hz case.
        object peakOnly = Measure(() => new CoreAudioMeterBackend(includeOutputSessionControls: false), 60, seconds);

        var synthetic = new List<object>();
        foreach (int count in new[] { 8, 64, 256 })
            synthetic.Add(Measure(() => new SyntheticBackend(count), 20, seconds));

        using var sampler = new AudioMeterSampler();
        // Warm native/runtime/JIT caches before the repeated-open resource comparison.
        OpenAndClose(sampler);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        int handlesBefore = process.HandleCount;
        long privateBefore = process.PrivateMemorySize64;
        var reopen = Stopwatch.StartNew();
        for (int index = 0; index < 20; index++) OpenAndClose(sampler);
        reopen.Stop();
        process.Refresh();
        int handlesAfter = process.HandleCount;
        long privateAfter = process.PrivateMemorySize64;
        Stop(sampler);
        var after = ComThreadingHelper.RunOnCoreAudioThread(ReadControls);
        bool controlsUnchanged = Complete && after.Complete && Values.SequenceEqual(after.Values);
        var report = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            SecondsPerCase = seconds,
            Native = native,
            PeakOnly60Hz = peakOnly,
            Synthetic = synthetic,
            Reopen = new { Cycles = 20, ElapsedMs = reopen.Elapsed.TotalMilliseconds, HandlesBefore = handlesBefore, HandlesAfter = handlesAfter, PrivateBytesBefore = privateBefore, PrivateBytesAfter = privateAfter },
            Controls = new { BeforeComplete = Complete, AfterComplete = after.Complete, ObservedUnchanged = controlsUnchanged },
            Notes = "Read-only peak API; source maxima are raw peaks, while Output UI also applies session volume/mute. Endpoint peaks precede endpoint attenuation. Software meters may report zero in exclusive mode. Per-app Input support requires an existing capture session. Source identifiers are hashed; no names, PIDs, PCM or paths are exported. CPU includes probe polling; read allocations are measured on the metering worker."
        };
        string path = Path.Combine(outputDirectory, "metering.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, IpcBenchmarkSession.ReportJson));
        Console.WriteLine($"Read-only metering report: {Path.GetFullPath(path)}");
        Console.WriteLine($"Endpoint volumes, mute and Windows defaults observed unchanged: {controlsUnchanged}");
        return controlsUnchanged ? 0 : 1;
    }

    private static object Measure(Func<IAudioMeterBackend> factory, int frequency, int seconds)
    {
        using var sampler = new AudioMeterSampler(factory, interval: TimeSpan.FromSeconds(1d / frequency));
        using var output = sampler.AcquireVisibility(AudioMixerMode.Output);
        using var input = sampler.AcquireVisibility(AudioMixerMode.Input);
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var elapsed = Stopwatch.StartNew();
        var durations = new List<double>();
        var allocations = new List<long>();
        var steadyAllocations = new List<long>();
        var sources = new Dictionary<AudioMeterSource, SourceStats>();
        long lastTimestamp = -1;
        int failed = 0, incomplete = 0, truncated = 0;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (sampler.Completion.IsCompleted) sampler.Completion.GetAwaiter().GetResult();
            if (sampler.TryGetLatest(AudioMixerMode.Output, out AudioMeterSnapshot? snapshot) && snapshot!.SampleStarted != lastTimestamp)
            {
                lastTimestamp = snapshot.SampleStarted;
                durations.Add(snapshot.ReadDuration.TotalMilliseconds);
                allocations.Add(snapshot.AllocatedBytes);
                if (!snapshot.IncludesDiscovery) steadyAllocations.Add(snapshot.AllocatedBytes);
                if (snapshot.ErrorCode.HasValue) failed++;
                if (snapshot.Batch.CompleteModes != AudioMeterModes.Both) incomplete++;
                if (snapshot.Batch.Truncated) truncated++;
                foreach (AudioMeterReading reading in snapshot.Batch.Readings)
                {
                    if (!sources.TryGetValue(reading.Source, out SourceStats? stats)) sources.Add(reading.Source, stats = new());
                    if (reading.Status == AudioMeterStatus.Available) { stats.Available++; stats.Maximum = Math.Max(stats.Maximum, reading.Peak!.Value); }
                    else if (reading.Status == AudioMeterStatus.Unsupported) stats.Unsupported++;
                    else stats.Unavailable++;
                }
            }
            Thread.Sleep(2);
        }
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        Stop(sampler);
        if (durations.Count == 0) throw new InvalidOperationException("The metering worker produced no current snapshots.");
        double cold = durations[0];
        double[] steady = [.. durations.Skip(1).Order()];
        if (steady.Length == 0) throw new InvalidOperationException("The metering probe produced no steady-state samples.");
        Console.WriteLine($"{frequency} Hz: {durations.Count} frames; median {Percentile(steady, .5):F3} ms, p95 {Percentile(steady, .95):F3} ms; {sources.Count} sources");
        return new
        {
            Frequency = frequency,
            Samples = durations.Count,
            FirstReadMs = cold,
            MedianReadMs = Percentile(steady, .5),
            P95ReadMs = Percentile(steady, .95),
            MaximumReadMs = durations.Max(),
            MeanWorkerReadAllocatedBytes = allocations.Average(),
            MedianSteadyWorkerReadAllocatedBytes = steadyAllocations.Count == 0 ? (double?)null : steadyAllocations.Order().ElementAt(steadyAllocations.Count / 2),
            CpuPercentOfOneCore = cpu.TotalSeconds / elapsed.Elapsed.TotalSeconds * 100,
            FailedFrames = failed,
            IncompleteFrames = incomplete,
            TruncatedFrames = truncated,
            Sources = sources.Select(pair => new
            {
                Mode = pair.Key.Mode.ToString(),
                Kind = pair.Key.IsEndpoint ? "Endpoint" : "Session",
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{pair.Key.Mode}\0{pair.Key.EndpointId}\0{pair.Key.SessionInstanceId}")))[..12],
                pair.Value.Available,
                pair.Value.Unsupported,
                pair.Value.Unavailable,
                pair.Value.Maximum
            }).ToArray()
        };
    }

    private static double Percentile(double[] values, double percentile) => values[(int)Math.Ceiling((values.Length - 1) * percentile)];

    private static void OpenAndClose(AudioMeterSampler sampler)
    {
        using (sampler.AcquireVisibility(AudioMixerMode.Output))
        using (sampler.AcquireVisibility(AudioMixerMode.Input))
        {
            WaitUntil(() => sampler.TryGetLatest(AudioMixerMode.Input, out _));
        }
        WaitUntil(() => sampler.IsIdle);
    }

    private static void WaitUntil(Func<bool> completed)
    {
        var wait = Stopwatch.StartNew();
        while (!completed())
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Metering probe did not settle.");
            Thread.Sleep(1);
        }
    }

    private static void Stop(AudioMeterSampler sampler)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        sampler.StopAsync(deadline.Token).GetAwaiter().GetResult();
    }

    private static (bool Complete, string[] Values) ReadControls()
    {
        using var enumerator = new MMDeviceEnumerator();
        var values = new List<string>();
        bool complete = true;
        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            for (int index = 0; index < devices.Count; index++)
            {
                try
                {
                    using var device = devices[index];
                    values.Add(FormattableString.Invariant($"{device.ID}:{device.AudioEndpointVolume.MasterVolumeLevelScalar:R}:{device.AudioEndpointVolume.Mute}"));
                }
                catch { complete = false; }
            }
            foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                if (!enumerator.HasDefaultAudioEndpoint(flow, role)) { values.Add($"{flow}:{role}:none"); continue; }
                using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                values.Add($"{flow}:{role}:{device.ID}");
            }
        }
        return (complete, values.Order(StringComparer.Ordinal).ToArray());
    }

    private sealed class SourceStats
    {
        internal int Available, Unavailable, Unsupported;
        internal float Maximum;
    }

    private sealed class SyntheticBackend(int count) : IAudioMeterBackend
    {
        private readonly ImmutableArray<AudioMeterReading> _readings = [.. Enumerable.Range(0, count).Select(index => AudioMeterReading.FromPeak(new(index % 2 == 0 ? AudioMixerMode.Output : AudioMixerMode.Input, $"synthetic-{index}", $"session-{index}", (uint)index + 1), .25f))];
        public AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken)
        {
            var readings = ImmutableArray.CreateBuilder<AudioMeterReading>(_readings.Length);
            foreach (var reading in _readings) { cancellationToken.ThrowIfCancellationRequested(); readings.Add(reading); }
            return new(readings.MoveToImmutable(), modes);
        }
        public void Dispose() { }
    }
}
