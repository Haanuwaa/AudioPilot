using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayTrackNavigationIntegrationTests
{
    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackHidden_WhenNoSessionContextExists()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.False(result.ShowOverlay);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_NextTrackHidden_IncludesNoSessionDiagCode()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.Hidden, result.Overlay.Kind);
        Assert.Equal("media-overlay-no-session", result.DiagCode);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackUsesSharedMediaStickySource_WhenTrackNavStickyIsCold()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot browserPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser Source", null, "chrome", 120);
        MediaOverlaySessionSnapshot spotifyPaused = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Spotify Track A", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyPlaying = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track A", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyNext = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track B", "Spotify Artist", null, "spotify", 1);

        int currentSnapshotCalls = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;

                if (currentSnapshotCalls <= 3)
                {
                    return Task.FromResult(browserPlaying);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(spotifyNext);
                }

                return Task.FromResult(browserPlaying);
            },
            snapshotsBySourceOverride: MediaOverlayTestHarness.CreateQueuedSnapshotsBySourceOverride(
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chrome"] = browserPlaying,
                    ["spotify"] = spotifyPaused,
                },
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chrome"] = browserPlaying,
                    ["spotify"] = spotifyPlaying,
                },
                new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chrome"] = browserPlaying,
                    ["spotify"] = spotifyPlaying,
                }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { browserPlaying, spotifyNext }));

        MediaOverlayResult playPauseResult = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PlayPause, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, playPauseResult.Kind);
        Assert.Equal("Playback resumed", playPauseResult.Header);
        Assert.Equal("Spotify Track A", playPauseResult.Title);

        MediaOverlayResult nextTrackResult = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.TrackMessage, nextTrackResult.Kind);
        Assert.Equal("Spotify Track B", nextTrackResult.Title);
        Assert.Equal("Spotify Artist", nextTrackResult.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackKeepsBaselineSource_WhenAnotherAppRemainsCurrent()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot spotifyBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track A", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyNext = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track B", "Spotify Artist", null, "spotify", 1);
        MediaOverlaySessionSnapshot browserCurrent = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser Source", null, "chrome", 120);

        int currentSnapshotCalls = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(spotifyBaseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(spotifyNext);
                }

                return Task.FromResult(browserCurrent);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = spotifyBaseline,
                ["chrome"] = browserCurrent,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { spotifyNext, browserCurrent }));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Spotify Track B", result.Title);
        Assert.Equal("Spotify Artist", result.Artist);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_NextTrackChanged_IncludesTrackNavigationDiagnostics()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot baseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "spotify", 84);
        MediaOverlaySessionSnapshot next = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track B", "Artist B", null, "spotify", 1);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: MediaOverlayTestHarness.CreateQueuedCurrentSnapshotOverride(baseline, next),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { next }));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Overlay.Kind);
        Assert.Equal("media-overlay-track-changed", result.DiagCode);
        Assert.NotNull(result.TrackNavigationDiagnostics);
        Assert.Equal("changed", result.TrackNavigationDiagnostics.Value.Outcome);
        Assert.NotNull(result.ElapsedMs);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackUsesRecoveredAlternateTrack_WhenPreferredSourceStagnates()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot spotifyBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track A", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot browserChanged = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Track B", "Browser Artist", null, "chrome", 1);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                if (string.Equals(preferredSource, "chrome", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(browserChanged);
                }

                return Task.FromResult(spotifyBaseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = spotifyBaseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { spotifyBaseline, browserChanged }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Browser Track B", result.Title);
        Assert.Equal("Browser Artist", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackUsesSettledAlternateTrack_WhenInitialAlternateSnapshotWasTransient()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot braveBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Live Stream", "SyntheticStreamerA", null, "brave", 343);
        MediaOverlaySessionSnapshot transientAlternate = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Transient Track A", "Transient Artist A", "Transient Album A", "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 0);
        MediaOverlaySessionSnapshot settledAlternate = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Settled Track A", "Settled Artist A", "Settled Album A", "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 1);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                if (string.Equals(preferredSource, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(settledAlternate);
                }

                return Task.FromResult(braveBaseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = braveBaseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, transientAlternate }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Settled Track A", result.Title);
        Assert.Equal("Settled Artist A", result.Artist);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_NextTrackUsesCommandTargetSource_WhenCurrentBrowserStagnates()
    {
        bool commandSent = false;
        const string targetSource = "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4";
        MediaOverlaySessionSnapshot braveBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "NEW TRYHARD ACC", "humzh", null, "Brave", 1724);
        MediaOverlaySessionSnapshot spotifyPreCommand = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "90210", "Travis Scott", "Rodeo", targetSource, 42);
        MediaOverlaySessionSnapshot spotifyChanged = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "20 Min", "Lil Uzi Vert", "Luv Is Rage 2 (Deluxe)", targetSource, 0);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                return Task.FromResult(string.Equals(preferredSource, targetSource, StringComparison.OrdinalIgnoreCase)
                    ? spotifyChanged
                    : braveBaseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Brave"] = braveBaseline,
                [targetSource] = spotifyPreCommand,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, spotifyChanged }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return Task.FromResult(true);
            },
            () => targetSource,
            TestContext.Current.CancellationToken);

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Overlay.Kind);
        Assert.Equal("20 Min", result.Overlay.Title);
        Assert.Equal("Lil Uzi Vert", result.Overlay.Artist);
        Assert.Equal("media-overlay-track-changed", result.DiagCode);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackDoesNotAdoptFarPlayingAlternate_WhenPreferredSourceStagnates()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot browserBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Target Track", "Artist", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 0);
        MediaOverlaySessionSnapshot wrongAlternate = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Far Alternate Stream", "SyntheticStreamHost", null, "brave", 812);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(browserBaseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = browserBaseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { browserBaseline, wrongAlternate }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Kind);
        Assert.DoesNotContain("Far Alternate Stream", result.Title ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Next track unchanged", result.Message);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackDoesNotAdoptPreexistingCrossSourceAlternate_WhenBaselineStillPlaying()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot braveBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "SyntheticStreamHost", "SyntheticStreamHost", null, "brave", 812);
        MediaOverlaySessionSnapshot chromiumPreCommand = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Preexisting Track A", "Preexisting Artist A", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 0);
        MediaOverlaySessionSnapshot chromiumChanged = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Changed Track A", "Changed Artist A", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 0);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(braveBaseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = braveBaseline,
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = chromiumPreCommand,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, chromiumChanged }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Kind);
        Assert.Equal("Next track unchanged", result.Message);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackStillRejectsPreexistingCrossSourceAlternate_AfterRepeatedUnchangedCommands()
    {
        MediaOverlaySessionSnapshot braveBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Baseline Stream A", "Baseline Stream A", null, "brave", 4028);
        MediaOverlaySessionSnapshot chromiumPreCommand = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Preexisting Track B", "Preexisting Artist B", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 2);
        MediaOverlaySessionSnapshot chromiumChanged = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Changed Track B", "Changed Artist B", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 0);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(braveBaseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = braveBaseline,
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = chromiumPreCommand,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, chromiumChanged }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayResult first = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);
        MediaOverlayResult second = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.PlainMessage, first.Kind);
        Assert.Equal("Next track unchanged", first.Message);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, second.Kind);
        Assert.Equal("Next track unchanged", second.Message);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackAdoptsPreexistingCrossSourceAlternate_WhenExactSourceRecentlySignaled()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot braveBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Baseline Stream A", "Baseline Stream A", null, "brave", 4028);
        MediaOverlaySessionSnapshot chromiumPreCommand = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Save a Prayer - 2009 Remaster", "Duran Duran", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 2);
        MediaOverlaySessionSnapshot chromiumChanged = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Divinity", "Porter Robinson", null, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", 0);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                if (string.Equals(preferredSource, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(chromiumChanged);
                }

                return Task.FromResult(braveBaseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = braveBaseline,
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = chromiumPreCommand,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, chromiumChanged }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(true, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4")));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Divinity", result.Title);
        Assert.Equal("Porter Robinson", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PreviousTrackKeepsBaselineSource_WhenAnotherAppRemainsCurrent()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot spotifyBaseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track B", "Spotify Artist", null, "spotify", 84);
        MediaOverlaySessionSnapshot spotifyPrevious = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Spotify Track A", "Spotify Artist", null, "spotify", 1);
        MediaOverlaySessionSnapshot browserCurrent = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Browser Audio", "Browser Source", null, "chrome", 120);

        int currentSnapshotCalls = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(spotifyBaseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(spotifyPrevious);
                }

                return Task.FromResult(browserCurrent);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = spotifyBaseline,
                ["chrome"] = browserCurrent,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { spotifyPrevious, browserCurrent }));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PreviousTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Spotify Track A", result.Title);
        Assert.Equal("Spotify Artist", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PreviousTrackFallsBackToPlainMessage_WhenMetadataNeverChanges()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot baseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "spotify", 84);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(baseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { baseline }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(MediaOverlayCommand.PreviousTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Overlay.Kind);
        Assert.Equal("Previous track unchanged", result.Overlay.Message);
        Assert.Equal("media-overlay-track-unchanged", result.DiagCode);
        Assert.NotNull(result.TrackNavigationDiagnostics);
        Assert.Equal("unchanged", result.TrackNavigationDiagnostics.Value.Outcome);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PreviousTrackShowsMetadata_WhenSameTrackRestarts()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            7);
        MediaOverlaySessionSnapshot restarted = baseline with { PositionSeconds = 0 };

        int currentSnapshotCalls = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(baseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(restarted);
                }

                return Task.FromResult(baseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { restarted }));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PreviousTrack, () => { commandSent = true; return true; });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Previous track", result.Header);
        Assert.Equal("Track A", result.Title);
        Assert.Equal("Artist A", result.Artist);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PreviousTrackRecordsSameTrackRestartDiagnostics_WhenSameTrackRestarts()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            7);
        MediaOverlaySessionSnapshot restarted = baseline with { PositionSeconds = 0 };
        int currentSnapshotCalls = 0;
        var adapter = new MediaOverlayEngineTestAdapter(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(baseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(restarted);
                }

                return Task.FromResult(baseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { restarted }));

        MediaOverlayEngineTestAdapterResult result = await adapter.SendWithBestEffortOverlayAsync(MediaOverlayCommand.PreviousTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Result.Kind);
        Assert.NotNull(result.TrackNavigationDiagnostics);
        Assert.Equal("same-track-restart", result.TrackNavigationDiagnostics.Value.FinalChangeKind);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackDoesNotTreatSameTrackResetAsChanged()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            4);
        MediaOverlaySessionSnapshot resetSameTrack = baseline with { PositionSeconds = 0 };
        int currentSnapshotCalls = 0;
        var adapter = new MediaOverlayEngineTestAdapter(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(baseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(resetSameTrack);
                }

                return Task.FromResult(baseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = resetSameTrack,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { resetSameTrack }),
            timingProfile: MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                unchangedRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 1,
                sessionDropRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                graceRecoveryAttempts: 1));

        MediaOverlayEngineTestAdapterResult result = await adapter.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Result.Kind);
        Assert.Equal("Next track unchanged", result.Result.Message);
        Assert.NotNull(result.TrackNavigationDiagnostics);
        Assert.Equal("unchanged", result.TrackNavigationDiagnostics.Value.Outcome);
        Assert.Equal("track-changed", result.TrackNavigationDiagnostics.Value.FinalChangeKind);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_ReturnsFailureMessage_WhenCommandSendFails()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return false;
            });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Kind);
        Assert.Equal("Next track failed", result.Message);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_ReturnsFailureDiagCode_WhenCommandSendFails()
    {
        bool commandSent = false;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return false;
            });

        Assert.True(commandSent);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Overlay.Kind);
        Assert.Equal("media-command-send-failed", result.DiagCode);
        Assert.Null(result.TrackNavigationDiagnostics);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackDoesNotFallThroughToFinalFallback_WhenSameSourceWinnerAppearsAfterConflict()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "did i tell u that i miss u (Instrumental)",
            "lucii. - Topic",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot pausedSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "GPU News",
            "Daniel Owen",
            null,
            "brave",
            864);
        MediaOverlaySessionSnapshot winner = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "jenny - slowed to perfection",
            "breezyves",
            null,
            "brave",
            0);

        MediaOverlayEngineTestAdapter adapter = MediaOverlayTestHarness.CreateReplayAdapter(
            new MediaOverlayTestHarness.ReplayScenario(
            [
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
                new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = baseline,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = pausedSibling,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = pausedSibling,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = winner,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = winner,
                }),
            ],
            MediaOverlayResultKind.TrackMessage,
            ExpectedTitle: winner.Title,
            ExpectedArtist: winner.Artist));

        MediaOverlayEngineTestAdapterResult adapterResult = await adapter.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.TrackMessage, adapterResult.Result.Kind);
        Assert.Equal("jenny - slowed to perfection", adapterResult.Result.Title);
        Assert.NotNull(adapterResult.TrackNavigationDiagnostics);
        Assert.Equal("changed", adapterResult.TrackNavigationDiagnostics.Value.Outcome);
        Assert.NotEqual("final-fallback", adapterResult.TrackNavigationDiagnostics.Value.FinalPhase);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackReturnsLoading_WhenOnlyBlockedSameSourceRivalsRemainAfterSessionDrop()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "SUICIDAL-IDOL - ecstacy (slowed)",
            "EUPHXRIA",
            null,
            "brave",
            23);
        MediaOverlaySessionSnapshot wrongSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "[459/730] stream",
            "Baseline Stream A",
            null,
            "brave",
            516);

        MediaOverlayEngineTestAdapter adapter = MediaOverlayTestHarness.CreateReplayAdapter(
            new MediaOverlayTestHarness.ReplayScenario(
            [
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
                new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = baseline,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
                new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave")),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = wrongSibling,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = MediaOverlaySessionSnapshot.Empty,
                }),
                new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave")),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = wrongSibling,
                }),
            ],
            MediaOverlayResultKind.PlainMessage,
            ExpectedMessage: "Next track loading"));

        MediaOverlayEngineDetailedTestAdapterResult adapterResult = await adapter.SendWithDetailedResultAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.PlainMessage, adapterResult.Result.Overlay.Kind);
        Assert.Equal("Next track loading", adapterResult.Result.Overlay.Message);
        Assert.Equal("media-overlay-track-loading", adapterResult.Result.DiagCode);
        Assert.NotNull(adapterResult.Result.TrackNavigationDiagnostics);
        Assert.Equal("loading", adapterResult.Result.TrackNavigationDiagnostics.Value.Outcome);
        Assert.Equal("loading", adapterResult.Result.TrackNavigationDiagnostics.Value.FinalFallbackClassification);
        Assert.True(adapterResult.Result.TrackNavigationDiagnostics.Value.SawSessionDrop);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_NextTrackDoesNotTreatSameTrackStatusOnlyChangeAsObservedTransition()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Cult Member - three",
            "SYNE",
            null,
            "brave",
            1);
        MediaOverlaySessionSnapshot sameTrackUnknownStatus = new(
            null,
            "Cult Member - three",
            "SYNE",
            null,
            "brave",
            1);

        MediaOverlayEngineTestAdapter adapter = MediaOverlayTestHarness.CreateReplayAdapter(
            new MediaOverlayTestHarness.ReplayScenario(
            [
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
                new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = baseline,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = sameTrackUnknownStatus,
                }),
                new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = sameTrackUnknownStatus,
                }),
            ],
            MediaOverlayResultKind.PlainMessage,
            ExpectedMessage: "Next track loading"));

        MediaOverlayEngineTestAdapterResult adapterResult = await adapter.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.PlainMessage, adapterResult.Result.Kind);
        Assert.Equal("Next track loading", adapterResult.Result.Message);
        Assert.NotNull(adapterResult.TrackNavigationDiagnostics);
        Assert.Equal("loading", adapterResult.TrackNavigationDiagnostics.Value.Outcome);
        Assert.NotEqual("changed", adapterResult.TrackNavigationDiagnostics.Value.Outcome);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_CanceledCommandClearsBrowserSameSourceState_BeforeNextCommand()
    {
        MediaOverlaySessionSnapshot conflictedBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot farSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Wrong Stream",
            "Streamer",
            null,
            "brave",
            512);
        MediaOverlaySessionSnapshot ambiguousSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Maybe Next Track",
            "Artist B",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot steadyBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track Stable",
            "Artist Stable",
            null,
            "brave",
            0);

        int phase = 0;
        int firstCommandPreferredCalls = 0;
        var firstCommandReadyToBeSuperseded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCommand = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaOverlayTrackNavigationDiagnostics? latestDiagnostics = null;

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                if (Volatile.Read(ref phase) == 0)
                {
                    if (string.IsNullOrWhiteSpace(preferredSource))
                    {
                        return Task.FromResult(conflictedBaseline);
                    }

                    int preferredCall = Interlocked.Increment(ref firstCommandPreferredCalls);
                    return preferredCall switch
                    {
                        1 => Task.FromResult(farSibling),
                        2 => Task.FromResult(ambiguousSibling),
                        _ => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
                    };
                }

                if (string.IsNullOrWhiteSpace(preferredSource))
                {
                    return Task.FromResult(steadyBaseline);
                }

                return Task.FromResult(steadyBaseline);
            },
            snapshotsBySourceOverride: (_, _) =>
            {
                MediaOverlaySessionSnapshot baseline = Volatile.Read(ref phase) == 0
                    ? conflictedBaseline
                    : steadyBaseline;
                return Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                {
                    ["brave"] = baseline,
                });
            },
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            eventWaitOverride: async (_, _, commandSequence, cancellationToken) =>
            {
                if (commandSequence == 1 && firstCommandPreferredCalls >= 2)
                {
                    firstCommandReadyToBeSuperseded.TrySetResult(true);
                    await releaseFirstCommand.Task.WaitAsync(cancellationToken);
                }

                return new MediaEventAssistOutcome(false, null);
            },
            trackNavigationDiagnosticsSink: diagnostics => latestDiagnostics = diagnostics);

        Task<MediaOverlayResult> firstCommand = engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        await firstCommandReadyToBeSuperseded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Volatile.Write(ref phase, 1);
        MediaOverlayResult secondResult = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);
        MediaOverlayTrackNavigationDiagnostics? secondDiagnostics = latestDiagnostics;

        releaseFirstCommand.TrySetResult(true);
        MediaOverlayResult canceledFirstResult = await firstCommand;

        Assert.False(canceledFirstResult.ShowOverlay);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, secondResult.Kind);
        Assert.Equal("Next track unchanged", secondResult.Message);
        Assert.NotNull(secondDiagnostics);
        Assert.False(secondDiagnostics.Value.SameSourceConflictObserved);
        Assert.False(secondDiagnostics.Value.SameSourceConflictActive);
        Assert.Equal(0, engine.BrowserSameSourceCommandStateCountForTests);
    }
}
