using System.IO.Compression;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using Newtonsoft.Json.Linq;

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

        string zipPath = Path.Combine(directory.Root, "bundle.zip");
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

        JObject manifest = JObject.Parse(ReadEntry(archive, "manifest.json"));
        Assert.Equal("AudioPilot", manifest["App"]?.Value<string>());
        Assert.Equal("redacted", manifest["RedactionMode"]?.Value<string>());
        Assert.False(manifest["PartialExport"]?.Value<bool>());
        Assert.Contains(manifest["Entries"]!.Children(), entry => entry["ArchiveEntry"]?.Value<string>() == "manifest.json");
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

        JObject manifest = JObject.Parse(ReadEntry(archive, "manifest.json"));
        Assert.Equal("sensitive", manifest["RedactionMode"]?.Value<string>());
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
        JObject manifest = JObject.Parse(ReadEntry(archive, "manifest.json"));
        Assert.True(manifest["PartialExport"]?.Value<bool>());
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
