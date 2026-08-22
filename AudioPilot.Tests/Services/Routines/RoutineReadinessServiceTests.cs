using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineReadinessServiceTests
{
    [Fact]
    public async Task AllChecksShareInventoryAndTimeWithoutChangingRoutines()
    {
        int devices = 0;
        int clocks = 0;
        AudioRoutine[] routines = [new() { Id = "one", Name = "Private one", OutputDeviceId = "out" }, new() { Id = "two", Name = "Private two", OutputDeviceId = "out" }];
        var reads = new RoutineReadOperations(_ => ++devices == 1 ? [new() { Id = "out" }] : [])
        {
            UtcNow = () => { clocks++; return new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero); },
        };
        var result = await CliRoutineCheckCoordinator.CheckAsync(routines, "--all",
            () => new(reads, new FakeRoutineProcessSnapshotProvider()), true, true, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, devices);
        Assert.Equal(1, clocks);
        Assert.DoesNotContain("Private", result.Output!, StringComparison.Ordinal);
        var data = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!["data"]!;
        Assert.Equal(2, data["count"]!.GetValue<int>());
        Assert.All(routines, routine => Assert.Null(routine.LastRunUtc));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 3)]
    public async Task AllChecksKeepHealthyResultsWhenAnotherRoutineIsBlockedOrUnavailable(bool unavailable, int exitCode)
    {
        var reads = new RoutineReadOperations(playback => playback && unavailable ? throw new IOException() : [new() { Id = playback ? "out" : "in" }]);
        AudioRoutine[] routines = [new() { Id = "first", Enabled = unavailable, OutputDeviceId = "out" }, new() { Id = "second", InputDeviceId = "in" }];
        var result = await CliRoutineCheckCoordinator.CheckAsync(routines, "--all", () => new(reads, new FakeRoutineProcessSnapshotProvider()),
            true, false, TestContext.Current.CancellationToken);
        Assert.Equal(exitCode, result.ExitCode);
        var results = System.Text.Json.Nodes.JsonNode.Parse(result.Output!)!["data"]!["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.False(results[0]!["ready"]!.GetValue<bool>());
        Assert.True(results[1]!["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EmptyBatchDoesNotCreateNativeServicesAndCancellationIsHonored()
    {
        var result = await CliRoutineCheckCoordinator.CheckAsync([], "--all", () => throw new InvalidOperationException(), true, false, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"count\": 0", result.Output!, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliRoutineCheckCoordinator.CheckAsync([], "--all",
            () => throw new InvalidOperationException(), true, false, new CancellationToken(true)));
    }

    [Fact]
    public async Task CheckReportsAllUnmetRequirementsAndCachesDeviceDiscoveryWithoutEditingTheRoutine()
    {
        int reads = 0;
        var operations = new RoutineReadOperations(_ => { reads++; return []; })
        {
            UtcNow = () => new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero),
            IsApplicationRunning = _ => false,
            GetConnectedNetworks = () => [],
        };
        var routine = new AudioRoutine
        {
            Id = "routine-one",
            Name = "Private routine",
            OutputDeviceId = "missing",
            OutputDeviceName = "Private speakers",
            Conditions = new()
            {
                TimeWindow = new() { Start = new(22, 0), End = new(2, 0), TimeZoneId = "UTC" },
                Device = new() { Id = "required", Name = "Private device" },
                RunningAppPath = @"C:\Apps\ExamplePlayer.exe",
                ConnectedNetwork = "Private network",
            },
        };
        var result = await new RoutineReadinessService(operations, new FakeRoutineProcessSnapshotProvider()).CheckAsync(routine, TestContext.Current.CancellationToken);
        Assert.False(result.Ready);
        Assert.False(result.Unavailable);
        Assert.Equal(1, reads);
        Assert.Equal(5, result.Checks.Count(check => check.Status == "blocked"));
        Assert.Equal("missing", routine.OutputDeviceId);
        Assert.Null(routine.LastRunUtc);
        string output = CliOutputFormatter.FormatRoutineCheck(routine, result, true, true);
        foreach (string secret in new[] { "Private", "ExamplePlayer", "routine-one", @"C:\Apps" }) Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains("routine-condition-network-disconnected", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exact", true, "routine-target-available")]
    [InlineData("stable", true, "routine-target-available")]
    [InlineData("name", true, "routine-target-available")]
    [InlineData("ambiguous-stable", false, "routine-target-ambiguous")]
    [InlineData("ambiguous-name", false, "routine-target-ambiguous")]
    [InlineData("absent", false, "routine-target-unavailable")]
    public async Task TargetChecksUseTheExecutionResolver(string state, bool ready, string code)
    {
        CycleDevice[] devices = state switch
        {
            "exact" => [new() { Id = "saved", Name = "Renamed" }],
            "stable" => [new() { Id = "new", Name = "Renamed", StableId = "opaque " }],
            "name" => [new() { Id = "new", Name = "Speakers" }],
            "ambiguous-stable" => [new() { Id = "one", StableId = "opaque " }, new() { Id = "two", StableId = "opaque " }],
            "ambiguous-name" => [new() { Id = "one", Name = "Speakers" }, new() { Id = "two", Name = "Speakers" }],
            _ => [],
        };
        var routine = new AudioRoutine { OutputDeviceId = "saved", OutputDeviceName = "Speakers", OutputDeviceStableId = "opaque " };
        var result = await new RoutineReadinessService(new(_ => devices), new FakeRoutineProcessSnapshotProvider()).CheckAsync(routine, TestContext.Current.CancellationToken);
        Assert.Equal(ready, result.Ready);
        Assert.Equal(code, Assert.Single(result.Checks, check => check.Area == "output").Code);
        Assert.Equal("saved", routine.OutputDeviceId);
    }

    [Fact]
    public async Task UnavailableReadDoesNotHideTheOtherChecksOrExposeExceptionContent()
    {
        var operations = new RoutineReadOperations(_ => [new() { Id = "out" }])
        {
            GetConnectedNetworks = () => throw new InvalidOperationException("Private network and user path"),
        };
        var routine = new AudioRoutine { OutputDeviceId = "out", Conditions = new() { ConnectedNetwork = "Network" } };
        var result = await new RoutineReadinessService(operations, new FakeRoutineProcessSnapshotProvider()).CheckAsync(routine, TestContext.Current.CancellationToken);
        Assert.False(result.Ready);
        Assert.True(result.Unavailable);
        Assert.Equal("ready", Assert.Single(result.Checks, check => check.Area == "output").Status);
        Assert.DoesNotContain("Private", CliOutputFormatter.FormatRoutineCheck(routine, result, true, false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutingDistinguishesAMissingApplicationFromUnverifiedAudioAvailability()
    {
        var processes = new FakeRoutineProcessSnapshotProvider();
        var service = new RoutineReadinessService(new(_ => [new() { Id = "out" }]), processes);
        var routine = new AudioRoutine { OutputDeviceId = "out", RouteToApplication = true, TargetAppPath = @"C:\Apps\ExamplePlayer.exe" };
        var missing = await service.CheckAsync(routine, TestContext.Current.CancellationToken);
        Assert.False(missing.Ready);
        Assert.Equal("waiting-for-app", missing.RoutingState);
        processes.CaptureAllSnapshots.Add(new(42, routine.TargetAppPath));
        var present = await service.CheckAsync(routine, TestContext.Current.CancellationToken);
        Assert.True(present.Ready);
        Assert.Equal("app-running-audio-not-checked", present.RoutingState);
        Assert.Equal(0, processes.TryCaptureCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolumeAndMuteOnlyActionsCheckTheDefaultEndpoint(bool present)
    {
        var reads = new RoutineReadOperations(_ => throw new InvalidOperationException("Unused discovery"))
        {
            GetDefaultDevice = _ => present ? new() { Id = "default", Name = "Microphone" } : null,
        };
        var result = await new RoutineReadinessService(reads, new FakeRoutineProcessSnapshotProvider()).CheckAsync(
            new() { MicVolumePercent = 40, InputMuteAction = RoutineMuteAction.Mute }, TestContext.Current.CancellationToken);
        Assert.Equal(present, result.Ready);
        Assert.Equal(present ? "routine-default-device-available" : "routine-default-device-missing", Assert.Single(result.Checks, check => check.Area == "input").Code);
    }

    [Fact]
    public async Task KnownMuteOwnershipBlockIsReportedWithoutChangingTheEndpoint()
    {
        var reads = new RoutineReadOperations(_ => [])
        {
            GetDefaultDevice = _ => new() { Id = "mic" },
            GetMuteBlockReason = (playback, _) => playback ? null : "Microphone mute is controlled by push-to-talk or a held microphone shortcut.",
        };
        var result = await new RoutineReadinessService(reads, new FakeRoutineProcessSnapshotProvider()).CheckAsync(
            new() { InputMuteAction = RoutineMuteAction.Unmute }, TestContext.Current.CancellationToken);
        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Code == "routine-mute-blocked");
    }

    [Fact]
    public async Task CancellationStopsInspectionBeforeTheNextNativeRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int defaultReads = 0;
        var operations = new RoutineReadOperations(_ => { cancellation.Cancel(); return []; })
        {
            GetDefaultDevice = _ => { defaultReads++; return null; },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoutineReadinessService(operations, new FakeRoutineProcessSnapshotProvider())
            .CheckAsync(new() { OutputDeviceId = "out", MicVolumePercent = 20 }, cancellation.Token));
        Assert.Equal(0, defaultReads);
    }

    [Fact]
    public async Task UnknownOrAmbiguousSelectorDoesNotConstructNativeServices()
    {
        int constructions = 0;
        RoutineReadinessService Create() { constructions++; throw new InvalidOperationException(); }
        foreach (var (routines, selector) in new[]
        {
            ((IReadOnlyList<AudioRoutine>)[], "missing"),
            ((IReadOnlyList<AudioRoutine>)[new() { Id = "one", Name = "Duplicate" }, new() { Id = "two", Name = "Duplicate" }], "Duplicate"),
        })
        {
            var result = await CliRoutineCheckCoordinator.CheckAsync(routines, selector, Create, true, true, TestContext.Current.CancellationToken);
            Assert.Equal(5, result.ExitCode);
        }
        Assert.Equal(0, constructions);
    }
}
