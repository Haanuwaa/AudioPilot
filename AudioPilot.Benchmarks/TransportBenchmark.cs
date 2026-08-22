using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioPilot.Cli;
using AudioPilot.Platform;

namespace AudioPilot.Benchmarks;

internal static class TransportBenchmark
{
    internal static void Run(string outputDirectory, int sampleCount, int warmups)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        using var session = new IpcBenchmarkSession(outputDirectory);
        using var client = new SingleInstanceHelper();
        string payload = new CliCommand { Action = CliAction.Status, JsonOutput = true }.ToPipePayload();
        double[] durations = new double[sampleCount];
        for (int index = -warmups; index < sampleCount; index++)
        {
            long start = Stopwatch.GetTimestamp();
            bool acquired = client.TryAcquire(payload);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (acquired || !client.LastSignalExistingSucceeded || client.LastSignalExitCode != 0 ||
                client.LastSignalOutput != IpcBenchmarkSession.ExpectedOutput)
                throw new InvalidOperationException($"Unexpected warmed IPC response: {client.LastSignalFailureKind}.");
            if (index >= 0) durations[index] = elapsed;
        }
        double[] sorted = [.. durations.Order()];
        var report = new
        {
            SchemaVersion = 1,
            Scenario = "warmed-ipc-isolated-same-process-peer",
            Notes = "Production client acquisition, framing, command dispatch, and reply parsing. No CLI launch, real status collection, or cross-process scheduling.",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            Assemblies = new[] { typeof(TransportBenchmark).Assembly, typeof(SingleInstanceHelper).Assembly }.ToDictionary(
                assembly => Path.GetFileName(assembly.Location), assembly =>
                {
                    using var file = File.OpenRead(assembly.Location);
                    return Convert.ToHexString(SHA256.HashData(file));
                }),
            WarmupIterations = warmups,
            Iterations = sampleCount,
            MedianMs = IpcBenchmarkSession.Percentile(sorted, 0.5),
            P95Ms = IpcBenchmarkSession.Percentile(sorted, 0.95),
            SamplesMs = durations,
        };
        File.WriteAllText(Path.Combine(outputDirectory, "transport.json"),
            System.Text.Json.JsonSerializer.Serialize(report, IpcBenchmarkSession.ReportJson));
        Console.WriteLine($"Warmed IPC, {sampleCount} samples: median {report.MedianMs:F3} ms, p95 {report.P95Ms:F3} ms");
    }
}
