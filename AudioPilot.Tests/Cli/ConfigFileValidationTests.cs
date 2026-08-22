using System.IO.Compression;
using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Cli;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class ConfigFileValidationTests
{
    [Fact]
    public async Task HeadlessFileValidationDoesNotLoadOrCreateSettingsOrNativeServices()
    {
        using var directory = new TestScopedDirectory(nameof(ConfigFileValidationTests));
        string path = Path.Combine(directory.Root, "candidate.json");
        SettingsTransferService.ExportSettings(new Settings(), path);
        var settings = new SettingsService(Path.Combine(directory.Root, "saved"), Path.Combine(directory.Root, "fallback"));
        using var runner = new LocalHeadlessCommandRunner(new LocalHeadlessCommandRunner.RuntimeServiceFactories(
            CreateSettingsService: () => settings,
            CreateAudioService: () => throw new InvalidOperationException("Must not create audio."),
            CreateStartupService: () => throw new InvalidOperationException("Must not change startup.")));
        var result = await runner.ExecuteAsync(new() { Action = CliAction.ConfigValidate, Key = path, AllowAnyPath = true, JsonOutput = true });
        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(directory.Root, "saved")));
        Assert.False(Directory.Exists(Path.Combine(directory.Root, "fallback")));
    }

    [Fact]
    public async Task FileValidationHonorsPathPolicyAndPreCanceledRequests()
    {
        string outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "candidate.json");
        var blocked = await CliConfigFileValidator.ValidateAsync(outside, Path.Combine(Environment.CurrentDirectory, "settings.json"), false, true, true, TestContext.Current.CancellationToken);
        Assert.Equal(5, blocked.ExitCode);
        Assert.Contains("path-blocked", blocked.Output!, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliConfigFileValidator.ValidateAsync(outside, outside, true, true, true, new CancellationToken(true)));
    }

    [Fact]
    public void ParserAndPipePreserveFileOptions()
    {
        Assert.True(CliCommand.TryParse(["config", "validate", "--json", "--file", "settings snapshot.zip", "--redact", "--allow-any-path"], out var command, out _));
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var decoded));
        Assert.Equal("settings snapshot.zip", decoded!.Key);
        Assert.True(decoded.JsonOutput);
        Assert.True(decoded.RedactOutput);
        Assert.True(decoded.AllowAnyPath);
    }

    [Theory]
    [InlineData("--file")]
    [InlineData("--allow-any-path")]
    [InlineData("--replace")]
    public void ParserRejectsIncompleteOrUnsupportedFileOptions(string flag) =>
        Assert.False(CliCommand.TryParse(["config", "validate", flag], out _, out _));

    [Theory]
    [InlineData(".json", false)]
    [InlineData(".zip", true)]
    public async Task ValidFileIsReadOnlyOfflineAndCanRedactPaths(string extension, bool redact)
    {
        using var directory = new TestScopedDirectory(nameof(ConfigFileValidationTests));
        string path = Path.Combine(directory.Root, "Private configuration" + extension);
        Settings settings = new();
        settings.DeviceSwitching.Output.CycleDevices.Add(new() { Id = "disconnected", Name = "Private output" });
        SettingsTransferService.ExportSettings(settings, path);
        byte[] before = File.ReadAllBytes(path);
        string savedPath = Path.Combine(directory.Root, "not-created", "settings.json");
        var result = await CliConfigFileValidator.ValidateAsync(path, savedPath, true, true, redact, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        var data = JsonNode.Parse(result.Output!)!["data"]!;
        Assert.True(data["readOnly"]!.GetValue<bool>());
        Assert.Equal("offline", data["scope"]!.GetValue<string>());
        Assert.Empty(data["warnings"]!.AsArray());
        if (redact) Assert.DoesNotContain("Private", result.Output!, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(savedPath)));
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":\"999.0.0\"}")]
    [InlineData("{\"SchemaVersion\":\"1.0.0\",\"UnexpectedPrivateValue\":true}")]
    [InlineData("{\"SchemaVersion\":\"1.0.0\",\"SchemaVersion\":\"1.0.0\"}")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidImportsAreRejectedWithoutExposingTheirContents(string json)
    {
        using var directory = new TestScopedDirectory(nameof(ConfigFileValidationTests));
        string path = Path.Combine(directory.Root, "private.json");
        File.WriteAllText(path, json);
        var result = await CliConfigFileValidator.ValidateAsync(path, path, true, true, true, TestContext.Current.CancellationToken);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("config-validation-file-invalid", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", result.Output!, StringComparison.Ordinal);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public async Task ZipWithoutSettingsAndMissingFileHaveDistinctValidationAndReadFailures()
    {
        using var directory = new TestScopedDirectory(nameof(ConfigFileValidationTests));
        string path = Path.Combine(directory.Root, "empty.zip");
        using (ZipFile.Open(path, ZipArchiveMode.Create)) { }
        Assert.Equal(5, (await CliConfigFileValidator.ValidateAsync(path, path, true, true, true, TestContext.Current.CancellationToken)).ExitCode);
        File.Delete(path);
        Assert.Equal(3, (await CliConfigFileValidator.ValidateAsync(path, path, true, true, true, TestContext.Current.CancellationToken)).ExitCode);
    }
}
