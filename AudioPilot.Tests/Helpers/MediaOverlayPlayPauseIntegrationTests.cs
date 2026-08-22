using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayPlayPauseIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithDetailedResultAsync_NewCommandTarget_DoesNotReuseAnotherPlayersBaseline(bool targetPlaying)
    {
        bool sent = false;
        var other = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Other track", null, null, "Spotify.exe", 12);
        var target = new MediaOverlaySessionSnapshot(
            targetPlaying ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Target track", null, null, "PortableBrowser", 0);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (source, _, _) =>
            {
                if (!sent) { return Task.FromResult(other); }
                Assert.Equal(target.SourceAppUserModelId, source);
                return Task.FromResult(target);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(sent
                ? new Dictionary<string, MediaOverlaySessionSnapshot>
                {
                    [other.SourceAppUserModelId!] = other with { PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused },
                    [target.SourceAppUserModelId!] = target,
                }
                : new Dictionary<string, MediaOverlaySessionSnapshot> { [other.SourceAppUserModelId!] = other }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.PlayPause,
            () => { Assert.False(sent); sent = true; return Task.FromResult(true); },
            () => target.SourceAppUserModelId,
            TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.Equal("Play/pause command sent", result.Overlay.Header);
        Assert.Equal("Target track", result.Overlay.Title);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 280)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, 0)]
    [InlineData(true, 280)]
    [InlineData(true, int.MaxValue)]
    public async Task SendWithDetailedResultAsync_PlayPauseUsesCommandTarget_WhenAnotherPlayerChanges(
        bool wasPlaying, int transitionAtMs)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        DateTimeOffset now = start;
        int commandsSent = 0;
        var target = new MediaOverlaySessionSnapshot(
            wasPlaying ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Target track", "Target artist", null, wasPlaying ? "Spotify.exe" : "Chromium.Profile", 12);
        var other = target with
        {
            SourceAppUserModelId = wasPlaying ? "Chromium.Profile" : "Spotify.exe",
            Title = "Other track",
            PlaybackStatus = wasPlaying ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
        };
        MediaOverlaySessionSnapshot CurrentTarget() => commandsSent > 0 && (now - start).TotalMilliseconds >= transitionAtMs
            ? target with { PlaybackStatus = other.PlaybackStatus }
            : target;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (source, _, _) =>
            {
                if (commandsSent == 0) { return Task.FromResult(other); }
                Assert.Equal(target.SourceAppUserModelId, source);
                return Task.FromResult(CurrentTarget());
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [target.SourceAppUserModelId!] = CurrentTarget(),
                [other.SourceAppUserModelId!] = commandsSent > 0 ? other with { PlaybackStatus = target.PlaybackStatus } : other,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            eventWaitOverride: (source, delayMs, _, _) =>
            {
                Assert.Equal(target.SourceAppUserModelId, source);
                now = now.AddMilliseconds(delayMs);
                return Task.FromResult(new MediaEventAssistOutcome(false, null));
            },
            utcNow: () => now);

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.PlayPause,
            () => { commandsSent++; return Task.FromResult(true); },
            () => { Assert.Equal(1, commandsSent); return target.SourceAppUserModelId; },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, commandsSent);
        if (transitionAtMs == int.MaxValue)
        {
            Assert.Equal("Play/pause command sent", result.Overlay.Message);
            Assert.Equal("unconfirmed", result.PlayPauseDiagnostics?.Outcome);
        }
        else
        {
            Assert.Equal(wasPlaying ? "Playback paused" : "Playback resumed", result.Overlay.Header);
            Assert.Equal(target.Title, result.Overlay.Title);
            Assert.Equal("changed", result.PlayPauseDiagnostics?.Outcome);
            Assert.True((now - start).TotalMilliseconds >= transitionAtMs);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("UnknownPlayer", false)]
    [InlineData(null, true)]
    public async Task SendWithDetailedResultAsync_PlayPauseFallsBack_WhenCommandTargetIsUnavailable(string? reportedTarget, bool providerThrows)
    {
        int commandsSent = 0;
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Track", null, null, "browser", 0);
        MediaOverlaySessionSnapshot Current() => commandsSent == 0
            ? baseline
            : baseline with { PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing };
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(Current()),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>
            {
                [baseline.SourceAppUserModelId!] = Current(),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.PlayPause,
            () => { commandsSent++; return Task.FromResult(true); },
            () => providerThrows ? throw new InvalidOperationException("Target unavailable") : reportedTarget,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, commandsSent);
        Assert.Equal("Playback resumed", result.Overlay.Header);
        Assert.Equal("Track", result.Overlay.Title);
    }

    [Theory]
    [InlineData(2, 9000, true)]
    [InlineData(100, 9000, true)]
    [InlineData(300, 9000, true)]
    [InlineData(500, 9000, true)]
    [InlineData(840, 9000, true)]
    [InlineData(400, 400, true)]
    [InlineData(500, 400, false)]
    [InlineData(int.MaxValue, 9000, false)]
    public async Task ResolveSnapshotAsync_EventBurstPreservesBoundedWindow_ForDelayedPlaybackMetadata(
        int transitionAtMs, int budgetMs, bool expectedConfirmed)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        DateTimeOffset now = start;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Live ⛽", "Artist", null, "browser-source", 0);
        var resolver = new MediaOverlayPlayPauseResolver(
            (delayMs, _, deadline, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                bool withinBudget = now.AddMilliseconds(delayMs) <= deadline;
                if (withinBudget)
                {
                    now = now.AddMilliseconds(Math.Min(2, delayMs));
                }

                return Task.FromResult(new MediaOverlayDelayAssistResult(withinBudget, delayMs > 0));
            },
            (_, _, _, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult((now - start).TotalMilliseconds >= transitionAtMs
                    ? baseline with { PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing }
                    : baseline);
            },
            MediaOverlayTimingProfile.Default,
            () => now,
            (delayMs, token) =>
            {
                token.ThrowIfCancellationRequested();
                now = now.AddMilliseconds(delayMs);
                return Task.CompletedTask;
            });

        MediaOverlayPlayPauseResolutionResult resolution = await resolver.ResolveSnapshotAsync(
            baseline, new Dictionary<string, MediaOverlaySessionSnapshot> { [baseline.SourceAppUserModelId!] = baseline },
            null, baseline.SourceAppUserModelId, 1, start.AddMilliseconds(budgetMs), TestContext.Current.CancellationToken);
        MediaOverlayResult overlay = MediaOverlayMessageFormatter.BuildPlayPauseMessage(
            resolution.Resolution.Snapshot, resolution.Resolution.Baseline);

        Assert.Equal(expectedConfirmed ? "changed" : "unconfirmed", resolution.Diagnostics.Outcome);
        Assert.True(resolution.Diagnostics.UsedEventAssist);
        Assert.InRange((now - start).TotalMilliseconds, 0, Math.Min(840, budgetMs));
        if (expectedConfirmed)
        {
            Assert.InRange((now - start).TotalMilliseconds, transitionAtMs,
                Math.Min(Math.Min(840, budgetMs), transitionAtMs + MediaOverlayTimingProfile.Default.PlayPauseSettleRetryDelayMs));
            Assert.Equal("Playback resumed", overlay.Header);
            Assert.Equal("Live ⛽", overlay.Title);
        }
        else
        {
            Assert.Equal("Play/pause command sent", overlay.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithDetailedResultAsync_UnchangedPlaybackDoesNotClaimTheCommandSucceeded(bool wasPlaying)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int commandsSent = 0;
        MediaOverlaySessionSnapshot baseline = new(
            wasPlaying ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Live ⛽", "Artist", null, "browser-source", 0);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(baseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>
            {
                [baseline.SourceAppUserModelId!] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { baseline }),
            eventWaitOverride: (_, delayMs, _, _) =>
            {
                now = now.AddMilliseconds(delayMs);
                return Task.FromResult(new MediaEventAssistOutcome(false, null));
            },
            utcNow: () => now);

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(
            MediaOverlayCommand.PlayPause, () => { commandsSent++; return true; });

        Assert.Equal(1, commandsSent);
        Assert.Equal(MediaOverlayResultKind.PlainMessage, result.Overlay.Kind);
        Assert.Equal("Play/pause command sent", result.Overlay.Message);
        Assert.Equal("unconfirmed", result.PlayPauseDiagnostics?.Outcome);
        Assert.Equal("media-overlay-play-pause-fallback", result.DiagCode);
    }

    [Fact]
    public async Task ResolveSnapshotAsync_CancellationDuringFinalWaitDoesNotSampleAgain()
    {
        using var cancellation = new CancellationTokenSource();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool finalWaitStarted = false;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Live", null, null, "browser-source", 0);
        var resolver = new MediaOverlayPlayPauseResolver(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(true, true)),
            (_, _, _, _, _) =>
            {
                Assert.False(finalWaitStarted);
                return Task.FromResult(baseline);
            },
            MediaOverlayTimingProfile.Default,
            () => now,
            (_, token) =>
            {
                finalWaitStarted = true;
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveSnapshotAsync(
            baseline, new Dictionary<string, MediaOverlaySessionSnapshot>(), null, baseline.SourceAppUserModelId,
            1, now.AddSeconds(9), cancellation.Token));
        Assert.True(finalWaitStarted);
    }

    [Fact]
    public async Task ResolveSnapshotAsync_EventBurstRemainsBoundedWhenClockDoesNotAdvance()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int captures = 0;
        var delays = new List<int>();
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Track", null, null, "browser", 0);
        MediaOverlayTimingProfile timing = MediaOverlayTimingProfile.Default with
        {
            PlayPauseSettleAttempts = 3,
            PlayPauseSettleInitialDelayMs = 50,
            PlayPauseSettleRetryDelayMs = 50,
        };
        var resolver = new MediaOverlayPlayPauseResolver(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(true, true)),
            (_, _, _, _, _) => { captures++; return Task.FromResult(baseline); },
            timing,
            () => now,
            (delayMs, _) => { delays.Add(delayMs); return Task.CompletedTask; });

        MediaOverlayPlayPauseResolutionResult result = await resolver.ResolveSnapshotAsync(
            baseline, new Dictionary<string, MediaOverlaySessionSnapshot>(), null, "browser", 1,
            now.AddSeconds(9), TestContext.Current.CancellationToken);

        Assert.Equal("unconfirmed", result.Diagnostics.Outcome);
        Assert.Equal([50, 50, 50], delays);
        Assert.Equal(6, captures);
    }

    [Fact]
    public async Task SendWithBestEffortOverlayAsync_PlayPauseUnavailable_WhenNoSessionContextExists()
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
        Assert.True(result.IsPlainMessage);
        Assert.Equal("No media session detected", result.Message);
    }

    [Fact]
    public async Task SendWithDetailedResultAsync_PlayPauseUnavailable_IncludesNoSessionDiagnostics()
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
        Assert.True(result.Overlay.IsPlainMessage);
        Assert.Equal("No media session detected", result.Overlay.Message);
        Assert.Equal("media-overlay-no-session", result.DiagCode);
        Assert.NotNull(result.PlayPauseDiagnostics);
        Assert.Equal("no-session-context", result.PlayPauseDiagnostics.Value.FinalPath);
        Assert.Equal("no-session", result.PlayPauseDiagnostics.Value.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithDetailedResultAsync_WithoutCurrentSession_StillChecksKnownSessions(bool playbackChanges)
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Fixture title", "Fixture artist", null, "browser", 15);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (source, _, _) => Task.FromResult(
                source == "browser" && playbackChanges
                    ? baseline with { PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing }
                    : MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>
            {
                ["browser"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { baseline }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        MediaOverlayCommandResult result = await engine.SendWithDetailedResultAsync(MediaOverlayCommand.PlayPause, () => true);

        Assert.Equal(playbackChanges ? "media-overlay-play-pause-resolved" : "media-overlay-play-pause-fallback", result.DiagCode);
        Assert.Equal(playbackChanges ? "changed" : "unconfirmed", result.PlayPauseDiagnostics?.Outcome);
        if (playbackChanges)
        {
            Assert.Equal("Playback resumed", result.Overlay.Header);
            Assert.Equal("Fixture title", result.Overlay.Title);
        }
        else
        {
            Assert.Equal("Play/pause command sent", result.Overlay.Message);
        }
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
