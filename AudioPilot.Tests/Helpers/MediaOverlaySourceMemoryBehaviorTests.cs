namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlaySourceMemoryBehaviorTests
{
    [Fact]
    public void TryGetRecoveredSource_ReturnsNull_AfterExpiry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new MediaOverlaySourceMemoryStore(() => now);
        store.UpdateRecoveredSource("track-nav", "spotify");

        string? withinTtl = store.TryGetRecoveredSource("track-nav", recoveredSourceTtlSeconds: 8);
        now = now.AddSeconds(9);
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
