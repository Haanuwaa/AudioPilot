using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed class CliDirectDeviceSwitchTests
{
    [Theory]
    [InlineData("output", "--device", "Desk Speakers", false)]
    [InlineData("input", "--device-id", "capture-id", true)]
    public void Parse_DirectSwitch_PreservesSelectorAndGuards(string kind, string flag, string value, bool exactId)
    {
        Assert.True(CliCommand.TryParse(["switch", kind, flag, value, "--require-current", "old-id", "--dry-run", "--json", "--redact"], out var command, out var error), error);
        Assert.Equal(kind == "output" ? CliAction.SwitchOutputToDevice : CliAction.SwitchInputToDevice, command.Action);
        var query = CliDeviceSelectorResolver.Decode(command.Value);
        Assert.Equal(exactId ? CliDeviceSelectorKind.ExactId : CliDeviceSelectorKind.ExactName, query.Kind);
        Assert.Equal(value, query.Value);
        Assert.Equal("old-id", command.Key);
        Assert.True(command.DryRun && command.JsonOutput && command.RedactOutput);
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out var restored));
        Assert.Equal(command.Value, restored.Value);
        Assert.Equal(command.Key, restored.Key);
        Assert.True(restored.DryRun && restored.JsonOutput && restored.RedactOutput);
    }

    [Theory]
    [InlineData("--device", "Speakers", "--reverse")]
    [InlineData("--device-id", "id", "--device", "Speakers")]
    [InlineData("--device", "--json")]
    [InlineData("--device-id")]
    public void Parse_DirectSwitch_RejectsInvalidSelectors(params string[] flags)
    {
        Assert.False(CliCommand.TryParse(["switch", "output", .. flags], out _, out _));
    }

    [Theory]
    [InlineData("[name]speakers", null, false, 0, 1)]
    [InlineData("[id]a", "A", false, 0, 1)]
    [InlineData("[id]a", null, true, 0, 0)]
    [InlineData("[id]Speakers", null, false, 5, 0)]
    [InlineData("[name]a", null, false, 5, 0)]
    [InlineData("[id]a", "different", false, 5, 0)]
    public async Task ExecuteAsync_ResolvesExactTargetsAndHonorsGuards(string selector, string? required, bool dryRun, int exitCode, int expectedWrites)
    {
        int writes = 0;
        var result = await CliDirectDeviceSwitch.ExecuteAsync(new CliCommand
        {
            Action = CliAction.SwitchOutputToDevice,
            Value = selector,
            Key = required,
            DryRun = dryRun,
            JsonOutput = true,
        }, () => [new CycleDevice { Id = "a", Name = "Speakers" }], () => "a", target =>
        {
            Assert.Equal("a", target.Id);
            writes++;
            return Task.FromResult(true);
        });
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(expectedWrites, writes);
        if (exitCode == 0)
        {
            Assert.Equal("a", JsonNode.Parse(result.Output!)!["data"]!["targetDeviceId"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_AmbiguousNames_DoNotSwitchAndRedactCandidateIds(bool json)
    {
        var result = await CliDirectDeviceSwitch.ExecuteAsync(new CliCommand
        {
            Action = CliAction.SwitchInputToDevice,
            Value = "[name]Private Mic",
            JsonOutput = json,
            RedactOutput = true,
        }, () => [new CycleDevice { Id = "private-1", Name = "Private Mic" }, new CycleDevice { Id = "private-2", Name = "Private Mic" }],
            () => null, _ => throw new InvalidOperationException("Must not switch"));
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("device-selector-ambiguous", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Mic", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", result.Output!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 7)]
    public async Task ExecuteAsync_SwitchFailure_ReportsFailure(bool throws, int expectedExit)
    {
        var result = await CliDirectDeviceSwitch.ExecuteAsync(new CliCommand { Action = CliAction.SwitchInputToDevice, Value = "[id]a", JsonOutput = true },
            () => [new CycleDevice { Id = "a", Name = "Mic" }], () => null,
            _ => throws ? throw new InvalidOperationException("Driver disconnected") : Task.FromResult(false));
        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Contains("input-switch-failed", result.Output!, StringComparison.Ordinal);
    }
}
