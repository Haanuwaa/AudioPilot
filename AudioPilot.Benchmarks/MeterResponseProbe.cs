using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.ViewModels;

namespace AudioPilot.Benchmarks;

internal static class MeterResponseProbe
{
    internal static int Run(string outputDirectory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                object[] cases = [Measure(50, 50), Measure(16, 16), Measure(16, 33)];
                File.WriteAllText(Path.Combine(outputDirectory, "meter-response.json"), JsonSerializer.Serialize(new
                {
                    CapturedUtc = DateTimeOffset.UtcNow,
                    Cases = cases,
                    Notes = "Synthetic steps every 137 ms through the production sampler, a WPF dispatcher timer and QuickMixerRow. Source-change-to-projection latency; excludes device periods, GPU rendering and desktop input. Each case runs four seconds. CPU includes both sampling and UI projection."
                }, IpcBenchmarkSession.ReportJson));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("Meter response probe failed.", failure);
        return 0;
    }

    private static object Measure(int sampleMs, int displayMs)
    {
        using var fixture = new QuickMixerUiProbe.Fixture(AudioMixerMode.Output, 1);
        using var row = new QuickMixerRow(fixture.Mixer, fixture.Mixer.Sessions.Single(item => item.IsApplication));
        long epoch = Stopwatch.GetTimestamp();
        using var sampler = new AudioMeterSampler(() => new Steps(epoch), interval: TimeSpan.FromMilliseconds(sampleMs));
        using var visible = sampler.AcquireVisibility(AudioMixerMode.Output);
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var latencies = new List<double>();
        int lastStep = -1, projections = 0;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(displayMs) };
        timer.Tick += (_, _) =>
        {
            if (sampler.TryGetLatest(AudioMixerMode.Output, out var snapshot))
            {
                int step = (int)Math.Round(snapshot!.Batch.Readings[0].Peak!.Value * 100) - 1;
                if (step != lastStep)
                {
                    if (step > 0) latencies.Add(Stopwatch.GetElapsedTime(epoch).TotalMilliseconds - step * 137);
                    lastStep = step;
                }
                row.UpdateMeter(snapshot, AudioMixerMode.Output);
                projections++;
            }
            if (Stopwatch.GetElapsedTime(epoch).TotalSeconds >= 4) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timer.Stop();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            sampler.StopAsync(stop.Token).GetAwaiter().GetResult();
        }
        latencies.Sort();
        if (latencies.Count < 20) throw new InvalidOperationException("Insufficient meter response observations.");
        double median = latencies[latencies.Count / 2], p95 = latencies[(int)Math.Ceiling((latencies.Count - 1) * .95)];
        Console.WriteLine($"Sample/display {sampleMs}/{displayMs} ms: response median {median:F1} ms, p95 {p95:F1} ms ({latencies.Count} steps)");
        return new
        {
            SamplingIntervalMs = sampleMs,
            DisplayIntervalMs = displayMs,
            Steps = latencies.Count,
            Projections = projections,
            MedianResponseMs = median,
            P95ResponseMs = p95,
            CpuPercentOfOneCore = (process.TotalProcessorTime - cpuBefore).TotalSeconds / Stopwatch.GetElapsedTime(epoch).TotalSeconds * 100
        };
    }

    private sealed class Steps(long epoch) : IAudioMeterBackend
    {
        private readonly AudioMeterSource _source = new(AudioMixerMode.Output, "synthetic-output", "session-Output-1", 1);
        public AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken)
        {
            int step = (int)(Stopwatch.GetElapsedTime(epoch).TotalMilliseconds / 137);
            return new([AudioMeterReading.FromPeak(_source, (step + 1) / 100f)], modes);
        }
        public void Dispose() { }
    }
}
