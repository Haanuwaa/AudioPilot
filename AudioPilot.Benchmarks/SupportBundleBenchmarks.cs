using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Logging;
using BenchmarkDotNet.Attributes;

namespace AudioPilot.Benchmarks;

[MemoryDiagnoser]
public class SupportBundleBenchmarks
{
    [Params(1, 10, 50)]
    public int InputMiB { get; set; }

    private const string PrivateMarker = "BenchmarkSensitive_漢字";
    private string _root = null!;
    private string _logs = null!;
    private string _destination = null!;
    private long _inputBytes;
    private static readonly DiagnosticBundlePayloads Payloads = new("{}", "[]", "{}", "{}");

    [GlobalSetup]
    public void SetUp()
    {
        _inputBytes = 0;
        _root = Path.Combine(Path.GetTempPath(), $"AudioPilot-export-benchmark-{Guid.NewGuid():N}");
        _logs = Path.Combine(_root, "synthetic-logs");
        _destination = Path.Combine(_root, "bundle.zip");
        string backups = Path.Combine(_logs, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backups);
        foreach (string path in new[] { Path.Combine(_logs, AppConstants.Files.LogFileName), Path.Combine(backups, AppConstants.Files.LogFileName + ".bak1") })
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            long bytes = 0;
            for (int index = 0; bytes < InputMiB * 1024L * 1024 / 2; index++)
            {
                string entry = $"[2026-11-06 00:00:00.000] [Info] [Fixture] volume=50 device[{PrivateMarker}-{index}]\r\n"
                    + $"Exception: InvalidOperationException: {PrivateMarker}-message-{index}\r\n  {PrivateMarker}-continuation-{index}\r\n";
                writer.Write(entry);
                bytes += Encoding.UTF8.GetByteCount(entry);
            }
            _inputBytes += bytes;
        }
        ExportRedactedBundle();
        ValidateExport();
    }

    [Benchmark]
    public long ExportRedactedBundle()
    {
        var result = DiagnosticBundleExportService.ExportBundle(_logs, _destination, Payloads, includeSensitive: false);
        if (result.PartialExport) throw new InvalidOperationException("Synthetic bundle export was incomplete.");
        return new FileInfo(_destination).Length;
    }

    private void ValidateExport()
    {
        using var archive = ZipFile.OpenRead(_destination);
        int logs = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal)))
        {
            logs++;
            using var reader = new StreamReader(entry.Open());
            bool hasLevel = false;
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains(PrivateMarker, StringComparison.Ordinal)) throw new InvalidOperationException("Synthetic private content escaped redaction.");
                hasLevel |= line.Contains("volume=50", StringComparison.Ordinal);
            }
            if (!hasLevel) throw new InvalidOperationException("Useful log context was lost.");
        }
        if (logs != 2 || archive.GetEntry("manifest.json") == null) throw new InvalidOperationException("Synthetic bundle has the wrong inventory.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("AUDIOPILOT_BENCHMARK_REPORT_DIR") is { Length: > 0 } output)
            {
                using var process = Process.GetCurrentProcess();
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, $"export-{InputMiB}MiB-{process.Id}-resources.json"), JsonSerializer.Serialize(new
                {
                    InputBytes = _inputBytes,
                    process.PeakWorkingSet64,
                    Scope = "Benchmark child process peak, including fixture setup and validation; OS file cache is not flushed."
                }, IpcBenchmarkSession.ReportJson));
            }
        }
        finally { if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    internal static void ValidateCases()
    {
        foreach (int size in new[] { 1, 10, 50 })
        {
            var benchmark = new SupportBundleBenchmarks { InputMiB = size };
            try { benchmark.SetUp(); benchmark.ExportRedactedBundle(); benchmark.ValidateExport(); }
            finally { benchmark.Cleanup(); }
        }
    }
}
