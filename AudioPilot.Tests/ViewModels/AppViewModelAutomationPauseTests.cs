using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

[Collection("AppDialogServiceIsolation")]
public sealed class AppViewModelAutomationPauseTests
{
    [Fact]
    public Task CliControlsShareTheRuntimePauseState_AndClearOldDeadlines() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelAutomationPauseTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        var model = harness.ViewModel;
        try
        {
            Assert.Equal(2, model.SetAutomaticRoutinePauseFromCli(true, 0, jsonOutput: true).ExitCode);
            Assert.False(model.AutomaticRoutinesPaused);
            var timed = model.SetAutomaticRoutinePauseFromCli(true, 30, jsonOutput: true);
            Assert.Equal(0, timed.ExitCode);
            var state = JsonNode.Parse(timed.Output!)!["data"]!;
            Assert.True(model.AutomaticRoutinesPaused);
            Assert.True(state["automaticPaused"]!.GetValue<bool>());
            Assert.NotNull(state["automaticResumeUtc"]);
            Assert.False(state["hasAutomaticRoutines"]!.GetValue<bool>());
            var indefinite = model.SetAutomaticRoutinePauseFromCli(true, null, jsonOutput: true);
            Assert.Null(JsonNode.Parse(indefinite.Output!)!["data"]!["automaticResumeUtc"]);
            var resumed = model.SetAutomaticRoutinePauseFromCli(false, null, jsonOutput: true);
            Assert.Equal(0, resumed.ExitCode);
            Assert.False(model.AutomaticRoutinesPaused);
            state = JsonNode.Parse(resumed.Output!)!["data"]!;
            Assert.False(state["automaticPaused"]!.GetValue<bool>());
            Assert.Null(state["automaticResumeUtc"]);
            Assert.Equal(0, model.SetAutomaticRoutinePauseFromCli(false, null, jsonOutput: false).ExitCode);
        }
        finally { await model.CleanupAsync(); }
    });
}
