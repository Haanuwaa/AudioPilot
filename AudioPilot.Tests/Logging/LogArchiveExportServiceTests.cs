using System.IO.Compression;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

public sealed class LogArchiveExportServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactedExports_PreserveUtf8AcrossWriterBuffersAndReportExactBytes(bool diagnosticBundle)
    {
        using var scope = new TestScopedDirectory(nameof(RedactedExports_PreserveUtf8AcrossWriterBuffersAndReportExactBytes));
        string text = new string('a', 1023) + "😀 日本語\r\n" + new string('b', 8191) + "🎧\n";
        File.WriteAllText(Path.Combine(scope.Root, AppConstants.Files.LogFileName), text);
        string destination = Path.Combine(scope.Root, "export.zip");
        long reportedBytes;
        string entryName;
        if (diagnosticBundle)
        {
            var result = DiagnosticBundleExportService.ExportBundle(scope.Root, destination,
                new DiagnosticBundlePayloads("{}", "{}", "{}", "{}"), includeSensitive: false);
            reportedBytes = Assert.Single(result.Entries, entry => entry.SourceKind == "current-log").Bytes;
            entryName = "logs/" + AppConstants.Files.LogFileName;
        }
        else
        {
            var result = LogArchiveExportService.ExportLogs(scope.Root, destination, redactContent: true);
            reportedBytes = Assert.Single(result.Entries).Bytes;
            entryName = AppConstants.Files.LogFileName;
        }

        using ZipArchive archive = ZipFile.OpenRead(destination);
        using Stream stream = archive.GetEntry(entryName)!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(text);
        Assert.Equal(expected, bytes.ToArray());
        Assert.Equal(expected.LongLength, reportedBytes);
    }

    [Fact]
    public void ExportLogs_IncludesCurrentLogAndBackups()
    {
        using var scope = new TestScopedDirectory(nameof(ExportLogs_IncludesCurrentLogAndBackups));
        string backupDirectory = Path.Combine(scope.Root, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);

        string logPath = Path.Combine(scope.Root, AppConstants.Files.LogFileName);
        string backupPath = Path.Combine(backupDirectory, AppConstants.Files.LogFileName + ".bak");
        File.WriteAllText(logPath, "current-log");
        File.WriteAllText(backupPath, "backup-log");

        string exportPath = Path.Combine(scope.Root, "artifacts", "logs.zip");

        LogArchiveExportResult result = LogArchiveExportService.ExportLogs(scope.Root, exportPath);

        Assert.Equal(exportPath, result.ExportPath);
        Assert.Equal(2, result.ExportedFileCount);
        Assert.True(result.ExportedBytes > 0);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, entry => entry.Status == "exported" && entry.SourceKind == "current" && entry.ArchiveEntry == AppConstants.Files.LogFileName);
        Assert.Contains(result.Entries, entry => entry.Status == "exported" && entry.SourceKind == "backup" && entry.ArchiveEntry == $"{AppConstants.Files.BackupFolderName}/{AppConstants.Files.LogFileName}.bak");

        using var archive = ZipFile.OpenRead(exportPath);
        Assert.NotNull(archive.GetEntry(AppConstants.Files.LogFileName));
        Assert.NotNull(archive.GetEntry($"{AppConstants.Files.BackupFolderName}/{AppConstants.Files.LogFileName}.bak"));
    }

    [Fact]
    public void ExportLogs_Throws_WhenNoLogsExist()
    {
        using var scope = new TestScopedDirectory(nameof(ExportLogs_Throws_WhenNoLogsExist));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LogArchiveExportService.ExportLogs(scope.Root, Path.Combine(scope.Root, "logs.zip")));

        Assert.Contains("No log files", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportLogs_RedactedContent_SanitizesArchivedLogs()
    {
        using var scope = new TestScopedDirectory(nameof(ExportLogs_RedactedContent_SanitizesArchivedLogs));
        string logPath = Path.Combine(scope.Root, AppConstants.Files.LogFileName);
        File.WriteAllText(logPath, @"Opened C:\Users\ExampleUser\Music\track.mp3 for device 'Desk Speakers'.");

        string exportPath = Path.Combine(scope.Root, "redacted-logs.zip");
        LogArchiveExportService.ExportLogs(scope.Root, exportPath, redactContent: true);

        using ZipArchive archive = ZipFile.OpenRead(exportPath);
        ZipArchiveEntry entry = archive.GetEntry(AppConstants.Files.LogFileName)
            ?? throw new InvalidOperationException("Missing current log entry.");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        string content = reader.ReadToEnd();

        Assert.Contains("<path>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ExampleUser", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Desk Speakers", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportLogs_WithoutRedaction_PreservesArchivedLogs()
    {
        using var scope = new TestScopedDirectory(nameof(ExportLogs_WithoutRedaction_PreservesArchivedLogs));
        string logPath = Path.Combine(scope.Root, AppConstants.Files.LogFileName);
        const string rawContent = @"Opened C:\Users\ExampleUser\Music\track.mp3 for device 'Desk Speakers'.";
        File.WriteAllText(logPath, rawContent);

        string exportPath = Path.Combine(scope.Root, "raw-logs.zip");
        LogArchiveExportService.ExportLogs(scope.Root, exportPath);

        using ZipArchive archive = ZipFile.OpenRead(exportPath);
        ZipArchiveEntry entry = archive.GetEntry(AppConstants.Files.LogFileName)
            ?? throw new InvalidOperationException("Missing current log entry.");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);

        Assert.Equal(rawContent, reader.ReadToEnd());
    }

    [Fact]
    public void ExportLogs_ReplacesAnExistingDestinationWithACompleteArchive()
    {
        using var scope = new TestScopedDirectory(nameof(ExportLogs_ReplacesAnExistingDestinationWithACompleteArchive));
        File.WriteAllText(Path.Combine(scope.Root, AppConstants.Files.LogFileName), "current-log");
        string exportPath = Path.Combine(scope.Root, "logs.zip");
        File.WriteAllText(exportPath, "previous export");

        LogArchiveExportService.ExportLogs(scope.Root, exportPath);

        using ZipArchive archive = ZipFile.OpenRead(exportPath);
        Assert.NotNull(archive.GetEntry(AppConstants.Files.LogFileName));
        Assert.Empty(Directory.EnumerateFiles(scope.Root, "logs.zip.*.tmp"));
    }
}
