using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{


    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportLogs_WritesZipArchive()
    {
        using var logRoot = new TestScopedDirectory(nameof(ExecuteAsync_DiagnosticsExportLogs_WritesZipArchive));
        string logPath = Path.Combine(logRoot.Root, AppConstants.Files.LogFileName);
        Directory.CreateDirectory(Path.Combine(logRoot.Root, AppConstants.Files.BackupFolderName));
        string backupPath = Path.Combine(logRoot.Root, AppConstants.Files.BackupFolderName, AppConstants.Files.LogFileName + ".bak");
        File.WriteAllText(logPath, @"Opened C:\Users\ExampleUser\Music\track.mp3 for device 'Desk Speakers'.");
        File.WriteAllText(backupPath, "backup-log");

        string exportPath = Path.Combine(logRoot.Root, "exports", "logs.zip");

        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetLogRootDirectory: () => logRoot.Root));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.DiagnosticsExportLogs,
            Key = exportPath,
            AllowAnyPath = true,
            JsonOutput = true,
            RedactOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(exportPath));

        JObject parsed = JObject.Parse(result.Output!);
        Assert.True(parsed["data"]?["success"]?.Value<bool>());
        Assert.Equal(2, parsed["data"]?["fileCount"]?.Value<int>());

        using var archive = System.IO.Compression.ZipFile.OpenRead(exportPath);
        System.IO.Compression.ZipArchiveEntry currentLog = archive.GetEntry(AppConstants.Files.LogFileName)
            ?? throw new InvalidOperationException("Missing current log entry.");
        Assert.NotNull(archive.GetEntry($"{AppConstants.Files.BackupFolderName}/{AppConstants.Files.LogFileName}.bak"));
        using Stream currentLogStream = currentLog.Open();
        using var reader = new StreamReader(currentLogStream);
        string archivedLog = reader.ReadToEnd();
        Assert.Contains("<path>", archivedLog, StringComparison.Ordinal);
        Assert.DoesNotContain("ExampleUser", archivedLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Desk Speakers", archivedLog, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportLogs_JsonSummary_IncludesExplicitPartialExportFields()
    {
        using var logRoot = new TestScopedDirectory(nameof(ExecuteAsync_DiagnosticsExportLogs_JsonSummary_IncludesExplicitPartialExportFields));
        string logPath = Path.Combine(logRoot.Root, AppConstants.Files.LogFileName);
        File.WriteAllText(logPath, "current-log");

        string exportPath = Path.Combine(logRoot.Root, "exports", "logs.zip");

        using var scope = new HeadlessRunnerScope(
            new Settings(),
            audioOverrides: new LocalHeadlessCommandRunner.AudioOverrides(
                GetLogRootDirectory: () => logRoot.Root));

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.DiagnosticsExportLogs,
            Key = exportPath,
            AllowAnyPath = true,
            JsonOutput = true,
            DiagnosticsExportDetailLevel = CliDiagnosticsExportDetailLevel.Summary,
        });

        Assert.Equal(0, result.ExitCode);

        JObject parsed = JObject.Parse(result.Output!);
        JToken data = Assert.IsType<JObject>(parsed["data"]);
        Assert.Equal("summary", data["detailLevel"]?.Value<string>());
        Assert.False(data["partialExport"]?.Value<bool>());
        Assert.Equal(0, data["missingAtExportCount"]?.Value<int>());
        Assert.Equal(1, data["fileCount"]?.Value<int>());
    }


    [Fact]
    public void GetDiagnosticsHistoryDetail_MissingEntry_ReturnsNotFoundPayload()
    {
        using var scope = new HeadlessRunnerScope(new Settings());

        var (found, output) = scope.Runner.GetDiagnosticsHistoryDetail("missing-op", jsonOutput: true, redactOutput: false);

        Assert.False(found);
        JObject parsed = JObject.Parse(output);
        Assert.Equal("diagnostics-history-not-found", parsed["data"]?["diagCode"]?.Value<string>());
        Assert.Contains("missing-op", parsed["data"]?["error"]?.Value<string>(), StringComparison.Ordinal);
    }

}
