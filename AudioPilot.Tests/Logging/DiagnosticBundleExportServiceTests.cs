using System.IO.Compression;
using System.Text.Json.Nodes;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

[Collection("LogPrivacy")]
public sealed class DiagnosticBundleExportServiceTests
{
    [Fact]
    public void ExportBundle_RedactedMode_WritesDeterministicEntriesAndSanitizesLogs()
    {
        using var directory = new TestScopedDirectory(nameof(ExportBundle_RedactedMode_WritesDeterministicEntriesAndSanitizesLogs));
        string logPath = Path.Combine(directory.Root, AppConstants.Files.LogFileName);
        string backupDirectory = Path.Combine(directory.Root, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        File.WriteAllText(logPath, @"Opened C:\Users\ExampleUser\My Music\track.mp3 and \\nas\Media Library\mix.flac for routine 'Office WiFi'.");
        File.WriteAllText(Path.Combine(backupDirectory, AppConstants.Files.LogFileName + ".bak1"), "backup log");

        string zipPath = Path.Combine(directory.Root, "ExampleUser-private-project.zip");
        DiagnosticBundleExportResult result = DiagnosticBundleExportService.ExportBundle(directory.Root, zipPath, CreatePayloads(), includeSensitive: false);

        Assert.False(result.IncludeSensitive);
        Assert.False(result.PartialExport);
        Assert.True(File.Exists(zipPath));

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string[] names = [.. archive.Entries.Select(static entry => entry.FullName)];
        Assert.Equal(
            [
                "diagnostics/status.json",
                "diagnostics/history.json",
                "diagnostics/media-status.json",
                "diagnostics/config-validation.json",
                "README.txt",
                "logs/AudioPilot.log",
                "logs/backups/AudioPilot.log.bak1",
                "manifest.json",
            ],
            names);

        string logContent = ReadEntry(archive, "logs/AudioPilot.log");
        Assert.Contains("<path>", logContent, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\ExampleUser", logContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("My Music", logContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\nas\Media Library", logContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Office WiFi", logContent, StringComparison.Ordinal);

        JsonObject manifest = JsonNode.Parse(ReadEntry(archive, "manifest.json"))!.AsObject();
        Assert.Equal("AudioPilot", manifest["App"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(manifest["AppVersion"]?.GetValue<string>()));
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            manifest["Environment"]?["ProcessArchitecture"]?.GetValue<string>());
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            manifest["Environment"]?["Framework"]?.GetValue<string>());
        Assert.Equal("redacted", manifest["RedactionMode"]?.GetValue<string>());
        Assert.Equal("<redacted>.zip", manifest["ExportFileName"]?.GetValue<string>());
        Assert.DoesNotContain(Path.GetFileName(zipPath), manifest.ToString(), StringComparison.Ordinal);
        Assert.False(manifest["PartialExport"]?.GetValue<bool>());
        Assert.Contains(manifest["Entries"]!.AsArray(), entry => entry!["ArchiveEntry"]?.GetValue<string>() == "manifest.json");
        foreach (JsonNode? entry in manifest["Entries"]!.AsArray())
        {
            Assert.Equal(archive.GetEntry(entry!["ArchiveEntry"]!.GetValue<string>()!)!.Length, entry["Bytes"]?.GetValue<long>());
        }
        Assert.Equal(archive.Entries.Sum(static entry => entry.Length), result.ExportedBytes);
    }

    [Fact]
    public void ExportBundle_IncludeSensitive_WritesRawLogs()
    {
        using var directory = new TestScopedDirectory(nameof(ExportBundle_IncludeSensitive_WritesRawLogs));
        File.WriteAllText(Path.Combine(directory.Root, AppConstants.Files.LogFileName), @"Opened C:\Users\ExampleUser\Music\track.mp3 for routine 'Office WiFi'.");

        string zipPath = Path.Combine(directory.Root, "bundle-sensitive.zip");
        DiagnosticBundleExportService.ExportBundle(directory.Root, zipPath, CreatePayloads(), includeSensitive: true);

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string logContent = ReadEntry(archive, "logs/AudioPilot.log");
        Assert.Contains(@"C:\Users\ExampleUser\Music\track.mp3", logContent, StringComparison.Ordinal);
        Assert.Contains("Office WiFi", logContent, StringComparison.Ordinal);

        JsonObject manifest = JsonNode.Parse(ReadEntry(archive, "manifest.json"))!.AsObject();
        Assert.Equal("sensitive", manifest["RedactionMode"]?.GetValue<string>());
        Assert.Equal(Path.GetFileName(zipPath), manifest["ExportFileName"]?.GetValue<string>());
    }

    [Fact]
    public void ExportBundle_RedactedMode_SanitizesQuotedValuesEvenWhenRuntimeLogRedactionIsDisabled()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = false } });
        try
        {
            using var directory = new TestScopedDirectory(nameof(ExportBundle_RedactedMode_SanitizesQuotedValuesEvenWhenRuntimeLogRedactionIsDisabled));
            File.WriteAllText(Path.Combine(directory.Root, AppConstants.Files.LogFileName), "Routine 'Office WiFi' switched device 'Desk Speakers'.");

            string zipPath = Path.Combine(directory.Root, "bundle-redacted.zip");
            DiagnosticBundleExportService.ExportBundle(directory.Root, zipPath, CreatePayloads(), includeSensitive: false);

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            string logContent = ReadEntry(archive, "logs/AudioPilot.log");

            Assert.DoesNotContain("Office WiFi", logContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Desk Speakers", logContent, StringComparison.Ordinal);
            Assert.Contains("'len=", logContent, StringComparison.Ordinal);
        }
        finally
        {
            LogPrivacy.ApplySettings(null);
        }
    }

    [Fact]
    public void ExportBundle_WhenLogsAreMissing_ReturnsPartialBundleInsteadOfFailing()
    {
        using var directory = new TestScopedDirectory(nameof(ExportBundle_WhenLogsAreMissing_ReturnsPartialBundleInsteadOfFailing));
        string zipPath = Path.Combine(directory.Root, "bundle.zip");

        DiagnosticBundleExportResult result = DiagnosticBundleExportService.ExportBundle(directory.Root, zipPath, CreatePayloads(), includeSensitive: false);

        Assert.True(result.PartialExport);
        Assert.Contains(result.Entries, entry => entry.Status == "missing" && entry.ArchiveEntry == "logs/");

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        JsonObject manifest = JsonNode.Parse(ReadEntry(archive, "manifest.json"))!.AsObject();
        Assert.True(manifest["PartialExport"]?.GetValue<bool>());
    }

    [Fact]
    public void ExportBundle_ReplacesAnExistingDestinationWithACompleteArchive()
    {
        using var directory = new TestScopedDirectory(nameof(ExportBundle_ReplacesAnExistingDestinationWithACompleteArchive));
        string zipPath = Path.Combine(directory.Root, "bundle.zip");
        File.WriteAllText(zipPath, "previous export");

        DiagnosticBundleExportService.ExportBundle(directory.Root, zipPath, CreatePayloads(), includeSensitive: false);

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "bundle.zip.*.tmp"));
    }

    [Fact]
    public void ExportBundle_WhenLogIsLocked_RecordsReadFailureAndKeepsDiagnostics()
    {
        using var directory = new TestScopedDirectory(nameof(ExportBundle_WhenLogIsLocked_RecordsReadFailureAndKeepsDiagnostics));
        string logPath = Path.Combine(directory.Root, AppConstants.Files.LogFileName);
        File.WriteAllText(logPath, "private log content");
        using var lockedLog = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string zipPath = Path.Combine(directory.Root, "bundle.zip");

        DiagnosticBundleExportResult result = DiagnosticBundleExportService.ExportBundle(directory.Root, zipPath, CreatePayloads(), includeSensitive: false);

        Assert.True(result.PartialExport);
        DiagnosticBundleExportEntryResult failure = Assert.Single(result.Entries, entry => entry.Status == "unavailable");
        Assert.Equal("logs/AudioPilot.log", failure.ArchiveEntry);
        Assert.Equal("IOException", failure.ErrorType);
        Assert.Equal("0x80070020", failure.HResult);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        Assert.NotNull(archive.GetEntry("diagnostics/status.json"));
        Assert.Null(archive.GetEntry("logs/AudioPilot.log"));
        string manifestText = ReadEntry(archive, "manifest.json");
        JsonObject manifest = JsonNode.Parse(manifestText)!.AsObject();
        JsonNode failedEntry = Assert.Single(manifest["Entries"]!.AsArray(), entry => entry!["Status"]!.GetValue<string>() == "unavailable")!;
        Assert.Equal(failure.ErrorType, failedEntry["ErrorType"]!.GetValue<string>());
        Assert.Equal(failure.HResult, failedEntry["HResult"]!.GetValue<string>());
        Assert.DoesNotContain(directory.Root, manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private log content", manifestText, StringComparison.Ordinal);
    }

    [Fact]
    public void AddLogEntries_WhenArchiveWriteFails_PropagatesFailure()
    {
        using var directory = new TestScopedDirectory(nameof(AddLogEntries_WhenArchiveWriteFails_PropagatesFailure));
        File.WriteAllText(Path.Combine(directory.Root, AppConstants.Files.LogFileName), "log content");
        using var stream = new FailingWriteStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entries = new List<DiagnosticBundleExportEntryResult>();

        IOException exception = Assert.Throws<IOException>(() =>
            DiagnosticBundleExportService.AddLogEntries(archive, directory.Root, includeSensitive: false, entries));

        Assert.Equal("archive-write-failed", exception.Message);
        Assert.DoesNotContain(entries, entry => entry.Status == "unavailable");
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        private bool _failed;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("archive-write-failed");
            }

            base.Write(buffer.ToArray(), 0, buffer.Length);
        }
    }

    private static DiagnosticBundlePayloads CreatePayloads()
    {
        return new DiagnosticBundlePayloads(
            StatusJson: """{"status":"ok"}""",
            HistoryJson: """{"entries":[]}""",
            MediaStatusJson: """{"available":false}""",
            ConfigValidationJson: """{"isValid":true}""");
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Missing archive entry {name}.");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
