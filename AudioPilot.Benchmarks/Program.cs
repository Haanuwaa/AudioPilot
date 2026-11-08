using AudioPilot.Benchmarks;
using BenchmarkDotNet.Running;

if (args is ["startup", var cliPath, var outputDirectory, var iterations, var warmupIterations] &&
    int.TryParse(iterations, out int sampleCount) && sampleCount is >= 3 and <= 200 &&
    int.TryParse(warmupIterations, out int warmups) && warmups is >= 0 and <= 20)
{
    StartupBenchmark.Run(cliPath, outputDirectory, sampleCount, warmups);
    return 0;
}

if (args is ["codec", .. var codecOptions])
{
    return RunBenchmarks([typeof(CodecBenchmarks)], codecOptions);
}

if (args is ["settings", .. var settingsOptions])
{
    return RunBenchmarks([typeof(SettingsComparisonBenchmarks)], settingsOptions);
}

if (args is ["comparisons", .. var comparisonOptions])
{
    return RunBenchmarks([typeof(CodecBenchmarks), typeof(SettingsComparisonBenchmarks)], comparisonOptions);
}

if (args is ["mixer", .. var mixerOptions])
{
    return RunBenchmarks([typeof(MixerBenchmarks)], mixerOptions);
}

if (args is ["export", .. var exportOptions])
{
    return RunBenchmarks([typeof(SupportBundleBenchmarks)], exportOptions);
}

if (args is ["app", .. var appOptions])
{
    return RunBenchmarks([typeof(MixerBenchmarks), typeof(SupportBundleBenchmarks)], appOptions);
}

if (args is ["verify-app"])
{
    Exception? failure = null;
    var thread = new Thread(() => { try { MixerBenchmarks.ValidateCases(); } catch (Exception ex) { failure = ex; } });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Mixer fixture verification failed.", failure);
    SupportBundleBenchmarks.ValidateCases();
    Console.WriteLine("Synthetic mixer and redacted support-bundle fixtures passed.");
    return 0;
}

if (args is ["transport", var output, var transportIterations, var transportWarmups] &&
    int.TryParse(transportIterations, out int transportSamples) && transportSamples is >= 20 and <= 100_000 &&
    int.TryParse(transportWarmups, out int warmupCount) && warmupCount is >= 0 and <= 1_000)
{
    TransportBenchmark.Run(output, transportSamples, warmupCount);
    return 0;
}

if (args is ["verify", var reportDirectory])
{
    CodecBenchmarks.WriteCaseManifest(reportDirectory);
    SettingsComparisonBenchmarks.ValidateCases();
    Console.WriteLine("Codec and settings benchmark fixtures passed.");
    return 0;
}

Console.Error.WriteLine("Usage: codec|settings|comparisons|mixer|export|app [BenchmarkDotNet options] | startup <cli-executable> <output-directory> <samples> <warmups> | transport <output-directory> <samples> <warmups> | verify <output-directory> | verify-app");
return 2;

static int RunBenchmarks(Type[] types, string[] options)
{
    using var buildLock = AcquireBuildLock();
    var summaries = BenchmarkSwitcher.FromTypes(types).Run(options).ToArray();
    return summaries.Length > 0 && summaries.All(summary => !summary.HasCriticalValidationErrors &&
        summary.Reports.Length > 0 && summary.Reports.All(report => report.Success)) ? 0 : 1;
}

static FileStream AcquireBuildLock()
{
    string path = Path.Combine(AppContext.BaseDirectory, ".benchmark-build.lock");
    var wait = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && wait.Elapsed < TimeSpan.FromMinutes(10))
        {
            Thread.Sleep(100);
        }
    }
}
