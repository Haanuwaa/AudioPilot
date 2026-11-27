using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AudioPilot.Benchmarks;

internal static class StartupBenchmark
{
    internal static void Run(string cliPath, string outputDirectory, int sampleCount, int warmups)
    {
        cliPath = Path.GetFullPath(cliPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        object fingerprint = PayloadFingerprint.Read(cliPath);
        using var session = new IpcBenchmarkSession(outputDirectory);
        var durations = new double[sampleCount];
        for (int index = -warmups; index < sampleCount; index++)
        {
            double elapsed = MeasureAsync(cliPath).GetAwaiter().GetResult();
            if (index >= 0) durations[index] = elapsed;
        }
        double[] sorted = [.. durations.Order()];
        var result = new
        {
            SchemaVersion = 1,
            Scenario = "cli-status-json-ipc-fresh-process",
            Notes = "Fresh CLI launch against a warmed synthetic peer. Includes process startup and shutdown; excludes real status collection and does not flush OS file caches.",
            CliPayload = fingerprint,
            PeerArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PeerRuntime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            WarmupIterations = warmups,
            Iterations = sampleCount,
            MedianMs = IpcBenchmarkSession.Percentile(sorted, 0.5),
            P95Ms = IpcBenchmarkSession.Percentile(sorted, 0.95),
            SamplesMs = durations,
        };
        File.WriteAllText(Path.Combine(outputDirectory, "startup.json"), JsonSerializer.Serialize(result, IpcBenchmarkSession.ReportJson));
        Console.WriteLine($"CLI startup and IPC, {sampleCount} samples: median {result.MedianMs:F2} ms, p95 {result.P95Ms:F2} ms");
    }

    private static async Task<double> MeasureAsync(string cliPath)
    {
        var info = new ProcessStartInfo(cliPath)
        {
            WorkingDirectory = Path.GetDirectoryName(cliPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("status");
        info.ArgumentList.Add("--json");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long started = Stopwatch.GetTimestamp();
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start AudioPilot CLI.");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (process.ExitCode != 0 || stdout.Result.Trim() != IpcBenchmarkSession.ExpectedOutput)
                throw new InvalidOperationException($"Unexpected benchmark CLI response (exit {process.ExitCode}): {stderr.Result}");
            return elapsed;
        }
        catch (Exception measurementError)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    if (!process.WaitForExit(5000)) throw new TimeoutException("The benchmark CLI could not be stopped.");
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("CLI measurement and process cleanup both failed.", measurementError, cleanupError);
            }
            throw;
        }
    }

}
