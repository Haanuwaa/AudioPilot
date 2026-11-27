using AudioPilot.Constants;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlaySourceMemoryBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WallClockChanges_DoNotExpireOrProlongSourceAffinity(bool trim)
    {
        using var clock = new ManualTimeProvider();
        var store = new MediaOverlaySourceMemoryStore(clock);
        store.UpdateStickySource("media", "player");
        store.UpdateRecoveredSource("media", "player");
        store.MarkRecentlySignaledSource("player");
        store.MarkTrustedTrackNavigationSource("player");
        Assert.True(store.IsInFirstCommandGraceWindow("next", "player", 1000));
        DateTimeOffset original = clock.GetUtcNow();

        clock.SetUtcNow(original.AddDays(1));
        Assert.Equal("player", store.TryGetStickySource("media", 1));
        Assert.Equal("player", store.TryGetRecoveredSource("media", 1));
        Assert.True(store.HasRecentSignal("player", 1000));
        Assert.True(store.HasTrustedTrackNavigationSource("player", 1000));

        clock.SetUtcNow(original.AddDays(-1));
        clock.Advance(TimeSpan.FromSeconds(Math.Max(AppConstants.MediaOverlay.StickySourceTtlSeconds,
            Math.Max(AppConstants.MediaOverlay.RecentSignalTtlMs, AppConstants.MediaOverlay.RecentTrustedSourceTtlMs) / 1000d) + 1));
        if (trim) store.TrimIfNeeded();

        Assert.Null(store.TryGetStickySource("media", 1));
        Assert.Null(store.TryGetRecoveredSource("media", 1));
        Assert.False(store.HasRecentSignal("player", 1000));
        Assert.False(store.HasTrustedTrackNavigationSource("player", 1000));
        Assert.False(store.IsInFirstCommandGraceWindow("next", "player", 1000));
        Assert.Equal(0, store.StickySourceCount);
        Assert.Equal(0, store.TrustedSourceCount);
    }

    [Fact]
    public void TryGetRecoveredSource_ReturnsNull_AfterExpiry()
    {
        using var clock = new ManualTimeProvider();
        var store = new MediaOverlaySourceMemoryStore(clock);
        store.UpdateRecoveredSource("track-nav", "spotify");

        string? withinTtl = store.TryGetRecoveredSource("track-nav", recoveredSourceTtlSeconds: 8);
        clock.Advance(TimeSpan.FromSeconds(9));
        string? expired = store.TryGetRecoveredSource("track-nav", recoveredSourceTtlSeconds: 8);

        Assert.Equal("spotify", withinTtl);
        Assert.Null(expired);
    }

    [Fact]
    public void TrimIfNeeded_BoundsRecoveredSourceEntries()
    {
        var store = new MediaOverlaySourceMemoryStore();

        for (int index = 0; index < 80; index++)
        {
            store.UpdateRecoveredSource($"group-{index}", $"source-{index}");
        }

        store.TrimIfNeeded();

        int retainedCount = 0;
        for (int index = 0; index < 80; index++)
        {
            if (store.TryGetRecoveredSource($"group-{index}", recoveredSourceTtlSeconds: 60) != null)
            {
                retainedCount++;
            }
        }

        Assert.True(retainedCount <= 64);
    }
}
