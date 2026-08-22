using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionRecentSnapshotCacheTests
{
    [Fact]
    public void UpdateRecentNoControlsSnapshot_AllowsImmediateCacheReuse()
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions =
        [
            new AudioSessionSnapshot("Master Volume", 55f, "Speakers", null, null, null),
            new AudioSessionSnapshot("Player", 40f, "Speakers", "player", null, 42),
        ];

        cache.UpdateRecentNoControlsSnapshot(
            AudioMixerMode.Output,
            sessions,
            "fingerprint",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dev-1" },
            useSelectivePlaybackScan: true);

        bool found = cache.TryGetRecentNoControlsSnapshotData(
            AudioMixerMode.Output,
            AppConstants.Timing.SessionSnapshotFastPathCacheMs,
            out var cached);

        Assert.True(found);
        Assert.Equal(2, cached.Count);
    }

    [Fact]
    public void RecordEndpointVolumeNotification_UpdatesCachedSharedRow()
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions =
        [
            new AudioSessionSnapshot("Master Volume", 10f, "Speakers", null, null, null),
        ];

        cache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Output, sessions);
        cache.SeedEndpointSnapshotForTests(
            AudioMixerMode.Output,
            new AudioSessionRecentSnapshotCache.EndpointSnapshotEntry(
                "dev-1",
                "Speakers",
                10f,
                IsMuted: false,
                TimestampTicks: DateTime.UtcNow.Ticks));
        bool updated = cache.RecordEndpointVolumeNotification(AudioMixerMode.Output, "dev-1", 73f, isMuted: true);
        cache.TryGetRecentNoControlsSnapshotData(AudioMixerMode.Output, AppConstants.Timing.SessionSnapshotFastPathCacheMs, out var cached);

        Assert.True(updated);
        Assert.Equal(73f, cached[0].Volume);
        Assert.True(cached[0].IsMuted);
    }

    [Fact]
    public void RecordEndpointVolumeNotification_IgnoresNonPrimaryEndpoint()
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions =
        [
            new AudioSessionSnapshot("Master Volume", 10f, "Speakers", null, null, null),
        ];

        cache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Output, sessions);
        cache.SeedEndpointSnapshotForTests(
            AudioMixerMode.Output,
            new AudioSessionRecentSnapshotCache.EndpointSnapshotEntry(
                "primary",
                "Speakers",
                10f,
                IsMuted: false,
                TimestampTicks: DateTime.UtcNow.Ticks));

        bool updated = cache.RecordEndpointVolumeNotification(AudioMixerMode.Output, "secondary", 73f, isMuted: true);
        cache.TryGetRecentNoControlsSnapshotData(AudioMixerMode.Output, AppConstants.Timing.SessionSnapshotFastPathCacheMs, out var cached);

        Assert.False(updated);
        Assert.Equal(10f, cached[0].Volume);
        Assert.False(cached[0].IsMuted);
    }
}
