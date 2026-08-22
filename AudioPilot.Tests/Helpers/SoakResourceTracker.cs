using System.Diagnostics;

namespace AudioPilot.Tests.Helpers;

/// <summary>Samples post-warm-up resources and compares early/late medians to reduce allocator and GC noise.</summary>
internal sealed class SoakResourceTracker
{
    private readonly List<(TimeSpan At, long Managed, long Private, int Handles, int Sessions)> _samples = [];
    internal TimeSpan NextSampleAt { get; private set; }

    internal static TimeSpan ResolveDuration()
    {
        string? value = Environment.GetEnvironmentVariable("AUDIOPILOT_HARDWARE_SOAK_MINUTES");
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromMinutes(30);
        if (!int.TryParse(value, out int minutes) || minutes is < 1 or > 120)
            throw new InvalidOperationException("AUDIOPILOT_HARDWARE_SOAK_MINUTES must be an integer from 1 through 120.");
        return TimeSpan.FromMinutes(minutes);
    }

    internal void Sample(TimeSpan elapsed, int sessions = 0)
    {
        long managed = GC.GetTotalMemory(true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        _samples.Add((elapsed, managed, process.PrivateMemorySize64, process.HandleCount, sessions));
        NextSampleAt = elapsed + ResolveDuration() / 8;
        TestContext.Current.TestOutputHelper!.WriteLine($"soak-resource | seconds={elapsed.TotalSeconds:F1} managed={managed} private={process.PrivateMemorySize64} handles={process.HandleCount} sessions={sessions}");
    }

    internal void AssertBounded(string context)
    {
        Assert.True(_samples.Count >= 4, $"{context}: insufficient resource samples ({_samples.Count}).");
        int window = Math.Max(1, _samples.Count / 3);
        static double Median(IEnumerable<double> values)
        {
            double[] sorted = [.. values.Order()];
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
        var first = _samples.Take(window).ToArray();
        var last = _samples.TakeLast(window).ToArray();
        double managed = Median(last.Select(x => (double)x.Managed)) - Median(first.Select(x => (double)x.Managed));
        double native = Median(last.Select(x => (double)x.Private)) - Median(first.Select(x => (double)x.Private));
        double handles = Median(last.Select(x => (double)x.Handles)) - Median(first.Select(x => (double)x.Handles));
        double sessions = Median(last.Select(x => (double)x.Sessions)) - Median(first.Select(x => (double)x.Sessions));
        double minutes = Math.Max(0.001, Median(last.Select(x => x.At.TotalMinutes)) - Median(first.Select(x => x.At.TotalMinutes)));
        TestContext.Current.TestOutputHelper!.WriteLine($"soak-trend | {context} samples={_samples.Count} managedGrowth={managed:F0} privateGrowth={native:F0} handleGrowth={handles:F1} sessionGrowth={sessions:F1} privateBytesPerMinute={native / minutes:F0} handlesPerMinute={handles / minutes:F2}");
        Assert.True(managed < 64L * 1024 * 1024, $"{context}: sustained managed growth {managed} bytes.");
        Assert.True(native < 256L * 1024 * 1024, $"{context}: sustained private memory growth {native} bytes.");
        Assert.True(handles < 256, $"{context}: sustained handle growth {handles}.");
        Assert.True(sessions < 16, $"{context}: retained session registrations grew by {sessions}.");
    }
}
