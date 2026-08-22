using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayPlayPauseIntegrationTests
{
    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseHidden_WhenNoSessionContextExists()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(
            MediaOverlayCommand.PlayPause,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.False(result.ShowOverlay);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_PlayPauseHidden_IncludesNoSessionDiagnostics()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.PlayPause,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.Hidden, result.Overlay.Kind);
        Assert.Equal("media-overlay-no-session", result.DiagCode);
        Assert.NotNull(result.PlayPauseDiagnostics);
        Assert.Equal("no-session-context", result.PlayPauseDiagnostics.Value.FinalPath);
        Assert.Equal("hidden", result.PlayPauseDiagnostics.Value.Outcome);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseReturnsTrack_WhenImmediateSnapshotChanges()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: MediaOverlayTestHarness.CreateQueuedCurrentSnapshotOverride(
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Track A", "Artist A", null, "youtube", 12),
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "youtube", 12)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Track A", result.Title);
        Assert.Equal("Artist A", result.Artist);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_PlayPauseResolved_IncludesFinalPathDiagnostics()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: MediaOverlayTestHarness.CreateQueuedCurrentSnapshotOverride(
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Track A", "Artist A", null, "youtube", 12),
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "youtube", 12)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.PlayPause,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Overlay.Kind);
        Assert.Equal("media-overlay-play-pause-resolved", result.DiagCode);
        Assert.NotNull(result.PlayPauseDiagnostics);
        Assert.Equal("immediate-current-snapshot", result.PlayPauseDiagnostics.Value.FinalPath);
        Assert.Equal("changed", result.PlayPauseDiagnostics.Value.Outcome);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseReusesBaselineTrack_WhenResumeSnapshotHasNoMetadata()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: MediaOverlayTestHarness.CreateQueuedCurrentSnapshotOverride(
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Track A", "Artist A", null, "spotify", 12),
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, null, null, null, "spotify", 12)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Playback resumed", result.Header);
        Assert.Equal("Track A", result.Title);
        Assert.Equal("Artist A", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseReusesBaselineTrack_WhenPauseSnapshotHasNoMetadata()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: MediaOverlayTestHarness.CreateQueuedCurrentSnapshotOverride(
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "spotify", 12),
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, null, null, null, "spotify", 12)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Playback paused", result.Header);
        Assert.Equal("Track A", result.Title);
        Assert.Equal("Artist A", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseReusesBaselineTrack_WhenStatusChangesAndSourceIsMissing()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: MediaOverlayTestHarness.CreateQueuedCurrentSnapshotOverride(
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Track A", "Artist A", null, "spotify", 12),
                new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, null, null, null, null, 12)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Playback resumed", result.Header);
        Assert.Equal("Track A", result.Title);
        Assert.Equal("Artist A", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPausePrefersBaselineSource_WhenAnotherAppRemainsPlaying()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot spotifyPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyPaused = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Spotify Track", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot browserPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser Source", null, "chrome", 12);

        int callCount = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(spotifyPlaying);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(spotifyPaused);
                }

                return Task.FromResult(browserPlaying);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = spotifyPlaying,
                ["chrome"] = browserPlaying,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { spotifyPaused, browserPlaying }));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Playback paused", result.Header);
        Assert.Equal("Spotify Track", result.Title);
        Assert.Equal("Spotify Artist", result.Artist);
    }

    [Fact]
    public void TryResolveChangedPlayPauseSnapshot_PrefersSourceWithObservedPlaybackStateChange()
    {
        var preSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser", null, "chrome", 120),
            ["spotify"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track", "Spotify Artist", null, "spotify", 84),
        };
        var postSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser", null, "chrome", 121),
            ["spotify"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Spotify Track", "Spotify Artist", null, "spotify", 84),
        };

        bool resolved = MediaOverlayPlayPauseResolver.TryResolveChangedPlayPauseSnapshot(
            preSnapshots,
            postSnapshots,
            baselineSource: "chrome",
            stickySource: "spotify",
            out PlayPauseSnapshotResolution resolution);

        Assert.True(resolved);
        Assert.Equal("spotify", resolution.Snapshot.SourceAppUserModelId);
        Assert.Equal(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, resolution.Snapshot.PlaybackStatus);
    }

    [Fact]
    public void TryResolveChangedPlayPauseSnapshot_PrefersTrackRichChangedSource_WhenBaselineCandidateHasNoMetadata()
    {
        var preSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, null, null, null, "chrome", 120),
            ["spotify"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track", "Spotify Artist", null, "spotify", 84),
        };
        var postSnapshots = new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, null, null, null, "chrome", 120),
            ["spotify"] = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Spotify Track", "Spotify Artist", null, "spotify", 84),
        };

        bool resolved = MediaOverlayPlayPauseResolver.TryResolveChangedPlayPauseSnapshot(
            preSnapshots,
            postSnapshots,
            baselineSource: "chrome",
            stickySource: null,
            out PlayPauseSnapshotResolution resolution);

        Assert.True(resolved);
        Assert.Equal("spotify", resolution.Snapshot.SourceAppUserModelId);
        Assert.Equal("Spotify Track", resolution.Snapshot.Title);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseUsesChangedSource_WhenCurrentSessionStaysOnBrowser()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot browserPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser Source", null, "chrome", 120);
        MediaOverlaySessionSnapshot spotifyPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyPaused = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Spotify Track", "Spotify Artist", null, "spotify", 84);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(browserPlaying),
            snapshotsBySourceOverride: MediaOverlayTestHarness.CreateQueuedSnapshotsBySourceOverride(
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chrome"] = browserPlaying,
                    ["spotify"] = spotifyPlaying,
                },
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chrome"] = browserPlaying,
                    ["spotify"] = spotifyPaused,
                }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { browserPlaying, spotifyPaused }));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Playback paused", result.Header);
        Assert.Equal("Spotify Track", result.Title);
        Assert.Equal("Spotify Artist", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseUsesEventAssistToResolvePauseState()
    {
        bool commandSent = false;
        bool eventObserved = false;
        MediaOverlaySessionSnapshot spotifyPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyPaused = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Spotify Track", "Spotify Artist", null, "spotify", 84);

        var adapter = new MediaOverlayEngineTestAdapter(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase) && eventObserved)
                {
                    return Task.FromResult(spotifyPaused);
                }

                return Task.FromResult(spotifyPlaying);
            },
            snapshotsBySourceOverride: MediaOverlayTestHarness.CreateQueuedSnapshotsBySourceOverride(
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase) { ["spotify"] = spotifyPlaying },
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase) { ["spotify"] = spotifyPlaying },
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase) { ["spotify"] = spotifyPlaying }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { spotifyPaused }),
            eventWaitOverride: (_, _, _, _) =>
            {
                eventObserved = true;
                return Task.FromResult(new MediaEventAssistOutcome(true, null));
            });

        MediaOverlayEngineTestAdapterResult adapterResult = await adapter.SendWithBestEffortOverlayAsync(
            MediaOverlayCommand.PlayPause,
            () => { commandSent = true; return true; });
        MediaOverlayResult result = adapterResult.Result;

        Assert.True(commandSent);
        Assert.True(eventObserved);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Playback paused", result.Header);
        Assert.Equal("Spotify Track", result.Title);
        Assert.NotNull(adapterResult.PlayPauseDiagnostics);
        Assert.Equal("changed-by-source-snapshots", adapterResult.PlayPauseDiagnostics.Value.FinalPath);
        Assert.True(adapterResult.PlayPauseDiagnostics.Value.UsedEventAssist);
    }
}
