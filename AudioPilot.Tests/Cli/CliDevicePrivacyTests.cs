using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed class CliDevicePrivacyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingHistory_RedactsSuppliedIdentifier(bool jsonOutput)
    {
        string result = CliOutputFormatter.FormatExecutionHistoryNotFound("PrivateOwner", jsonOutput, redactOutput: true);
        Assert.DoesNotContain("PrivateOwner", result, StringComparison.Ordinal);
        Assert.Contains("diagnostics-history-not-found", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingSelector_RedactsApostrophesAndPaths(bool jsonOutput)
    {
        string apostrophe = CliOutputFormatter.FormatDeviceGetError("output", "device-not-found",
            "No active output device matched 'Owner's Private Headset'.", jsonOutput, redactOutput: true);
        string path = CliOutputFormatter.FormatRoutineError(5, "routine-target-unavailable",
            @"Cannot open C:\Users\ExampleUser\Private Folder\application.exe", jsonOutput, redactOutput: true);
        Assert.DoesNotContain("Private", apostrophe, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoutineHistory_RedactsApplicationAndCommunicationsTargets(bool jsonOutput)
    {
        var entry = new ExecutionHistoryEntry("op", DateTimeOffset.UtcNow, ExecutionHistoryKind.Routine, "cli", "routine-run", true, false,
            Target: "App: Private App | Communications output: Private Headset | Communications microphone: Private Mic | Master: 40%");
        string result = CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput: true);
        Assert.DoesNotContain("Private", result, StringComparison.Ordinal);
        Assert.Contains("Master: 40%", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CustomRoutineIdentifiers_AreRedactedInDetailsAndHistory(bool jsonOutput)
    {
        var routine = new AudioRoutine { Id = "PrivateOwner-routine", Name = "Fixture", Triggers = [new RoutineTrigger { Id = "PrivateOwner-trigger" }] };
        string details = CliOutputFormatter.FormatRoutineDetails(routine, jsonOutput, redactOutput: true);
        var entry = new ExecutionHistoryEntry("op", DateTimeOffset.UtcNow, ExecutionHistoryKind.Routine, "cli", "routine-run", true, false, RoutineId: routine.Id);
        string history = CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput, redactOutput: true);
        Assert.DoesNotContain("PrivateOwner", details, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateOwner", history, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VolumeError_RedactsIdentifierAndMessage(bool jsonOutput)
    {
        string result = CliOutputFormatter.FormatVolumeError("master", "volume-get-failed",
            "Could not find device 'private-device'.", jsonOutput, "private-device", redactOutput: true);

        Assert.DoesNotContain("private-device", result, StringComparison.Ordinal);
        Assert.Contains("volume-get-failed", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SwitchPreview_PreservesContractAndHonorsRedaction(bool jsonOutput, bool redactOutput)
    {
        var target = new CycleDevice { Id = "private-target-id", Name = "Private headset" };

        string result = CliOutputFormatter.FormatSwitchPreview("output", "private-current-id", target, jsonOutput, redactOutput);

        Assert.Contains("switch-dry-run", result, StringComparison.Ordinal);
        if (redactOutput)
        {
            Assert.DoesNotContain(target.Id, result, StringComparison.Ordinal);
            Assert.DoesNotContain(target.Name, result, StringComparison.Ordinal);
            Assert.DoesNotContain("private-current-id", result, StringComparison.Ordinal);
            Assert.Contains("device-id[", result, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(target.Id, result, StringComparison.Ordinal);
            Assert.Contains(target.Name, result, StringComparison.Ordinal);
        }

        if (jsonOutput)
        {
            JsonObject data = (JsonObject)JsonNode.Parse(result)!["data"]!;
            Assert.True(data["dryRun"]!.GetValue<bool>());
            Assert.Equal("output", data["kind"]!.GetValue<string>());
            Assert.NotEqual(data["currentDeviceId"]!.GetValue<string>(), data["targetDeviceId"]!.GetValue<string>());
        }
    }
}
