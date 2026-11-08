using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class MixerInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellationDuringRefreshReplacement_StillSettles(bool shutdown) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerFixture();
        var mixer = fixture.Mixer;
        using var previous = mixer.BeginRefreshCycle();
        using var cancellation = previous.Token.Register(() =>
        {
            if (shutdown) mixer.Cleanup();
            else mixer.TrimIdleState();
        });
        Exception? failure = await Record.ExceptionAsync(() => mixer.RefreshAsync());
        Assert.Null(failure);
        Assert.False(mixer.IsRefreshInProgress);
        await mixer.WaitForRefreshSettlementAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    });

    [Fact]
    public Task GroupDragPreservesOffsetsThroughClamping_AndDoesNotIncludeEndpointControls() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerFixture();
        var first = new AudioSessionItem("Music", 30, false, false, processId: 42);
        var second = new AudioSessionItem("Game", 60, false, false, processId: 43);
        var master = new AudioSessionItem("Master Volume", 80, true, false);
        await fixture.AddAsync(first, second, master);
        fixture.Mixer.ToggleApplicationSelection(first); fixture.Mixer.ToggleApplicationSelection(second); fixture.Mixer.ToggleApplicationSelection(master);
        Assert.Equal(2, fixture.Mixer.SelectedApplicationCount);
        fixture.Mixer.BeginGroupVolumeEdit(first);
        first.Volume = 100; fixture.Mixer.PropagateGroupVolume(first);
        Assert.Equal(100, second.Volume);
        first.Volume = 40; fixture.Mixer.PropagateGroupVolume(first);
        Assert.Equal(70, second.Volume);
        Assert.Equal(80, master.Volume);
        fixture.Mixer.EndGroupVolumeEdit();
        first.Volume = 50; fixture.Mixer.PropagateGroupVolume(first);
        Assert.Equal(70, second.Volume);
        fixture.Mixer.BeginGroupVolumeEdit(master);
        Assert.False(fixture.Mixer.HasSelectedApplications);
    });

    [Fact]
    public Task NamesSelectWhileChevronsRoute_AndGroupMuteUsesTheClickedRowsState() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerFixture();
        var first = new AudioSessionItem("Music", 30, false, false, processId: 42);
        var second = new AudioSessionItem("Game", 60, false, false, processId: 43, isMuted: true);
        var other = new AudioSessionItem("Other", 50, false, false, processId: 44);
        var master = new AudioSessionItem("Master Volume", 80, true, false);
        await fixture.AddAsync(first, second, other, master);
        fixture.Mixer.ToggleApplicationSelectionModeCommand.Execute(null);
        Assert.Equal("Done", fixture.Mixer.SelectionModeText);
        fixture.Mixer.ApplicationCommand.Execute(first); fixture.Mixer.ApplicationCommand.Execute(second);
        Assert.Equal(2, fixture.Mixer.SelectedApplicationCount);
        Assert.Null(first.RoutingDetails);
        fixture.Mixer.ToggleMute(first);
        Assert.True(first.IsMuted); Assert.True(second.IsMuted);
        Assert.False(other.IsMuted); Assert.False(master.IsMuted);
        fixture.Mixer.ToggleMute(second);
        Assert.False(first.IsMuted); Assert.False(second.IsMuted);
        fixture.Mixer.ToggleApplicationSelectionModeCommand.Execute(null);
        Assert.Equal(2, fixture.Mixer.SelectedApplicationCount);
        fixture.Mixer.ApplicationCommand.Execute(other);
        Assert.Equal(1, fixture.Mixer.SelectedApplicationCount);
        Assert.False(first.IsGroupSelected); Assert.False(second.IsGroupSelected); Assert.True(other.IsGroupSelected);
    });

    [Fact]
    public Task ReopeningDuringARoutingWriteWaitsThenReadsTheCommittedPreference() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0;
        string? saved = null;
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => { reads++; return Task.FromResult(new ApplicationRoutingInventory(new(true, false, saved), [new() { Id = "out", Name = "Speakers" }], 123)); },
            async (_, _, _, id, token) => { await write.Task.WaitAsync(token); saved = id; return new(ProcessAudioRoutingResult.Applied, new(true, false, id)); });
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        row.RoutingDetails!.SelectedDevice = row.RoutingDetails.Devices.Single(device => device.Id == "out");
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var reopened = row.RoutingDetails!;
        Assert.True(reopened.IsBusy);
        Assert.Equal(1, reads);
        await fixture.Mixer.RefreshApplicationRoutingAsync();
        Assert.Equal(1, reads);
        write.SetResult();
        await TestExecutionGuards.WaitUntilAsync(() => !reopened.IsBusy, "Reopened routing did not settle.");
        Assert.Equal(2, reads);
        Assert.Equal("out", reopened.SelectedDevice!.Id);
        Assert.False(reopened.HasError);
    });

    [Fact]
    public Task CleanupCancelsAnOutstandingRoutingWrite_WithoutReopeningThePanel() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [new() { Id = "out", Name = "Speakers" }], 123)),
            async (_, _, _, _, token) =>
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            });
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        row.RoutingDetails!.SelectedDevice = row.RoutingDetails.Devices.Single(device => device.Id == "out");
        fixture.Mixer.Cleanup();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(row.RoutingDetails);
        Assert.False(fixture.Mixer.HasSelectedApplications);
    });

    [Fact]
    public Task InputRowsCannotBeGrouped_AndRemovalClearsAnOutputSelection() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var input = new MixerFixture(AudioMixerMode.Input);
        var recording = new AudioSessionItem("Recorder", 50, false, false, processId: 42);
        await input.AddAsync(recording);
        input.Mixer.ToggleApplicationSelection(recording);
        Assert.False(input.Mixer.HasSelectedApplications);
        using var output = new MixerFixture();
        var playback = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await output.AddAsync(playback);
        output.Mixer.ToggleApplicationSelection(playback);
        await output.Mixer.ApplyRefreshResultsAsync(0, ["pid:42"], null, null, TestContext.Current.CancellationToken);
        Assert.False(playback.IsGroupSelected);
        Assert.False(output.Mixer.HasSelectedApplications);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public Task ExpandedRoutingDistinguishesUnknownAndMixedPreferences_FromWindowsDefault(bool available, bool mixed) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => Task.FromResult(new ApplicationRoutingInventory(new(available, mixed, null), [new() { Id = "out", Name = "Speakers" }], 123)),
            (_, _, _, _, _) => throw new InvalidOperationException("No selection was made."));
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        await TestExecutionGuards.WaitUntilAsync(() => row.RoutingDetails is { IsBusy: false }, "Routing did not load.");
        Assert.False(row.RoutingDetails!.SelectedDevice!.IsSelectable);
        Assert.Contains(row.RoutingDetails.Devices, device => device.Id == null && device.Name == "Windows default" && device.IsSelectable);
        Assert.True(row.RoutingDetails.CanEdit);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        Assert.Null(row.RoutingDetails);
    });

    [Fact]
    public Task DefaultSelectionClearsOnlyTheCurrentDirection_AndDoesNotClaimTheStreamMoved() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var changes = new List<(bool Output, string? Id)>();
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => Task.FromResult(new ApplicationRoutingInventory(new(true, false, "out"), [new() { Id = "out", Name = "Speakers" }], 123)),
            (_, output, _, id, _) => { changes.Add((output, id)); return Task.FromResult(new ApplicationRoutingChange(ProcessAudioRoutingResult.Applied, new(true, false, id))); });
        using var fixture = new MixerFixture(AudioMixerMode.Input, operations);
        var row = new AudioSessionItem("Recorder", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        await TestExecutionGuards.WaitUntilAsync(() => row.RoutingDetails is { IsBusy: false }, "Routing did not load.");
        var details = row.RoutingDetails!;
        details.SelectedDevice = details.Devices.Single(device => device.Id == null);
        await TestExecutionGuards.WaitUntilAsync(() => !details.IsBusy, "Routing did not settle.");
        Assert.Equal((false, (string?)null), Assert.Single(changes));
        Assert.Contains("preference saved", details.Status, StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingRoutingDetailsCancelsPendingDiscovery(bool quietRefresh) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0;
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new ApplicationRoutingOperations(async (_, _, _, token) =>
        {
            if (quietRefresh && ++reads == 1) return new ApplicationRoutingInventory(new(true, false, null), [], 123);
            using var registration = token.Register(() => canceled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        }, (_, _, _, _, _) => throw new InvalidOperationException());
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        Task? refresh = quietRefresh ? fixture.Mixer.RefreshApplicationRoutingAsync() : null;
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (refresh != null) await refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(row.RoutingDetails);
    });

    [Fact]
    public Task FailedDiscoveryCanBeRetriedWithoutClosingThePanel() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0;
        var operations = new ApplicationRoutingOperations((_, _, _, _) => ++reads == 1
            ? Task.FromException<ApplicationRoutingInventory>(new InvalidOperationException())
            : Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [], 123)),
            (_, _, _, _, _) => throw new InvalidOperationException());
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var details = row.RoutingDetails!;
        await TestExecutionGuards.WaitUntilAsync(() => !details.IsBusy, "Initial read did not finish.");
        Assert.True(details.HasError);
        Assert.True(details.CanRetry);
        fixture.Mixer.RetryApplicationRoutingCommand.Execute(null);
        await TestExecutionGuards.WaitUntilAsync(() => !details.IsBusy && reads == 2, "Retry did not finish.");
        Assert.False(details.HasError);
        Assert.False(details.NeedsReadRetry);
        Assert.Equal("Windows default", details.SelectedDevice!.Name);
    });

    [Fact]
    public Task AutomaticRefreshCoalesces_LeavesUnchangedChoicesAlone_AndStopsAfterClosing() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0, writes = 0, listChanges = 0;
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => { reads++; return Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [new() { Id = "out", Name = "Speakers" }], 123)); },
            (_, _, _, _, _) => { writes++; throw new InvalidOperationException(); });
        using var fixture = new MixerFixture(routing: operations);
        fixture.Mixer.RefreshApplicationRoutingOnActivation();
        await fixture.Mixer.RefreshApplicationRoutingAsync();
        Assert.Equal(0, reads);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var details = row.RoutingDetails!;
        var choices = details.Devices;
        details.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(details.Devices)) listChanges++; };
        for (int i = 0; i < 20; i++) fixture.Mixer.RefreshApplicationRoutingOnActivation();
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(2, reads);
        Assert.Same(choices, details.Devices);
        Assert.Equal(0, listChanges);
        Assert.Equal(0, writes);
        await fixture.Audio.ResetAllPerAppAudioRoutingAsync(TestContext.Current.CancellationToken);
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(3, reads);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        await fixture.Audio.ResetAllPerAppAudioRoutingAsync(TestContext.Current.CancellationToken);
        fixture.Mixer.RefreshApplicationRoutingOnActivation();
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(3, reads);
    });

    [Fact]
    public Task RefreshWaitsUntilTheDropdownCloses_AndRequestedReadsPickUpExternalChanges() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0, writes = 0;
        string? selected = null;
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => { reads++; return Task.FromResult(new ApplicationRoutingInventory(new(true, false, selected), [new() { Id = "out", Name = "Speakers" }], 123)); },
            (_, _, _, _, _) => { writes++; throw new InvalidOperationException(); });
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var details = row.RoutingDetails!;
        details.IsChoosingDevice = true;
        selected = "out";
        for (int i = 0; i < 10; i++) fixture.Mixer.RefreshApplicationRoutingOnActivation();
        await fixture.Mixer.RefreshApplicationRoutingAsync();
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(1, reads);
        Assert.Null(details.SelectedDevice!.Id);
        details.IsChoosingDevice = false;
        await TestExecutionGuards.WaitUntilAsync(() => reads == 2 && !details.IsReading, "Deferred routing refresh did not finish.");
        Assert.Equal("out", details.SelectedDevice!.Id);
        Assert.Equal(0, writes);
        selected = null;
        await fixture.Mixer.RefreshApplicationRoutingAsync();
        Assert.Equal(3, reads);
        Assert.Null(details.SelectedDevice!.Id);
        Assert.Equal(0, writes);
    });

    [Fact]
    public Task RequestedRefreshWaitsForItsQueuedFollowupRead() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0;
        var second = new TaskCompletionSource<ApplicationRoutingInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource<ApplicationRoutingInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new ApplicationRoutingInventory(new(true, false, null), [], 123);
        var operations = new ApplicationRoutingOperations((_, _, _, _) => ++reads switch
        {
            1 => Task.FromResult(snapshot),
            2 => second.Task,
            _ => third.Task,
        }, (_, _, _, _, _) => throw new InvalidOperationException());
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        Task ongoing = fixture.Mixer.RefreshApplicationRoutingAsync();
        Task requested = fixture.Mixer.RefreshApplicationRoutingAsync();
        try
        {
            second.SetResult(snapshot);
            await TestExecutionGuards.WaitUntilAsync(() => reads == 3, "Follow-up read did not start.");
            Assert.False(requested.IsCompleted);
            third.SetResult(snapshot);
            await Task.WhenAll(ongoing, requested).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(row.RoutingDetails!.IsReading);
        }
        finally { second.TrySetResult(snapshot); third.TrySetResult(snapshot); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BackendCancellationShowsRetry_WhenThePanelLifetimeIsStillActive(bool initialRead) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0;
        var snapshot = new ApplicationRoutingInventory(new(true, false, null), [], 123);
        var operations = new ApplicationRoutingOperations((_, _, _, _) => ++reads == 1 && !initialRead
            ? Task.FromResult(snapshot)
            : Task.FromCanceled<ApplicationRoutingInventory>(new CancellationToken(canceled: true)),
            (_, _, _, _, _) => throw new InvalidOperationException());
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        if (!initialRead) await fixture.Mixer.RefreshApplicationRoutingAsync();
        Assert.True(row.RoutingDetails!.HasError);
        Assert.True(row.RoutingDetails.CanRetry);
        Assert.False(row.RoutingDetails.IsBusy);
        Assert.DoesNotContain("Loading", row.RoutingDetails.Status, StringComparison.Ordinal);
    });

    [Fact]
    public Task BackendCancellationAfterARoutingChangeShowsUnconfirmedFeedback() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [new() { Id = "out", Name = "Speakers" }], 123)),
            (_, _, _, _, _) => Task.FromCanceled<ApplicationRoutingChange>(new CancellationToken(canceled: true)));
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var details = row.RoutingDetails!;
        details.SelectedDevice = details.Devices.Single(device => device.Id == "out");
        await TestExecutionGuards.WaitUntilAsync(() => !details.IsBusy, "Write did not finish.");
        Assert.True(details.HasError);
        Assert.True(details.CanRetry);
        Assert.Contains("could not be confirmed", details.Status, StringComparison.Ordinal);
        Assert.False(details.SelectedDevice!.IsSelectable);
    });

    [Fact]
    public Task ANewSelectionCancelsAnOlderQuietRead_AndCannotBeOverwrittenByIt() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        int reads = 0, writes = 0;
        var oldRead = new TaskCompletionSource<ApplicationRoutingInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = new ApplicationRoutingInventory(new(true, false, null), [new() { Id = "out", Name = "Speakers" }], 123);
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => ++reads == 1 ? Task.FromResult(initial) : oldRead.Task,
            (_, _, _, id, _) => { writes++; return Task.FromResult(new ApplicationRoutingChange(ProcessAudioRoutingResult.Applied, new(true, false, id))); });
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var details = row.RoutingDetails!;
        Task refresh = fixture.Mixer.RefreshApplicationRoutingAsync();
        Assert.True(details.IsReading);
        Assert.True(details.CanEdit);
        details.SelectedDevice = details.Devices.Single(device => device.Id == "out");
        Assert.Equal(1, writes);
        oldRead.SetResult(initial);
        await refresh;
        Assert.Equal("out", details.SelectedDevice!.Id);
        Assert.Contains("preference saved", details.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(details.IsReading);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnconfirmedOrPartialChangesNeverReportASavedPreference(bool error) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var result = error ? ProcessAudioRoutingResult.Failed : ProcessAudioRoutingResult.Applied;
        var operations = new ApplicationRoutingOperations(
            (_, _, _, _) => Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [new() { Id = "out", Name = "Speakers" }], 123)),
            (_, _, _, _, _) => Task.FromResult(new ApplicationRoutingChange(result, default)));
        using var fixture = new MixerFixture(routing: operations);
        var row = new AudioSessionItem("Player", 50, false, false, processId: 42);
        await fixture.AddAsync(row);
        fixture.Mixer.ExpandApplicationCommand.Execute(row);
        var details = row.RoutingDetails!;
        await TestExecutionGuards.WaitUntilAsync(() => !details.IsBusy, "Read did not finish.");
        details.SelectedDevice = details.Devices.Single(device => device.Id == "out");
        await TestExecutionGuards.WaitUntilAsync(() => !details.IsBusy, "Write did not finish.");
        Assert.Equal(error, details.HasError);
        Assert.DoesNotContain("saved", details.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(details.SelectedDevice!.IsSelectable);
        await fixture.Mixer.RefreshApplicationRoutingAsync();
        Assert.Equal(error, details.HasError);
        if (error) Assert.Contains("Could not apply", details.Status, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(42u, false)]
    [InlineData(43u, true)]
    public Task RemovingASelectedAppRetainsTheGestureOnlyWhenItsAnchorSurvives(uint removedProcessId, bool continues) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerFixture();
        var anchor = new AudioSessionItem("First", 30, false, false, processId: 42);
        var peer = new AudioSessionItem("Second", 60, false, false, processId: 43);
        var last = new AudioSessionItem("Last", 20, false, false, processId: 44);
        await fixture.AddAsync(anchor, peer, last);
        foreach (var row in new[] { anchor, peer, last }) fixture.Mixer.ToggleApplicationSelection(row);
        fixture.Mixer.BeginGroupVolumeEdit(anchor);
        anchor.Volume = 40; fixture.Mixer.PropagateGroupVolume(anchor);
        Assert.Equal(30, last.Volume);
        await fixture.Mixer.ApplyRefreshResultsAsync(0, [$"pid:{removedProcessId}"], null, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Mixer.SelectedApplicationCount);
        Assert.False((continues ? peer : anchor).IsGroupSelected);
        anchor.Volume = 50; fixture.Mixer.PropagateGroupVolume(anchor);
        Assert.Equal(continues ? 40 : 30, last.Volume);
        if (continues)
        {
            var replacement = new AudioSessionItem("Replacement", 75, false, false, processId: removedProcessId);
            await fixture.AddAsync(replacement);
            anchor.Volume = 60; fixture.Mixer.PropagateGroupVolume(anchor);
            Assert.Equal(50, last.Volume);
            Assert.Equal(75, replacement.Volume);
            Assert.False(replacement.IsGroupSelected);
        }
    });

    private sealed class MixerFixture : IDisposable
    {
        internal readonly AudioDeviceService Audio;
        private readonly DeviceCacheHelper _cache;
        internal MixerViewModel Mixer { get; }
        internal MixerFixture(AudioMixerMode mode = AudioMixerMode.Output, ApplicationRoutingOperations? routing = null)
        {
            Audio = new(new FakeInputListenPropertyWriter(), perAppAudioRoutingResetter: new FakePerAppAudioRoutingResetter());
            _cache = new(Audio);
            Mixer = routing == null ? new(Audio, Dispatcher.CurrentDispatcher, mode, Logger.Instance, _cache)
                : new(Audio, Dispatcher.CurrentDispatcher, mode, Logger.Instance, _cache, routing);
        }
        internal async Task AddAsync(params AudioSessionItem[] rows)
        {
            await Mixer.ApplyRefreshResultsAsync(0, null, [.. rows.Select(row => (row.IsMaster ? "master:primary" : $"pid:{row.ProcessId}", row))], null, TestContext.Current.CancellationToken);
            foreach (var row in rows) Mixer.DetachOwnedVolumeChangedHandler(row.IsMaster ? "master:primary" : $"pid:{row.ProcessId}", row);
        }
        public void Dispose() { Mixer.Cleanup(); Audio.Dispose(); _cache.Dispose(); }
    }
}
