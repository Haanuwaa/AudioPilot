using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

public sealed class LogArchiveExportServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactedExports_RemoveAmbiguousNamesAndLegacyMultilineExceptions(bool diagnosticBundle)
    {
        using var scope = new TestScopedDirectory(nameof(RedactedExports_RemoveAmbiguousNamesAndLegacyMultilineExceptions));
        string content = "title=len=4 hash=ABCDEF12 PrivateTitle\n" +
            "Routine 'len=4 hash=ABCDEF12' PrivateSuffix' failed.\n" +
            "  Exception: IOException: PrivateMessage\nPrivateContinuation\n";
        File.WriteAllText(Path.Combine(scope.Root, AppConstants.Files.LogFileName), content);
        string destination = Path.Combine(scope.Root, "export.zip");
        if (diagnosticBundle) DiagnosticBundleExportService.ExportBundle(scope.Root, destination, new("{}", "{}", "{}", "{}"), includeSensitive: false);
        else LogArchiveExportService.ExportLogs(scope.Root, destination, redactContent: true);
        using var archive = ZipFile.OpenRead(destination);
        var entry = Assert.Single(archive.Entries, item => item.FullName.EndsWith(AppConstants.Files.LogFileName, StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open());
        string sanitized = reader.ReadToEnd();
        Assert.DoesNotContain("Private", sanitized);
        Assert.Contains("Exception: IOException:", sanitized);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactedExports_MatchWholeFileAcrossRecordBoundariesAndFallback(bool diagnosticBundle)
    {
        using var scope = new TestScopedDirectory(nameof(RedactedExports_MatchWholeFileAcrossRecordBoundariesAndFallback));
        const string header = "[2026-11-20 09:10:11.123] [Warning] [Fixture] ";
        const string exception = header + "Exception: IOException: private-message\r\n  private-continuation\r\n";
        const int batchSize = 128 * 1024;
        // End the exception just before a batch boundary, then force the remainder to use the whole-file redactor.
        string first = header + new string('a', batchSize - 30 - header.Length - "😀\r\n".Length - exception.Length) + "😀\r\n" + exception;
        string longRecord = header + "Exception: IOException: private-oversized-" + new string('é', batchSize + 100) + "\r\n  private-continuation\r\n";
        string current = first + header + "status='Playing' device[raw:\"private-名前\"]\n" + longRecord + header + "volume=50\n";
        string backup = "Exception: IOException: private-unstructured\r\n  " + new string('ø', batchSize + 100) + "\r\n  private-tail";
        string logName = AppConstants.Files.LogFileName;
        string backupFolder = AppConstants.Files.BackupFolderName;
        Directory.CreateDirectory(Path.Combine(scope.Root, backupFolder));
        File.WriteAllText(Path.Combine(scope.Root, logName), current, Encoding.BigEndianUnicode);
        File.WriteAllText(Path.Combine(scope.Root, backupFolder, logName + ".bak"), backup, Encoding.UTF8);
        string destination = Path.Combine(scope.Root, "export.zip");

        Dictionary<string, long> reported;
        if (diagnosticBundle)
        {
            var result = DiagnosticBundleExportService.ExportBundle(scope.Root, destination, new("{}", "{}", "{}", "{}"), includeSensitive: false);
            Assert.False(result.PartialExport);
            reported = result.Entries.Where(entry => entry.ArchiveEntry.StartsWith("logs/", StringComparison.Ordinal))
                .ToDictionary(entry => entry.ArchiveEntry["logs/".Length..], entry => entry.Bytes);
        }
        else
        {
            var result = LogArchiveExportService.ExportLogs(scope.Root, destination, redactContent: true);
            reported = result.Entries.ToDictionary(entry => entry.ArchiveEntry, entry => entry.Bytes);
        }

        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal(2, reported.Count);
        foreach (var (name, raw) in new[] { (logName, current), ($"{backupFolder}/{logName}.bak", backup) })
        {
            string wholeFile = LogContentRedactor.Sanitize(raw);
            Assert.DoesNotContain("private-", wholeFile, StringComparison.Ordinal);
            using Stream file = archive.GetEntry((diagnosticBundle ? "logs/" : "") + name)!.Open();
            using var contents = new MemoryStream();
            file.CopyTo(contents);
            byte[] expected = Encoding.UTF8.GetBytes(wholeFile);
            Assert.Equal(expected, contents.ToArray());
            Assert.Equal(expected.LongLength, reported[name]);
        }
    }

    [Theory]
    [InlineData(false, "backup-junction")]
    [InlineData(true, "backup-junction")]
    [InlineData(false, "hard-link")]
    [InlineData(true, "hard-link")]
    [InlineData(false, "root-junction")]
    [InlineData(true, "root-junction")]
    public async Task ExportsSkipRedirectedSourcesAndReportThemUnavailable(bool diagnosticBundle, string redirect)
    {
        using var fixture = new TestScopedDirectory(nameof(ExportsSkipRedirectedSourcesAndReportThemUnavailable));
        bool rootRedirected = redirect == "root-junction";
        bool junction = redirect != "hard-link";
        string logs = Path.Combine(fixture.Root, "logs");
        string unrelated = Directory.CreateDirectory(Path.Combine(fixture.Root, "unrelated")).FullName;
        string backups = Path.Combine(logs, AppConstants.Files.BackupFolderName);
        string current = AppConstants.Files.LogFileName;
        string archiveBackup = Path.Combine(unrelated, current + ".bak");
        if (!rootRedirected)
        {
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, current), "safe log text");
        }
        File.WriteAllText(Path.Combine(unrelated, current), "private-current-fixture-text");
        File.WriteAllText(archiveBackup, "private-fixture-text");

        if (!junction) Directory.CreateDirectory(backups);
        string link = rootRedirected ? logs : junction ? backups : Path.Combine(backups, current + ".bak");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /v:off /c mklink /{(junction ? "J" : "H")} \"%AUDIOPILOT_LOG_TEST_LINK%\" \"%AUDIOPILOT_LOG_TEST_TARGET%\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["AUDIOPILOT_LOG_TEST_LINK"] = link;
        start.Environment["AUDIOPILOT_LOG_TEST_TARGET"] = junction ? unrelated : archiveBackup;
        try
        {
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            string destination = Path.Combine(fixture.Root, "export.zip");
            if (rootRedirected && !diagnosticBundle)
            {
                var error = Assert.Throws<InvalidOperationException>(() => LogArchiveExportService.ExportLogs(logs, destination, redactContent: true));
                Assert.Contains("unavailable", error.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(destination));
                return;
            }
            if (diagnosticBundle)
            {
                var result = DiagnosticBundleExportService.ExportBundle(logs, destination, new("{}", "{}", "{}", "{}"), includeSensitive: false);
                Assert.True(result.PartialExport);
                Assert.Single(result.Entries, entry => entry.Status == "unavailable");
            }
            else
            {
                var result = LogArchiveExportService.ExportLogs(logs, destination, redactContent: true);
                Assert.Equal(1, result.ExportedFileCount);
                Assert.Single(result.Entries, entry => entry.Status == "unavailable");
                var summary = JsonNode.Parse(CliOutputFormatter.FormatLogExportResult(result, CliDiagnosticsExportDetailLevel.Summary,
                    jsonOutput: true, redactOutput: true))!["data"]!;
                Assert.True(summary["partialExport"]!.GetValue<bool>());
                Assert.Equal(1, summary["unavailableSourceCount"]!.GetValue<int>());
            }

            using var zip = ZipFile.OpenRead(destination);
            ZipArchiveEntry? currentEntry = zip.GetEntry((diagnosticBundle ? "logs/" : "") + current);
            if (rootRedirected) Assert.Null(currentEntry);
            else Assert.NotNull(currentEntry);
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith(".bak", StringComparison.Ordinal));
        }
        finally
        {
            if (junction && Directory.Exists(link)) Directory.Delete(link);
        }
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
