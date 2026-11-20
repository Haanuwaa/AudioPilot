using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{
    [Fact]
    public async Task WindowCommandsPersistWithoutAudioServicesOrSettingsWrites()
    {
        using var workspace = new TestSettingsWorkspace(nameof(WindowCommandsPersistWithoutAudioServicesOrSettingsWrites));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(workspace.Root, "window-placement.json");
        int audioReads = 0, startupReads = 0;
        var factories = new LocalHeadlessCommandRunner.RuntimeServiceFactories(
            CreateSettingsService: () => new SettingsService(workspace.PrimaryDir, workspace.FallbackDir, logger),
            CreateAudioService: () => { audioReads++; throw new InvalidOperationException("Audio must not be created."); },
            CreateStartupService: () => { startupReads++; throw new InvalidOperationException("Startup must not be created."); },
            CreateWindowPlacementService: () => new MainWindowPlacementService(path, logger));
        using (var runner = new LocalHeadlessCommandRunner(factories, logger: logger))
        {
            var status = await runner.ExecuteAsync(new() { Action = CliAction.WindowStatus, JsonOutput = true });
            Assert.Equal(0, status.ExitCode);
            Assert.False(File.Exists(path));
            var set = await runner.ExecuteAsync(new() { Action = CliAction.WindowSet, WindowLayout = new(Width: 700, Height: 500), JsonOutput = true });
            Assert.Equal(0, set.ExitCode);
            var saved = JsonNode.Parse(set.Output!)!["data"]!["layout"]!;
            using var reopened = new LocalHeadlessCommandRunner(factories, logger: logger);
            var read = await reopened.ExecuteAsync(new() { Action = CliAction.WindowStatus, JsonOutput = true });
            Assert.Equal(0, read.ExitCode);
            var loaded = JsonNode.Parse(read.Output!)!["data"]!["layout"]!;
            Assert.Equal(saved["width"]!.GetValue<int>(), loaded["width"]!.GetValue<int>());
            Assert.Equal(saved["height"]!.GetValue<int>(), loaded["height"]!.GetValue<int>());
            Assert.Equal("saved", loaded["source"]!.GetValue<string>());
            Assert.False(loaded["visible"]!.GetValue<bool>());
            Assert.Equal("physical-pixels", loaded["units"]!.GetValue<string>());
        }
        Assert.Equal(0, audioReads);
        Assert.Equal(0, startupReads);
        Assert.False(File.Exists(Path.Combine(workspace.PrimaryDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(workspace.FallbackDir, "settings.json")));
    }
}
