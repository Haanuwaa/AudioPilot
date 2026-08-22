using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed class CliRoutineCommandCoordinatorTests
{
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
        Assert.Equal(diagnostic, JObject.Parse(result.Output!)["data"]?["error"]?["code"]?.Value<string>());
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
