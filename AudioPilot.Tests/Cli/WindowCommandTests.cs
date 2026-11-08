using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Cli;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class WindowCommandTests
{
    [Theory]
    [InlineData("window status --json", CliAction.WindowStatus)]
    [InlineData("window reset", CliAction.WindowReset)]
    [InlineData("window set --x -1800 --y 100 --width 600 --height 500 --json", CliAction.WindowSet)]
    [InlineData("window set --width 700", CliAction.WindowSet)]
    public void WindowCommandsRoundTripThroughThePipe(string text, CliAction action)
    {
        Assert.True(CliCommand.TryParse(text.Split(' '), out var command, out var error), error);
        Assert.Equal(action, command.Action);
        Assert.False(CliHostUtilities.IsUiOnlyAction(action));
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var restored));
        Assert.Equal(command.Action, restored.Action);
        Assert.Equal(command.WindowLayout, restored.WindowLayout);
        Assert.Equal(command.JsonOutput, restored.JsonOutput);
        if (text.Contains("--x", StringComparison.Ordinal)) Assert.Equal(-1800, restored.WindowLayout!.X);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("window set")]
    [InlineData("window set --width --json")]
    [InlineData("window set --x 1 --x 2")]
    [InlineData("window set --width 0")]
    [InlineData("window set --height -10")]
    [InlineData("window set --x 1000001")]
    [InlineData("window set --width 1.5")]
    [InlineData("window reset --width 600")]
    [InlineData("window status --x 1")]
    public void InvalidWindowCommandsAreRejectedBeforeExecution(string text)
    {
        Assert.False(CliCommand.TryParse(text.Split(' '), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(CliAction.WindowSet, 0, false)]
    [InlineData(CliAction.WindowSet, 600, true)]
    [InlineData(CliAction.WindowStatus, 600, false)]
    public void PipeCommandsCannotBypassGeometryValidation(CliAction action, int width, bool reset)
    {
        var command = new CliCommand { Action = action, WindowLayout = new(Width: width, Reset: reset) };
        Assert.False(CliCommand.TryFromPipePayload(command.ToPipePayload(), out _));
        Assert.False(CliCommand.TryFromPipePayload(new CliCommand { Action = CliAction.WindowSet }.ToPipePayload(), out _));
    }

    [Theory]
    [InlineData("live", true)]
    [InlineData("pending", false)]
    public void SaveFailuresDistinguishLiveChangesFromUnappliedSavedChanges(string source, bool applied)
    {
        var result = CliOutputFormatter.FormatWindowLayout(new(100, 100, 600, 500, 96, "normal", false, source, false), CliAction.WindowSet, true);
        Assert.Equal(7, result.ExitCode);
        var data = JsonNode.Parse(result.Output!)!["data"]!;
        Assert.False(data["success"]!.GetValue<bool>());
        Assert.Equal(applied, data["applied"]!.GetValue<bool>());
        Assert.Equal("window-layout-save-failed", data["diagCode"]!.GetValue<string>());
    }
}
