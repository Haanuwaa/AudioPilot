using System.Text.Json;
using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineCommunicationsTests
{
    [Fact]
    public async Task Execution_UsesIndependentRolesAndStableIdentities()
    {
        var requests = new List<RoutineDeviceSwitchRequest>();
        var operations = Operations() with
        {
            GetActiveDevices = playback => playback ? [new() { Id = "new-comm", StableId = "stable" }] : [],
            SwitchDefaultAsync = request => { requests.Add(request); return ValueTask.FromResult((true, (string?)request.Target.Name)); },
        };
        var routine = new AudioRoutine
        {
            OutputDeviceId = "speakers",
            InputDeviceId = "mic",
            CommunicationsOutput = new() { Id = "old-comm", StableId = "stable" },
            CommunicationsInput = new() { Id = "headset", Playback = false },
        };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(PreserveAudioLevels: true), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(4, requests.Count);
        Assert.Equal([Role.Console, Role.Multimedia], requests[0].Roles);
        Assert.Equal([Role.Console, Role.Multimedia], requests[1].Roles);
        Assert.Equal([Role.Communications], requests[2].Roles);
        Assert.Equal([Role.Communications], requests[3].Roles);
        Assert.Equal("new-comm", requests[2].Target.Id);
        Assert.False(requests[0].RestoreMicVolume);
        Assert.False(requests[2].Options.PreserveAudioLevels);
        Assert.False(requests[3].Options.PreserveAudioLevels);
        Assert.Equal("old-comm", routine.CommunicationsOutput.Id);
    }

    [Fact]
    public async Task CommunicationsOnly_LeavesOtherFlowsAloneAndReportsPartialFailure()
    {
        var requests = new List<RoutineDeviceSwitchRequest>();
        var operations = Operations() with
        {
            SwitchDefaultAsync = request => { requests.Add(request); return ValueTask.FromResult((request.Playback, (string?)request.Target.Name)); },
            ApplyVolume = (_, _, _, _) => throw new InvalidOperationException("No volume action requested"),
        };
        var routine = new AudioRoutine { CommunicationsOutput = new() { Id = "out" }, CommunicationsInput = new() { Id = "in", Playback = false } };
        var result = await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.True(result.HasPartialSuccess);
        Assert.Null(result.OutputSucceeded);
        Assert.Null(result.InputSucceeded);
        Assert.True(result.CommunicationsOutputSucceeded);
        Assert.False(result.CommunicationsInputSucceeded);
        Assert.Contains("communications-input", result.CommunicationsFailureDetail);
        Assert.All(requests, request => Assert.Equal([Role.Communications], request.Roles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoleRestoration_PreservesOtherRolesAndExternalChanges(bool external)
    {
        var defaults = new Dictionary<Role, string?> { [Role.Console] = "speaker", [Role.Multimedia] = "music", [Role.Communications] = "old-headset" };
        Action<DataFlow, Role, string?>? changed = null;
        using var lease = new RoutineRoleRestoration(true, "headset", [Role.Communications], role => defaults[role],
            (id, role) => defaults[role] = id, handler => changed += handler, handler => changed -= handler, Logger.Instance);
        defaults[Role.Communications] = "headset";
        lease.Claim();
        if (external)
        {
            changed!(DataFlow.Render, Role.Communications, "external");
            changed(DataFlow.Render, Role.Communications, "headset");
        }
        lease.Restore();
        Assert.Equal(external ? "headset" : "old-headset", defaults[Role.Communications]);
        Assert.Equal("speaker", defaults[Role.Console]);
        Assert.Equal("music", defaults[Role.Multimedia]);
        lease.Dispose();
        Assert.Null(changed);
    }

    [Fact]
    public void RoleRestoration_UnsubscribeFailureStillReleasesOwnership()
    {
        int unsubscribes = 0;
        var lease = new RoutineRoleRestoration(true, "headset", [Role.Communications], _ => "speaker",
            (_, _) => { }, _ => { }, _ => { unsubscribes++; throw new InvalidOperationException("unsubscribe failed"); }, Logger.Instance);
        lease.Claim();
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        var owners = (System.Collections.IDictionary)typeof(RoutineRoleRestoration)
            .GetField("Owners", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        Assert.DoesNotContain(owners.Values.Cast<object>(), owner => ReferenceEquals(owner, lease));
        lease.Dispose();
        Assert.Equal(1, unsubscribes);
    }

    [Fact]
    public void RoleRestoration_NewerNoOpAndMissingOriginalDoNotOverwrite()
    {
        string? current = "first";
        using var older = new RoutineRoleRestoration(false, "headset", [Role.Communications], _ => current,
            (id, _) => current = id, _ => { }, _ => { }, Logger.Instance);
        current = "headset";
        older.Claim();
        using (var newer = new RoutineRoleRestoration(false, "headset", [Role.Communications], _ => current,
            (id, _) => current = id, _ => { }, _ => { }, Logger.Instance)) newer.Claim();
        older.Restore();
        Assert.Equal("headset", current);
        using var missing = new RoutineRoleRestoration(false, "headset", [Role.Console], _ => null,
            (_, _) => throw new InvalidOperationException("Cannot restore an absent default"), _ => { }, _ => { }, Logger.Instance);
        missing.Claim();
        missing.Restore();
    }

    [Fact]
    public async Task Cancellation_ReleasesAndRestoresCompletedRoleChanges()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var change = new Change();
        var operations = Operations() with
        {
            SwitchRolesAsync = _ => { cancellation.Cancel(); return ValueTask.FromResult(new RoutineRoleSwitchResult(true, "speaker", change)); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoutineExecutionService(operations, Logger.Instance)
            .ExecuteAsync(new() { OutputDeviceId = "out", CommunicationsInput = new() { Id = "in", Playback = false } },
                new(CaptureAudioRestoration: true), cancellationToken: cancellation.Token));
        Assert.Equal(1, change.Restores);
        Assert.Equal(1, change.Disposals);
    }

    [Fact]
    public void Editor_ImportAndCliRoundTripCommunicationsOnlyAndRejectWrongFlow()
    {
        using var editor = new RoutineEditorViewModel([new() { Id = "out", StableId = "stable", Name = "Private headset" }], [])
        { Name = "Calls", ShowInTrayMenu = true, SelectedCommunicationsOutputIndex = 1 };
        Assert.Null(editor.Validate());
        var routine = RoutineTransferService.ParseSingleRoutine(JsonSerializer.Serialize(editor.BuildRoutine().Clone(), SettingsJson.Options));
        var settings = new Settings { Routines = new() { Items = [routine] } };
        SettingsValidationService.Normalize(settings);
        routine = Assert.Single(settings.Routines.Items);
        Assert.True(routine.HasExecutionTarget);
        Assert.Equal("stable", routine.CommunicationsOutput!.StableId);
        using var reopened = new RoutineEditorViewModel([], [], routine);
        Assert.True(reopened.IsCommunicationsExpanded);
        Assert.Null(reopened.Validate());
        Assert.Equal(routine.CommunicationsOutput, reopened.BuildRoutine().CommunicationsOutput);
        string details = CliOutputFormatter.FormatRoutineDetails(routine, true, true);
        Assert.DoesNotContain("Private headset", details);
        Assert.DoesNotContain("\"stable\"", details);
        routine.CommunicationsInput = new() { Id = "wrong", Playback = true };
        Assert.NotNull(routine.ValidateCommunicationsTargets());
        routine.CommunicationsInput = null;
        routine.SwitchOutputPerApp = true;
        Assert.NotNull(routine.ValidateCommunicationsTargets());
        Assert.Throws<JsonException>(() => RoutineTransferService.ParseSingleRoutine("{\"CommunicationsOutput\":{\"Id\":\"out\",\"Typo\":true}}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Editor_PreservesStableOnlyTargetAndRejectsAmbiguousMatches(int matches)
    {
        System.Collections.ObjectModel.ObservableCollection<CycleDevice> devices = [];
        for (int index = 0; index < matches; index++) devices.Add(new() { Id = $"out-{index}", StableId = "stable", Name = "Headset" });
        using var editor = new RoutineEditorViewModel(devices, [], new AudioRoutine
        {
            Name = "Calls",
            ShowInTrayMenu = true,
            CommunicationsOutput = new() { StableId = "stable", Name = "Saved headset" },
        });
        Assert.Null(editor.Validate());
        var target = Assert.IsType<RoutineDeviceReference>(editor.BuildRoutine().CommunicationsOutput);
        Assert.Equal("stable", target.StableId);
        Assert.Equal(matches == 1 ? "out-0" : "", target.Id);
        Assert.Equal(matches != 1, editor.IsSelectedCommunicationsOutputUnavailable);
        Assert.False(editor.CanRoutePerApp);
        devices.Clear();
        Assert.True(editor.IsSelectedCommunicationsOutputUnavailable);
        Assert.Equal("stable", editor.BuildRoutine().CommunicationsOutput!.StableId);
        devices.Add(new() { Id = "returned", StableId = "stable", Name = "Headset" });
        Assert.False(editor.IsSelectedCommunicationsOutputUnavailable);
        Assert.Equal("returned", editor.BuildRoutine().CommunicationsOutput!.Id);
        editor.SelectedCommunicationsOutputIndex = 0;
        Assert.True(editor.CanRoutePerApp);
        Assert.Null(editor.BuildRoutine().CommunicationsOutput);
    }

    [Fact]
    public void Editor_PrefersExactEndpointOverEarlierStableIdMatch()
    {
        using var editor = new RoutineEditorViewModel(
            [new() { Id = "other", StableId = "shared" }, new() { Id = "saved", StableId = "shared" }], [],
            new AudioRoutine { CommunicationsOutput = new() { Id = "saved", StableId = "shared" }, OutputDeviceId = "saved", OutputDeviceStableId = "shared" });
        Assert.Equal("saved", editor.BuildRoutine().CommunicationsOutput!.Id);
        Assert.Equal("saved", editor.BuildRoutine().OutputDeviceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingCommunicationsTarget_DoesNotInheritNormalTargetStableIdentity(bool playback)
    {
        List<RoutineDeviceSwitchRequest> requests = [];
        var operations = Operations() with
        {
            GetActiveDevices = _ => [new() { Id = "normal", StableId = "normal-stable", Name = "Normal device" }],
            SwitchDefaultAsync = request => { requests.Add(request); return ValueTask.FromResult((true, (string?)request.Target.Name)); },
        };
        var routine = new AudioRoutine();
        if (playback)
        {
            routine.OutputDeviceId = "normal"; routine.OutputDeviceStableId = "normal-stable";
            routine.CommunicationsOutput = new() { Id = "missing", Name = "Communications device" };
        }
        else
        {
            routine.InputDeviceId = "normal"; routine.InputDeviceStableId = "normal-stable";
            routine.CommunicationsInput = new() { Id = "missing", Name = "Communications device", Playback = false };
        }
        await new RoutineExecutionService(operations, Logger.Instance).ExecuteAsync(routine, new(), cancellationToken: TestContext.Current.CancellationToken);
        var request = Assert.Single(requests, item => item.Communications);
        Assert.Equal("missing", request.Target.Id);
        Assert.Null(request.Target.StableId);
    }

    private sealed class Change : IRoutineAudioChange
    {
        internal int Restores;
        internal int Disposals;
        public void Restore() => Restores++;
        public void Dispose() => Disposals++;
    }

    private static RoutineExecutionOperations Operations() => new(
        _ => [], (_, target) => target,
        (_, _, _, _, _) => Task.FromResult(default(BluetoothReconnectAttemptResult)),
        (_, _, _, _, _) => Task.CompletedTask,
        request => ValueTask.FromResult((true, (string?)request.Target.Name)),
        (_, target, _, _) => ValueTask.FromResult(new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Applied, target.Name)),
        (_, _, _, _) => true);
}
