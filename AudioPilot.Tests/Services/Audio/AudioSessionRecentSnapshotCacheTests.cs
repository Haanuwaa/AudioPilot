using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionRecentSnapshotCacheTests
{
    [Theory]
    [InlineData(AudioMixerMode.Output, "Master Volume")]
    [InlineData(AudioMixerMode.Input, "Microphone Volume")]
    public void DuplicateEndpointNotification_PreservesSnapshotsAndRefreshesEndpointTimestamp(AudioMixerMode mode, string displayName)
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions = [new(displayName, 50f, "Device", null, null, null)];
        var output = cache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Output, sessions);
        var input = cache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Input, sessions);
        cache.SeedEndpointSnapshotForTests(mode, new("primary", "Device", 50f, false, 1));

        Assert.True(cache.RecordEndpointVolumeNotification(mode, "PRIMARY", 50f, false));

        Assert.Same(output, cache.GetRecentSnapshotData(AudioMixerMode.Output).Snapshot);
        Assert.Same(input, cache.GetRecentSnapshotData(AudioMixerMode.Input).Snapshot);
        Assert.True(cache.GetEndpointSnapshot(mode)!.Value.TimestampTicks > 1);

        Assert.True(cache.RecordEndpointVolumeNotification(mode, "primary", 50f, true));
        Assert.NotSame(output, cache.GetRecentSnapshotData(AudioMixerMode.Output).Snapshot);
        Assert.NotSame(input, cache.GetRecentSnapshotData(AudioMixerMode.Input).Snapshot);
        Assert.False(output[0].IsMuted);
        Assert.False(input[0].IsMuted);
        Assert.True(cache.GetRecentSnapshotData(AudioMixerMode.Output).Snapshot![0].IsMuted);
        Assert.True(cache.GetRecentSnapshotData(AudioMixerMode.Input).Snapshot![0].IsMuted);
    }

    [Fact]
    public void EndpointNotification_RepairsStaleRowsEvenWhenEndpointCacheAlreadyMatches()
    {
        var cache = new AudioSessionRecentSnapshotCache();
        cache.UpdateRecentNoControlsSnapshot(AudioMixerMode.Output, [new("Master Volume", 25f, "Device", null, null, null)]);
        cache.SeedEndpointSnapshotForTests(AudioMixerMode.Output, new("primary", "Device", 50f, false, 1));

        Assert.True(cache.RecordEndpointVolumeNotification(AudioMixerMode.Output, "primary", 50f, false));

        Assert.Equal(50f, cache.GetRecentSnapshotData(AudioMixerMode.Output).Snapshot![0].Volume);
    }

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
