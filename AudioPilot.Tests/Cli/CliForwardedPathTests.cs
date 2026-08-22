using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliForwardedPathTests
{
    [Fact]
    public void PipePayload_CapturesCallerDirectory_AndAcceptsOlderPayloads()
    {
        JsonObject payload = JsonNode.Parse(new CliCommand { Action = CliAction.ConfigExport, Key = "export.json" }.ToPipePayload())!.AsObject();
        Assert.True(CliCommand.TryFromPipePayload(payload.ToString(), out CliCommand forwarded));
        Assert.Equal(Environment.CurrentDirectory, forwarded.WorkingDirectory);

        payload.Remove("workingDirectory");

        Assert.True(CliCommand.TryFromPipePayload(payload.ToString(), out CliCommand legacy));
        Assert.Null(legacy.WorkingDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    public void PipePayload_RejectsAmbiguousCallerDirectory(string directory)
    {
        JsonObject payload = JsonNode.Parse(new CliCommand { Action = CliAction.ConfigImport, Key = "import.json" }.ToPipePayload())!.AsObject();
        payload["workingDirectory"] = directory;

        Assert.False(CliCommand.TryFromPipePayload(payload.ToString(), out _, out string? failureReason, out _));
        Assert.Equal("invalid-working-directory", failureReason);
    }

    [Fact]
    public async Task ConfigImport_ResolvesRelativePathFromCallerDirectory()
    {
        using var workspace = new TestSettingsWorkspace(nameof(CliForwardedPathTests));
        string callerDirectory = Directory.CreateDirectory(Path.Combine(workspace.Root, "caller")).FullName;
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        settingsService.SaveSettings(new Settings { Theme = AppTheme.Dark });
        File.WriteAllText(Path.Combine(callerDirectory, "import.json"),
            SettingsTransferService.SerializeSettings(new Settings { Theme = AppTheme.Light }));
        CliCommand command = ForwardFromDirectory(new CliCommand
        {
            Action = CliAction.ConfigImport,
            Key = "import.json",
            ReplaceImport = true,
            JsonOutput = true,
        }, callerDirectory);
        using var runner = new LocalHeadlessCommandRunner(new LocalHeadlessCommandRunner.RuntimeServiceFactories(
            CreateSettingsService: () => settingsService));
        string processDirectory = Environment.CurrentDirectory;

        CliExecutionResult result = await runner.ExecuteAsync(command);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AppTheme.Light, settingsService.LoadSettings().Theme);
        Assert.Equal(processDirectory, Environment.CurrentDirectory);
    }

    [Fact]
    public async Task ConcurrentCommands_KeepCallerDirectoriesIsolatedAcrossAwaits()
    {
        using var workspace = new TestSettingsWorkspace(nameof(CliForwardedPathTests));
        string processDirectory = Environment.CurrentDirectory;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CliExecutionResult>[] commands = [RunCommandAsync("first"), RunCommandAsync("second")];
        release.SetResult();

        CliExecutionResult[] results = await Task.WhenAll(commands);

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(processDirectory, Environment.CurrentDirectory);
        Assert.True(CliPathPolicy.TryResolveConfigPath("export.json", string.Empty, false, out string unscoped, out _));
        Assert.Equal(Path.Combine(processDirectory, "export.json"), unscoped);

        Task<CliExecutionResult> RunCommandAsync(string directoryName)
        {
            string callerDirectory = Directory.CreateDirectory(Path.Combine(workspace.Root, directoryName)).FullName;
            var runtime = new FakeRuntime
            {
                NetworkListAsyncOverride = async () =>
                {
                    await release.Task;
                    Assert.True(CliPathPolicy.TryResolveConfigPath("export.json", string.Empty, false, out string resolved, out _));
                    Assert.Equal(Path.Combine(callerDirectory, "export.json"), resolved);
                    Assert.False(CliPathPolicy.TryResolveConfigPath("../outside.json", string.Empty, false, out _, out _));
                    return "resolved";
                },
            };
            return CliCommandExecutor.ExecuteAsync(ForwardFromDirectory(new CliCommand { Action = CliAction.NetworkList }, callerDirectory), runtime);
        }
    }

    private static CliCommand ForwardFromDirectory(CliCommand command, string callerDirectory)
    {
        JsonObject payload = JsonNode.Parse(command.ToPipePayload())!.AsObject();
        payload["workingDirectory"] = callerDirectory;
        Assert.True(CliCommand.TryFromPipePayload(payload.ToString(), out CliCommand forwarded));
        return forwarded;
    }
}
