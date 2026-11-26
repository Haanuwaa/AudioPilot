using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionRecentSnapshotCacheTests
{
    [Theory]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Input, false)]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, true)]
    public void OlderCapture_CannotReplaceStateAfterInvalidationOrNotification(AudioMixerMode mode, bool notification)
    {
        var cache = new AudioSessionRecentSnapshotCache();
        bool playback = mode == AudioMixerMode.Output;
        string label = playback ? "Master Volume" : "Microphone Volume";
        long olderCapture = cache.CaptureGeneration;
        List<AudioSessionSnapshot> oldRows = [new(label, 25, "Old endpoint", null, null, null)];
        cache.UpdateRecentNoControlsSnapshot(olderCapture, mode, oldRows);
        cache.UpdatePrimaryEndpointSnapshot(olderCapture, playback, "endpoint", "Old endpoint", 25, false);

        if (notification)
        {
            Assert.True(cache.RecordEndpointVolumeNotification(mode, "endpoint", 80, true));
        }
        else
        {
            cache.InvalidateRecentMixerSnapshotState();
            long newerCapture = cache.CaptureGeneration;
            cache.UpdateRecentNoControlsSnapshot(newerCapture, mode, [new(label, 80, "New endpoint", null, null, null, true)]);
            cache.UpdatePrimaryEndpointSnapshot(newerCapture, playback, "endpoint", "New endpoint", 80, true);
        }

        cache.UpdateRecentNoControlsSnapshot(olderCapture, mode, oldRows, "obsolete topology");
        cache.UpdatePrimaryEndpointSnapshot(olderCapture, playback, "old-endpoint", "Old endpoint", 25, false);

        Assert.True(cache.TryGetRecentNoControlsSnapshotData(mode, 60_000, out var rows));
        Assert.Equal(80, Assert.Single(rows).Volume);
        Assert.True(rows[0].IsMuted);
        var endpoint = cache.GetEndpointSnapshot(mode);
        Assert.NotNull(endpoint);
        Assert.Equal("endpoint", endpoint.Value.DeviceId);
        Assert.Equal(80, endpoint.Value.VolumePercent);
        Assert.True(endpoint.Value.IsMuted);
        Assert.NotEqual("obsolete topology", cache.GetOutputScanState().PlaybackFingerprint);
    }

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public void ClockMovingBackward_DiscardsRecentSnapshot(AudioMixerMode mode)
    {
        var cache = new AudioSessionRecentSnapshotCache();
        cache.SeedRecentSnapshotForTests(mode, [new("Player", 40f, "Speakers", "player", null, 42)], DateTime.UtcNow.AddMinutes(5));

        Assert.False(cache.TryGetRecentNoControlsSnapshotData(mode, 200, out _));
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, "Master Volume")]
    [InlineData(AudioMixerMode.Input, "Microphone Volume")]
    public void DuplicateEndpointNotification_PreservesSnapshotsAndRefreshesEndpointTimestamp(AudioMixerMode mode, string displayName)
    {
        var cache = new AudioSessionRecentSnapshotCache();
        List<AudioSessionSnapshot> sessions = [new(displayName, 50f, "Device", null, null, null)];
        var output = cache.UpdateRecentNoControlsSnapshot(cache.CaptureGeneration, AudioMixerMode.Output, sessions);
        var input = cache.UpdateRecentNoControlsSnapshot(cache.CaptureGeneration, AudioMixerMode.Input, sessions);
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
        cache.UpdateRecentNoControlsSnapshot(cache.CaptureGeneration, AudioMixerMode.Output, [new("Master Volume", 25f, "Device", null, null, null)]);
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

        cache.UpdateRecentNoControlsSnapshot(cache.CaptureGeneration,
            AudioMixerMode.Output,
            sessions,
            "fingerprint",
            [with(StringComparer.OrdinalIgnoreCase), "dev-1"],
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

        cache.UpdateRecentNoControlsSnapshot(cache.CaptureGeneration, AudioMixerMode.Output, sessions);
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

        cache.UpdateRecentNoControlsSnapshot(cache.CaptureGeneration, AudioMixerMode.Output, sessions);
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
