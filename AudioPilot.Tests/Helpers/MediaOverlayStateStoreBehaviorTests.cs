namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayStateStoreBehaviorTests
{
    [Fact]
    public void TrimStateIfNeeded_BoundsStickySourceEntries()
    {
        var state = new MediaOverlayStateStore();

        for (int index = 0; index < 80; index++)
        {
            state.UpdateStickySource($"group-{index}", $"source-{index}");
        }

        state.TrimStateIfNeeded();

        Assert.True(state.StickySourceCountForTests <= 64);
    }

    [Fact]
    public void BeginCommand_ThrottlesTrimWork_UntilCommandThreshold()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new MediaOverlayStateStore();
        var coordinator = new MediaOverlaySessionTrackingCoordinator(state, () => now);

        _ = coordinator.BeginCommand();
        Assert.Equal(1, state.TrimStateCallCountForTests);

        for (int index = 0; index < 31; index++)
        {
            _ = coordinator.BeginCommand();
        }

        Assert.Equal(1, state.TrimStateCallCountForTests);

        _ = coordinator.BeginCommand();

        Assert.Equal(2, state.TrimStateCallCountForTests);
    }

    [Fact]
    public void BeginCommand_TriggersTrimAfterTimeThreshold()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new MediaOverlayStateStore();
        var coordinator = new MediaOverlaySessionTrackingCoordinator(state, () => now);

        _ = coordinator.BeginCommand();
        Assert.Equal(1, state.TrimStateCallCountForTests);

        now = now.AddSeconds(29);
        _ = coordinator.BeginCommand();
        Assert.Equal(1, state.TrimStateCallCountForTests);

        now = now.AddSeconds(2);
        _ = coordinator.BeginCommand();

        Assert.Equal(2, state.TrimStateCallCountForTests);
    }

    [Fact]
    public void BeginReadOnlySnapshot_DoesNotSupersedeActiveCommand()
    {
        var state = new MediaOverlayStateStore();
        var coordinator = new MediaOverlaySessionTrackingCoordinator(state);

        long commandSequence = coordinator.BeginCommand();
        long readOnlySequence = coordinator.BeginReadOnlySnapshot();

        coordinator.ThrowIfSuperseded(commandSequence, CancellationToken.None);
        coordinator.ThrowIfSuperseded(readOnlySequence, CancellationToken.None);
        Assert.True(readOnlySequence < 0);
    }

    [Fact]
    public void IsInFirstCommandGraceWindow_ReturnsTrue_AtBoundary()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);

        bool first = store.IsInFirstCommandGraceWindow("next-track", "source-a", firstCommandGraceWindowMs: 900);
        now = now.AddMilliseconds(900);
        bool atBoundary = store.IsInFirstCommandGraceWindow("next-track", "source-a", firstCommandGraceWindowMs: 900);

        Assert.True(first);
        Assert.True(atBoundary);
    }

    [Fact]
    public void IsInFirstCommandGraceWindow_ReturnsFalse_WhenSourceIsEmpty()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);

        bool inGraceWindow = store.IsInFirstCommandGraceWindow("next-track", string.Empty, firstCommandGraceWindowMs: 900);

        Assert.False(inGraceWindow);
    }

    [Fact]
    public void IsInFirstCommandGraceWindow_ReturnsFalse_AfterBoundary()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);

        _ = store.IsInFirstCommandGraceWindow("next-track", "source-a", firstCommandGraceWindowMs: 900);
        now = now.AddMilliseconds(901);

        bool afterBoundary = store.IsInFirstCommandGraceWindow("next-track", "source-a", firstCommandGraceWindowMs: 900);

        Assert.False(afterBoundary);
    }

    [Fact]
    public void IsInFirstCommandGraceWindow_ClearsStickySource_WhenBaselineSourceShifts()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);
        store.UpdateStickySource("next-track", "source-a");

        _ = store.IsInFirstCommandGraceWindow("next-track", "source-a", firstCommandGraceWindowMs: 900);
        now = now.AddMilliseconds(100);
        bool shifted = store.IsInFirstCommandGraceWindow("next-track", "source-b", firstCommandGraceWindowMs: 900);

        Assert.True(shifted);
        Assert.Null(store.TryGetStickySource("next-track", stickySourceTtlSeconds: 60));
    }

    [Fact]
    public void HasRecentSignal_ReturnsTrue_WithinTtl_AndFalseAfterExpiry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);
        store.MarkRecentlySignaledSource("source-b");

        bool withinTtl = store.HasRecentSignal("source-b", recentSignalTtlMs: 2500);
        now = now.AddMilliseconds(2501);
        bool afterExpiry = store.HasRecentSignal("source-b", recentSignalTtlMs: 2500);

        Assert.True(withinTtl);
        Assert.False(afterExpiry);
    }

    [Fact]
    public void HasTrustedTrackNavigationSource_ReturnsTrue_WithinTtl_AndFalseAfterExpiry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);
        store.MarkTrustedTrackNavigationSource("source-b");

        bool withinTtl = store.HasTrustedTrackNavigationSource("source-b", trustedSourceTtlMs: 2500);
        now = now.AddMilliseconds(2501);
        bool afterExpiry = store.HasTrustedTrackNavigationSource("source-b", trustedSourceTtlMs: 2500);

        Assert.True(withinTtl);
        Assert.False(afterExpiry);
    }

    [Fact]
    public void TrimStateIfNeeded_BoundsTrustedTrackNavigationSourceEntries()
    {
        var state = new MediaOverlayStateStore();

        for (int index = 0; index < 80; index++)
        {
            state.MarkTrustedTrackNavigationSource($"source-{index}");
        }

        state.TrimStateIfNeeded();

        Assert.True(state.TrustedSourceCountForTests <= 64);
    }

    [Fact]
    public void TrimIfNeeded_EvictsOldestTrackStreakEntry_AndKeepsRecentEntries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlayTrackStreakStore(() => now);

        store.Update("oldest", unchanged: true, stagnantPosition: true, out _, out _);
        now = now.AddMilliseconds(1);

        for (int index = 0; index < 256; index++)
        {
            store.Update($"recent-{index}", unchanged: true, stagnantPosition: true, out _, out _);
            now = now.AddMilliseconds(1);
        }

        store.TrimIfNeeded();

        store.Update("oldest", unchanged: true, stagnantPosition: true, out int oldestUnchanged, out int oldestStagnant);
        store.Update("recent-255", unchanged: true, stagnantPosition: true, out int recentUnchanged, out int recentStagnant);

        Assert.Equal(1, oldestUnchanged);
        Assert.Equal(1, oldestStagnant);
        Assert.Equal(2, recentUnchanged);
        Assert.Equal(2, recentStagnant);
    }

    [Fact]
    public void TrimIfNeeded_EvictsOldestContextEntry_AndPreservesRecentGraceWindowState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);

        _ = store.IsInFirstCommandGraceWindow("oldest", "source-a", firstCommandGraceWindowMs: 900);
        now = now.AddMilliseconds(1);

        for (int index = 0; index < 64; index++)
        {
            _ = store.IsInFirstCommandGraceWindow($"recent-{index}", "source-a", firstCommandGraceWindowMs: 900);
            now = now.AddMilliseconds(1);
        }

        store.TrimIfNeeded();
        now = now.AddMilliseconds(1000);

        bool oldestWasEvicted = store.IsInFirstCommandGraceWindow("oldest", "source-a", firstCommandGraceWindowMs: 900);
        bool recentStillTracked = store.IsInFirstCommandGraceWindow("recent-63", "source-a", firstCommandGraceWindowMs: 900);

        Assert.True(oldestWasEvicted);
        Assert.False(recentStillTracked);
    }

    [Fact]
    public void TryRecordOverlayTelemetry_CountsTrackChangeKind_AlongsideBrowserOutcome()
    {
        var state = new MediaOverlayStateStore();

        bool flushed = state.TryRecordOverlayTelemetry(
            MediaOverlayTelemetryEvent.TrackShown,
            MediaOverlayTelemetryOutcomeClass.BrowserCandidateConverged,
            TrackNavigationChangeKind.SourceSwitched,
            flushEveryEvents: 1,
            flushIntervalSeconds: 60,
            out MediaOverlayTelemetrySnapshot snapshot);

        Assert.True(flushed);
        Assert.Equal(1, snapshot.Track);
        Assert.Equal(1, snapshot.SourceSwitchedChange);
        Assert.Equal(1, snapshot.BrowserCandidateConverged);
        Assert.Equal(0, snapshot.DirectChange);
        Assert.Equal(0, snapshot.SameTrackRestartChange);
    }
}
