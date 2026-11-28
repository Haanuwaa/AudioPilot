using System.Collections.ObjectModel;
using System.Text.Json;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineRequirementTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    public void ApplicationRequirement_DistinguishesUnknownFromAbsent(bool? running, bool expected, bool passes)
    {
        var result = RoutineConditionEvaluator.Evaluate(new() { RunningAppPath = @"C:\Apps\Player.exe", ApplicationMustBeRunning = expected },
            new RoutineReadOperations(_ => []) { IsApplicationRunning = _ => running }, TestContext.Current.CancellationToken);
        Assert.Equal(passes, result == null);
        if (!running.HasValue) Assert.Equal("routine-condition-unavailable", result!.Value.Code);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    public void NetworkRequirement_DistinguishesUnknownFromDisconnected(bool? connected, bool expected, bool passes)
    {
        var result = RoutineConditionEvaluator.Evaluate(new() { ConnectedNetwork = "Example network", NetworkMustBeConnected = expected },
            new RoutineReadOperations(_ => []) { GetConnectedNetworks = () => connected.HasValue ? connected.Value ? ["Example network"] : [] : null }, TestContext.Current.CancellationToken);
        Assert.Equal(passes, result == null);
        if (!connected.HasValue) Assert.Equal("routine-condition-unavailable", result!.Value.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DefaultRequirement_UsesTheRequestedDirectionAndIdentity(bool playback)
    {
        bool? requested = null;
        CycleDevice current = new() { Id = "replacement", StableId = "OpaqueIdentity" };
        var reads = new RoutineReadOperations(_ => [current]) { GetDefaultDevice = direction => { requested = direction; return current; } };
        var conditions = new RoutineConditions { DeviceMustBeDefault = true, Device = new() { Id = "old", StableId = "OpaqueIdentity", Playback = playback } };
        Assert.Null(RoutineConditionEvaluator.Evaluate(conditions, reads, TestContext.Current.CancellationToken));
        Assert.Equal(playback, requested);
        Assert.NotNull(RoutineConditionEvaluator.Evaluate(conditions with { Device = conditions.Device with { StableId = "opaqueidentity" } }, reads, TestContext.Current.CancellationToken));
        Assert.Null(RoutineConditionEvaluator.Evaluate(conditions with { Device = conditions.Device with { Id = current.Id } },
            reads with { GetActiveDevices = _ => throw new IOException() }, TestContext.Current.CancellationToken));
        Assert.Equal("routine-condition-unavailable", RoutineConditionEvaluator.Evaluate(conditions,
            reads with { GetDefaultDevice = _ => null }, TestContext.Current.CancellationToken)!.Value.Code);
    }

    [Fact]
    public void InaccessibleApplicationIdentity_DoesNotSatisfyNegativeRequirements()
    {
        const string target = @"C:\Apps\Player.exe";
        Assert.Null(RoutineApplicationRouting.GetApplicationPresence(target, [new(1, "", ProcessName: "Player", IdentityUnavailable: true)]));
        Assert.Null(RoutineApplicationRouting.GetApplicationPresence(target, [new(1, "", IdentityUnavailable: true)]));
        Assert.False(RoutineApplicationRouting.GetApplicationPresence(target, [new(1, "", ProcessName: "Unrelated", IdentityUnavailable: true)]));
        Assert.False(RoutineApplicationRouting.GetApplicationPresence(target, [new(1, @"C:\Other\Player.exe")]));
        Assert.True(RoutineApplicationRouting.GetApplicationPresence(target, [new(1, target), new(2, "", IdentityUnavailable: true)]));
    }

    [Fact]
    public async Task ReadinessBatch_SharesDiscoveryAndCapturesFreshDataOnTheNextRequest()
    {
        var processes = new FakeRoutineProcessSnapshotProvider();
        processes.CaptureAllSnapshots.Add(new(42, @"C:\Apps\Player.exe"));
        var reads = new RoutineReadOperations(_ => []) { ProcessSnapshots = processes, IsApplicationRunning = _ => throw new InvalidOperationException("Must use the request snapshot") };
        var service = new RoutineReadinessService(reads, processes);
        AudioRoutine[] routines = [.. Enumerable.Range(0, 10).Select(index => new AudioRoutine { Id = index.ToString(), RouteToApplication = true, TargetAppPath = @"C:\Apps\Player.exe", MasterVolumePercent = 50 })];
        var first = await service.CheckAllAsync(routines, TestContext.Current.CancellationToken);
        Assert.Equal(1, processes.CaptureAllCallCount);
        Assert.All(first, result => Assert.Equal("app-running-audio-not-checked", result.Result.RoutingState));
        processes.CaptureAllSnapshots.Clear();
        var second = await service.CheckAllAsync(routines, TestContext.Current.CancellationToken);
        Assert.Equal(2, processes.CaptureAllCallCount);
        Assert.All(second, result => Assert.Equal("waiting-for-app", result.Result.RoutingState));
    }

    [Fact]
    public async Task MixedRequirementsAndRouting_UseOneSnapshotWithTheRequiredMetadata()
    {
        const string packaged = "Example.Player_1234567890abc!Main";
        var source = new FakeRoutineProcessSnapshotProvider();
        source.CaptureAllSnapshots.Add(new(1, @"C:\Apps\Player.exe"));
        source.CaptureAllSnapshots.Add(new(2, @"C:\Apps\Packaged.exe", AppUserModelId: packaged));
        var reads = new RoutineReadOperations(_ => []) { ProcessSnapshots = source, IsApplicationRunning = _ => throw new InvalidOperationException("Use the scoped source") };
        var service = new RoutineReadinessService(reads, source);
        var result = await service.CheckAllAsync([
            new() { MasterVolumePercent = 30, RouteToApplication = true, TargetAppPath = @"C:\Apps\Player.exe", Conditions = new() { RunningAppPath = packaged } },
            new() { MasterVolumePercent = 30, Conditions = new() { RunningAppPath = @"C:\Apps\Player.exe" } }], TestContext.Current.CancellationToken);
        Assert.Equal(1, source.CaptureAllCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId, Assert.Single(source.CaptureAllOptionsHistory));
        Assert.All(result, check => Assert.Contains(check.Result.Checks, item => item.Area == "required-app" && item.Status == "ready"));
    }

    [Fact]
    public async Task FailedProcessDiscovery_IsSharedOnlyWithinTheRequest()
    {
        var source = new FailedProcesses();
        var service = new RoutineReadinessService(new(_ => []), source);
        var routine = new AudioRoutine { RouteToApplication = true, TargetAppPath = @"C:\Apps\Player.exe", MasterVolumePercent = 50 };
        var results = await service.CheckAllAsync([routine, routine], TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Reads);
        Assert.All(results, result => Assert.True(result.Result.Unavailable));
        await service.CheckAsync(routine, TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void RichRequirements_RoundTripThroughEditorAndSettingsWithoutChangingLegacyDefaults()
    {
        var devices = new ObservableCollection<CycleDevice> { new() { Id = "out", StableId = "stable", Name = "Speaker" } };
        using var editor = new RoutineEditorViewModel(devices, [], preloadNetworks: false)
        {
            Name = "Example",
            MasterVolumePercentText = "40",
            RequireDevice = true,
            RequiredDeviceStateIndex = 2,
            RequiredApplicationPath = @"C:\Apps\Player.exe",
            RequiredApplicationStateIndex = 1,
            RequiredNetworkName = "Example network",
            RequiredNetworkStateIndex = 1,
        };
        editor.SelectedRequiredDevice = editor.RoutineDeviceChoices[0];
        var settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Routines = new() { Items = [editor.BuildRoutine()] } }))!;
        SettingsValidationService.Normalize(settings);
        var saved = Assert.Single(settings.Routines.Items);
        using var reopened = new RoutineEditorViewModel(devices, [], saved, preloadNetworks: false);
        Assert.Equal(2, reopened.RequiredDeviceStateIndex);
        Assert.Equal(1, reopened.RequiredApplicationStateIndex);
        Assert.Equal(1, reopened.RequiredNetworkStateIndex);
        Assert.Equal(saved.Conditions, reopened.BuildRoutine().Conditions);
        var legacy = JsonSerializer.Deserialize<RoutineConditions>("{\"RunningAppPath\":\"C:\\\\Apps\\\\Player.exe\",\"ConnectedNetwork\":\"Home\"}")!;
        Assert.True(legacy.ApplicationMustBeRunning);
        Assert.True(legacy.NetworkMustBeConnected);
        Assert.False(legacy.DeviceMustBeDefault);
    }

    private sealed class FailedProcesses : IRoutineProcessSnapshotProvider
    {
        public int Reads { get; private set; }
        public RoutineProcessSnapshot? TryCapture(int processId, RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full) => null;
        public List<RoutineProcessSnapshot> CaptureAll(RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full)
        { Reads++; throw new IOException("Unavailable process snapshot"); }
    }
}
