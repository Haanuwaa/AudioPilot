using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliRoutineCommandCoordinatorTests
{
    [Theory]
    [InlineData("volume", true)]
    [InlineData("volume", false)]
    [InlineData("mute", true)]
    [InlineData("mute", false)]
    [InlineData("communications", true)]
    [InlineData("communications", false)]
    public async Task MixedActionFailuresDescribeEveryOutcomeAndHonorRedaction(string action, bool json)
    {
        var routine = new AudioRoutine { Id = "mixed", Name = "Private routine", OutputDeviceId = "out", OutputDeviceName = "Private output" };
        RoutineExecutionResult execution = new(false, "Private output", null, OutputSucceeded: true);
        (string Failed, string Succeeded, string Label) = action switch
        {
            "volume" => ("masterVolumeSucceeded", "micVolumeSucceeded", "master volume"),
            "mute" => ("outputMuteSucceeded", "inputMuteSucceeded", "output mute"),
            _ => ("communicationsOutputSucceeded", "communicationsInputSucceeded", "communications output"),
        };
        execution = action switch
        {
            "volume" => execution with { MasterVolumeSucceeded = false, MicVolumeSucceeded = true },
            "mute" => execution with { OutputMuteSucceeded = false, InputMuteSucceeded = true, MuteFailureDetail = "Could not change 'Private microphone'." },
            _ => execution with { CommunicationsOutputSucceeded = false, CommunicationsInputSucceeded = true, CommunicationsFailureDetail = "Could not switch 'Private headset'." },
        };
        var coordinator = new CliRoutineCommandCoordinator(new FakeRoutineProcessSnapshotProvider(), (_, _) => Task.FromResult(execution), _ => { });
        CliExecutionResult result = await coordinator.RunAsync([routine], "mixed", json, redactOutput: true);
        Assert.Equal(3, result.ExitCode);
        Assert.DoesNotContain("Private", result.Output!, StringComparison.Ordinal);
        if (json)
        {
            var error = JsonNode.Parse(result.Output!)!["data"]!["error"]!;
            Assert.True(error["partialFailure"]!.GetValue<bool>());
            Assert.True(error["outputSucceeded"]!.GetValue<bool>());
            Assert.True(error[Succeeded]!.GetValue<bool>());
            Assert.False(error[Failed]!.GetValue<bool>());
        }
        else
        {
            Assert.Contains("Partial result: succeeded", result.Output!, StringComparison.Ordinal);
            Assert.Contains($"failed {Label}", result.Output!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnknownApplicationStateDoesNotExecuteOrBecomeANotRunningError()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new(42, string.Empty, ProcessName: "Player", IdentityUnavailable: true));
        var history = new List<ExecutionHistoryEntry>();
        var routine = new AudioRoutine { Id = "unknown", Name = "Private routine", RouteToApplication = true, TargetAppPath = @"C:\Apps\Player.exe", OutputDeviceId = "out" };
        var coordinator = new CliRoutineCommandCoordinator(provider, (_, _) => throw new InvalidOperationException("Must not execute."), history.Add);
        var result = await coordinator.RunAsync([routine], "unknown", jsonOutput: true, redactOutput: true);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(CliRoutineExecutionPolicy.TargetAppUnavailableCode, JsonNode.Parse(result.Output!)!["data"]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(CliRoutineExecutionPolicy.TargetAppUnavailableCode, Assert.Single(history).DiagCode);
        Assert.DoesNotContain("Private", result.Output!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 35, "routine-disabled")]
    [InlineData(true, null, "routine-has-no-targets")]
    public async Task RunAsync_InvalidPreconditionRecordsSkipWithoutExecuting(bool enabled, int? volume, string diagnostic)
    {
        var history = new List<ExecutionHistoryEntry>();
        var coordinator = new CliRoutineCommandCoordinator(new FakeRoutineProcessSnapshotProvider(),
            static (_, _) => throw new InvalidOperationException("Execution must not start."), history.Add);
        var routine = new AudioRoutine { Id = "test", Name = "Desk", Enabled = enabled, MasterVolumePercent = volume };

        CliExecutionResult result = await coordinator.RunAsync([routine], "test", jsonOutput: true);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(diagnostic, JsonNode.Parse(result.Output!)!["data"]?["error"]?["code"]?.GetValue<string>());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.True(entry.Skipped);
        Assert.Equal(diagnostic, entry.DiagCode);
    }

    [Fact]
    public async Task RunAsync_ResolvesApplicationProcessAndPreservesRedaction()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new RoutineProcessSnapshot(24, @"C:\Apps\PrivatePlayer.exe"));
        var routine = new AudioRoutine
        {
            Id = "test",
            Name = "Private desk",
            Enabled = true,
            UsesApplicationTrigger = true,
            RouteToApplication = true,
            TriggerAppPath = @"C:\Apps\PrivatePlayer.exe",
            OutputDeviceId = "private-output",
            OutputDeviceName = "Private speakers",
        };
        int? selectedProcess = null;
        var coordinator = new CliRoutineCommandCoordinator(provider, (selectedRoutine, processId) =>
        {
            Assert.Same(routine, selectedRoutine);
            selectedProcess = processId;
            return Task.FromResult(new RoutineExecutionResult(true, routine.OutputDeviceName, null));
        }, static _ => throw new InvalidOperationException("Successful execution owns its history entry."));

        CliExecutionResult result = await coordinator.RunAsync([routine], "test", jsonOutput: true, redactOutput: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(24, selectedProcess);
        Assert.DoesNotContain("Private", result.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain("private-output", result.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PartialEndpointFailureIsNotReportedAsSuccess()
    {
        var routine = new AudioRoutine { Id = "test", Name = "Desk", Enabled = true, OutputDeviceId = "out", InputDeviceId = "in" };
        var coordinator = new CliRoutineCommandCoordinator(new FakeRoutineProcessSnapshotProvider(),
            static (_, _) => Task.FromResult(new RoutineExecutionResult(true, "Speakers", "Mic", OutputSucceeded: true, InputSucceeded: true, InputFailureDetail: "Input unavailable")),
            static _ => { });

        CliExecutionResult result = await coordinator.RunAsync([routine], "test", jsonOutput: true);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("routine-run-failed", result.Output!, StringComparison.Ordinal);
        Assert.Contains("Input unavailable", result.Output!, StringComparison.Ordinal);
    }
}
