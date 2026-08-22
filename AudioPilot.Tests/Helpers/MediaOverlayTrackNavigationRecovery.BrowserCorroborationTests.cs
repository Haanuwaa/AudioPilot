using AudioPilot.Constants;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed partial class MediaOverlayTrackNavigationRecoveryTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CaptureSnapshotWithRetryAsync_ConfirmsBrowserCandidateOnFinalSample_WithinBudgetAndRetryLimit(
        bool confirmationFitsBudget, bool candidateSettles)
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "La la la", "Naughty Boy", null, "Chromium.Portable", 0);
        MediaOverlaySessionSnapshot changed = baseline with { Title = "Night Changes", Artist = "One Direction" };
        int captures = 0;
        List<int> delays = [];
        List<MediaOverlayTrackNavigationDiagnostics> diagnostics = [];
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                delays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(captures < 4 || confirmationFitsBudget, ObservedEvent: true));
            },
            (_, _, _, _, _) => Task.FromResult(++captures < 4 ? baseline
                : captures > 4 && candidateSettles ? changed : MediaOverlaySessionSnapshot.Empty),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => default,
            (_, _, _) => { },
            _ => true,
            _ => false,
            (_, _) => captures >= 4 ? new(DistinctCandidateCount: 1, HasPendingCandidateEvidence: true) : default,
            snapshot => snapshot == changed,
            diagnostics.Add,
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1, unchangedRecoveryAttempts: 1, stagnantTrackRecoveryAttempts: 1, trackLoadRecoveryAttempts: 1));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack, baseline, baseline, baseline.SourceAppUserModelId,
            new() { [baseline.SourceAppUserModelId!] = baseline }, false, 15,
            DateTimeOffset.UtcNow.AddSeconds(10), TestContext.Current.CancellationToken);

        bool confirmed = confirmationFitsBudget && candidateSettles;
        Assert.Equal(confirmed ? TrackNavigationRecoveryOutcome.Changed : TrackNavigationRecoveryOutcome.Loading, result.Outcome);
        Assert.Equal(confirmationFitsBudget ? 5 : 4, captures);
        Assert.Equal(AppConstants.MediaOverlay.BrowserSingleSourceLatePendingCorroborationRetryDelayMs, delays[^1]);
        MediaOverlayTrackNavigationDiagnostics diagnostic = Assert.Single(diagnostics);
        Assert.Equal(confirmed ? "track-changed" : "none", diagnostic.FinalChangeKind);
        Assert.Equal(confirmed ? "late-track-load-recovery" : "final-fallback", diagnostic.FinalPhase);
        if (confirmed)
        {
            Assert.Equal("Night Changes", result.Snapshot.Title);
        }
        else
        {
            Assert.Equal("metadata-pending", diagnostic.FinalFallbackClassification);
        }
    }

    [Fact]

    public async Task CaptureSnapshotWithRetryAsync_UsesExpeditedLateBrowserCorroborationRetry_AfterPendingUnchangedProbe()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            2);
        MediaOverlaySessionSnapshot sameTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);
        Queue<MediaOverlaySessionSnapshot> snapshots = new(
        [
            sameTrack,
            MediaOverlaySessionSnapshot.Empty,
            changed,
        ]);
        bool sawPendingBrowserCandidate = false;
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) =>
            {
                MediaOverlaySessionSnapshot snapshot = snapshots.Count > 0 ? snapshots.Dequeue() : changed;
                if (MediaOverlayEngine.IsSessionMissing(snapshot))
                {
                    sawPendingBrowserCandidate = true;
                }

                return Task.FromResult(snapshot);
            },
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => sawPendingBrowserCandidate
                ? new BrowserSameSourceCommandSummary(
                    DistinctCandidateCount: 1,
                    HasPendingCandidateEvidence: true)
                : default,
            snapshot => string.Equals(snapshot.Title, changed.Title, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                unchangedRecoveryAttempts: 2));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Track B", result.Snapshot.Title);
        Assert.Equal(
            [
                AppConstants.MediaOverlay.BrowserSingleSourceInitialSettleDelayMs,
                AppConstants.MediaOverlay.BrowserSingleSourceUnchangedRecoveryInitialDelayMs,
                AppConstants.MediaOverlay.BrowserSingleSourceLatePendingCorroborationRetryDelayMs,
            ],
            observedDelays);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_CompletesImmediately_ForLedgerConfirmedBrowserWinner()
    {
        List<int> observedDelays = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            2);
        MediaOverlaySessionSnapshot sameTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            "Album B",
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);
        string sourceAppId = changed.SourceAppUserModelId!;
        Queue<MediaOverlaySessionSnapshot> snapshots = new(
        [
            sameTrack,
            changed,
            MediaOverlaySessionSnapshot.Empty,
        ]);
        string winningTrackFingerprint = MediaOverlayBrowserSameSourcePolicy.BuildPendingCandidateTrackFingerprint(changed);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (delayMs, _, _, _, _) =>
            {
                observedDelays.Add(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false));
            },
            (_, _, _, _, _) => Task.FromResult(snapshots.Count > 0 ? snapshots.Dequeue() : MediaOverlaySessionSnapshot.Empty),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (source, _) => string.Equals(source, sourceAppId, StringComparison.OrdinalIgnoreCase)
                ? new BrowserSameSourceCommandSummary(
                    WinnerElection: new BrowserSameSourceWinnerElectionResult(
                        HasWinner: true,
                        WinnerIsCurrentCandidate: true,
                        WinningTrackFingerprint: winningTrackFingerprint,
                        WinningReasonClass: BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                        PromotionKind: BrowserSameSourcePromotionKind.StableRepetition,
                        ActiveRivalCount: 0,
                        ReinforcedRivalCount: 0,
                        StaleRivalCount: 0,
                        RivalReasonClasses: "<none>",
                        StaleRivalIgnored: false))
                : default,
            snapshot => string.Equals(snapshot.SourceAppUserModelId, sourceAppId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(MediaOverlayBrowserSameSourcePolicy.BuildPendingCandidateTrackFingerprint(snapshot), winningTrackFingerprint, StringComparison.Ordinal),
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 3,
                retryDelayMs: 25));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: sourceAppId,
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [sourceAppId] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Track B", result.Snapshot.Title);
        Assert.Equal("Artist B", result.Snapshot.Artist);
        Assert.Equal(
            [
                AppConstants.MediaOverlay.BrowserSingleSourceInitialSettleDelayMs,
                25,
            ],
            observedDelays);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_CompletesEarly_AfterCurrentSessionChangedToCorroboratedSource()
    {
        List<string?> capturedSources = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            240);
        MediaOverlaySessionSnapshot switchedCurrentSession = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: true,
                new MediaEventAssistOutcome(true, "spotify", MediaEventAssistKind.CurrentSessionChanged))),
            (preferredSourceAppUserModelId, _, _, _, _) =>
            {
                capturedSources.Add(preferredSourceAppUserModelId);
                return Task.FromResult(string.IsNullOrWhiteSpace(preferredSourceAppUserModelId)
                    ? switchedCurrentSession
                    : baseline);
            },
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
                ["spotify"] = switchedCurrentSession with { Title = "Track Previous", PositionSeconds = 200 },
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Track B", result.Snapshot.Title);
        Assert.Equal(new string?[] { "brave", null }, capturedSources);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_CompletesEarly_AfterSameSourceTimelineResetSignal()
    {
        int captureCalls = 0;
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            91);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track B",
            "Artist B",
            null,
            "spotify",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: true,
                new MediaEventAssistOutcome(true, "spotify", MediaEventAssistKind.TimelinePropertiesChanged))),
            (_, _, _, _, _) =>
            {
                captureCalls++;
                return Task.FromResult(changed);
            },
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "spotify",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Track B", result.Snapshot.Title);
        Assert.Equal(1, captureCalls);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_DoesNotShortCircuitChanged_ForBrowserSignal_WhenPreferredSourceStaysUnresolved()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: true,
                new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.TimelinePropertiesChanged))),
            (_, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                sessionDropRecoveryAttempts: 1,
                unchangedRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                graceRecoveryAttempts: 1));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(TrackNavigationRecoveryOutcome.Loading, result.Outcome);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_RecoversCommittedBrowserFarPositionCandidate_AfterSessionDrop()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Tambourine Lasagna",
            "Party In Backyard",
            null,
            "brave",
            336);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Track Alpha",
            "SyntheticChannelAlpha",
            null,
            "brave",
            343);
        Queue<MediaOverlaySessionSnapshot> snapshots = new(
        [
            MediaOverlaySessionSnapshot.Empty,
            changed,
        ]);

        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: false)),
            (_, _, _, _, _) => Task.FromResult(snapshots.Count > 0 ? snapshots.Dequeue() : changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            snapshot => snapshot.Title == changed.Title,
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                extraAttemptsAfterSessionDrop: 0,
                sessionDropRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 1,
                unchangedRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                graceRecoveryAttempts: 1));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Synthetic Track Alpha", result.Snapshot.Title);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_DoesNotAcceptFarPositionBrowserTimelineSignal_AsImmediateWinner()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track Epsilon",
            "SyntheticChannelTheta",
            null,
            "brave",
            357);
        MediaOverlaySessionSnapshot wrongSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            59);

        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: true,
                new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.TimelinePropertiesChanged))),
            (_, _, _, _, _) => Task.FromResult(wrongSibling),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                sessionDropRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 1,
                unchangedRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                graceRecoveryAttempts: 1));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_DoesNotAcceptNearStartBrowserTimelineSignal_AsImmediateWinner()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track",
            "SyntheticBaselineArtist",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot wrongSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            2);

        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: true,
                new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.MediaPropertiesChanged))),
            (_, _, _, _, _) => Task.FromResult(wrongSibling),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                sessionDropRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 1,
                unchangedRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                graceRecoveryAttempts: 1));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "brave",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_AcceptsNearStartBrowserMediaPropertiesSignal_WhenSourceIsCommandTarget()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track",
            "SyntheticBaselineArtist",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            393);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Resolved Song",
            "Resolved Artist",
            null,
            "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            0);

        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: true,
                new MediaEventAssistOutcome(true, "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", MediaEventAssistKind.MediaPropertiesChanged))),
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                sessionDropRecoveryAttempts: 1,
                sessionDropTrackLoadRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 1,
                unchangedRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                graceRecoveryAttempts: 1));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            preferredSourceIsCommandTarget: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Resolved Song", result.Snapshot.Title);
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_BrowserWaitsPastRepeatedOldMetadata_ForLateYouTubeTrack()
    {
        var baseline = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Perfect", "Ed Sheeran", null, "Chromium.Portable", 0);
        MediaOverlaySessionSnapshot changed = baseline with { Title = "Sunflower", Artist = "Post Malone" };
        List<MediaOverlayTrackNavigationDiagnostics> diagnostics = [];
        int captures = 0;
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(true, false)),
            (_, _, _, _, _) => Task.FromResult(++captures <= 4 ? baseline : changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            snapshot => snapshot == changed,
            diagnostics.Add,
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                unchangedRecoveryAttempts: 2,
                stagnantTrackRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 2));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "Chromium.Portable",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot> { ["Chromium.Portable"] = baseline },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal("Sunflower", result.Snapshot.Title);
        Assert.Contains(diagnostics, entry => entry.FinalPhase == "late-track-load-recovery" && entry.Outcome == "changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureSnapshotWithRetryAsync_ReportsUnchangedRecovery_ForNonBrowserBaseline(bool trackChanges)
    {
        List<MediaOverlayTrackNavigationDiagnostics> diagnostics = [];
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            10);
        Queue<MediaOverlaySessionSnapshot> snapshots = new(
        [
            baseline with { PositionSeconds = 11 },
            baseline with { Title = trackChanges ? "Track B" : baseline.Title, PositionSeconds = 11 },
        ]);

        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(
                CompletedWithinBudget: true,
                ObservedEvent: false)),
            (_, _, _, _, _) => Task.FromResult(snapshots.Count > 0 ? snapshots.Dequeue() : baseline with { PositionSeconds = 11 }),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            diagnostics.Add,
            MediaOverlayTestHarness.CreateTimingProfile(
                maxAttempts: 1,
                unchangedRecoveryAttempts: 2,
                graceRecoveryAttempts: 1,
                stagnantTrackRecoveryAttempts: 1,
                trackLoadRecoveryAttempts: 3));

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            baseline,
            baseline,
            preferredSourceForCommand: "spotify",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(trackChanges ? TrackNavigationRecoveryOutcome.Changed : TrackNavigationRecoveryOutcome.Unchanged, result.Outcome);
        Assert.Equal(trackChanges ? "Track B" : "Track A", result.Snapshot.Title);
        Assert.Equal(trackChanges ? "track-changed" : "none", Assert.Single(diagnostics).FinalChangeKind);
        Assert.Contains(diagnostics, entry => entry.FinalPhase == "unchanged-track-recovery"
            && entry.Outcome == (trackChanges ? "changed" : "unchanged"));
    }

    [Fact]
    public async Task CaptureSnapshotWithRetryAsync_ClassifiesSourceSwitched_WhenEffectiveBaselineWasRebased()
    {
        MediaOverlaySessionSnapshot originalBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Browser Track",
            "Browser Artist",
            null,
            "brave",
            240);
        MediaOverlaySessionSnapshot effectiveBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track A",
            "Spotify Artist",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot changed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track B",
            "Spotify Artist",
            null,
            "spotify",
            0);
        MediaOverlayTrackNavigationRecoveryCoordinator coordinator = new(
            (_, _, _, _, _) => Task.FromResult(new MediaOverlayDelayAssistResult(CompletedWithinBudget: true, ObservedEvent: false)),
            (_, _, _, _, _) => Task.FromResult(changed),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            _ => { },
            MediaOverlayTimingProfile.Default);

        SnapshotCaptureResult result = await coordinator.CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand.NextTrack,
            originalBaseline,
            effectiveBaseline,
            preferredSourceForCommand: "spotify",
            preCommandSnapshots: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = originalBaseline,
                ["spotify"] = effectiveBaseline,
            },
            isInGraceWindow: false,
            commandSequence: 1,
            deadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.Equal(TrackNavigationRecoveryOutcome.Changed, result.Outcome);
        Assert.Equal(TrackNavigationChangeKind.SourceSwitched, result.ChangeKind);
    }

}

