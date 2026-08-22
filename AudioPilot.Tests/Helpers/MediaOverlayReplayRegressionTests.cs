using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayReplayRegressionTests
{
    [Fact]
    public async Task ReplayScenario_NextTrackReturnsUnchanged_WhenEvidenceNeverStrengthens()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "brave",
            3);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(SessionSnapshots: [baseline]),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track unchanged");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackWaitsThroughSessionDropAndSameTrackAtStart_UntilLateMetadataAppears()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "MERGULHAR NA TREVA",
            "Awake Kyoto - Topic",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot sameTrackAtStart = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "MERGULHAR NA TREVA",
            "Awake Kyoto - Topic",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot lateRealTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "SENDO SENDO",
            "Vlxdimir - Topic",
            null,
            "brave",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: sameTrackAtStart),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: sameTrackAtStart),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: sameTrackAtStart),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: lateRealTrack),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(SessionSnapshots: [lateRealTrack]),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "SENDO SENDO",
        ExpectedArtist: "Vlxdimir - Topic");

        MediaOverlayTimingProfile timingProfile = MediaOverlayTestHarness.CreateTimingProfile(
            maxAttempts: 2,
            extraAttemptsAfterSessionDrop: 1,
            sessionDropRecoveryAttempts: 2,
            sessionDropTrackLoadRecoveryAttempts: 3);

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario, timingProfile: timingProfile);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackReturnsTrack_WhenSessionDropsThenReappears()
    {
        bool commandSent = false;
        MediaOverlaySessionSnapshot baseline = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track A", "Artist A", null, "youtube", 42);
        MediaOverlaySessionSnapshot nextTrack = new(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Track B", "Artist B", null, "youtube", 1);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: nextTrack),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["youtube"] = baseline,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Track B",
        ExpectedArtist: "Artist B");

        var adapter = MediaOverlayTestHarness.CreateReplayAdapter(scenario);

        MediaOverlayEngineTestAdapterResult adapterResult = await adapter.SendWithBestEffortOverlayAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return true;
            });
        MediaOverlayResult result = adapterResult.Result;

        Assert.True(commandSent);
        Assert.Equal(scenario.ExpectedKind, result.Kind);
        Assert.Equal(scenario.ExpectedTitle, result.Title);
        Assert.Equal(scenario.ExpectedArtist, result.Artist);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackReturnsLoading_WhenBrowserEventAssistArrivesButPreferredSourceStaysUnresolved()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "AI DUA EM VE",
            "Release - Topic",
            null,
            "brave",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
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
                ["brave"] = MediaOverlaySessionSnapshot.Empty,
            }),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave")),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track loading");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackLoadingFallback_EmitsLoadingClassification()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "AI DUA EM VE",
            "Release - Topic",
            null,
            "brave",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
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
                ["brave"] = MediaOverlaySessionSnapshot.Empty,
            }),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave")),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track loading");

        MediaOverlayTimingProfile timingProfile = MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile(
            maxCaptureDurationMs: 100);
        MediaOverlayEngineTestAdapter adapter = MediaOverlayTestHarness.CreateReplayAdapter(scenario, timingProfile);
        MediaOverlayEngineTestAdapterResult adapterResult = await adapter.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal("Next track loading", adapterResult.Result.Message);
        Assert.NotNull(adapterResult.TrackNavigationDiagnostics);
        Assert.Equal("loading", adapterResult.TrackNavigationDiagnostics.Value.FinalFallbackClassification);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackAcceptsRepeatedAmbiguousNearStartBrowserCandidate_WhenFarPositionSiblingStaysBlocked()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "PUSHING",
            "valtis - Topic",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot wrongSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            392);
        MediaOverlaySessionSnapshot nextTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "bc she cares",
            "Bupin - Topic",
            null,
            "brave",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongSibling,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = nextTrack,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = nextTrack,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "bc she cares",
        ExpectedArtist: "Bupin - Topic");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackReturnsLoading_WhenBrowserSiblingTrackRepeatsAtFarPosition()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "THE COLOR VIOLET (chief remix)",
            "itsCHIEF",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot wrongSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            1175);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
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
                ["brave"] = wrongSibling with { PositionSeconds = 1176 },
            }),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track loading");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackReturnsLoading_WhenBrowserSessionDropsAndOnlyFarPositionSiblingRemains()
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

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
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
        ExpectedMessage: "Next track loading",
        ExpectedDiagnostics: new MediaOverlayTrackNavigationDiagnostics(
            FinalPhase: "final-fallback",
            Outcome: "loading",
            FinalChangeKind: "track-changed",
            SawSessionDrop: true,
            UsedSessionDropRecovery: true,
            UsedLateTrackLoadRecovery: true,
            UsedRecoveredAlternateSource: false,
            FinalFallbackClassification: "loading"));

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackRecoversSingleBrowserFarPositionCandidate_AfterSessionDrop()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Tambourine Lasagna",
            "Party In Backyard",
            null,
            "brave",
            336);
        MediaOverlaySessionSnapshot nextTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Track Alpha",
            "SyntheticChannelAlpha",
            null,
            "brave",
            343);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, null, MediaEventAssistKind.CurrentSessionChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = MediaOverlaySessionSnapshot.Empty,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = nextTrack,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = nextTrack with { PositionSeconds = 344 },
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Synthetic Track Alpha",
        ExpectedArtist: "SyntheticChannelAlpha");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackDoesNotAcceptFarPositionBrowserTimelineSibling_AsImmediateWinner()
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

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.TimelinePropertiesChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongSibling,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongSibling,
            }),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track loading");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackDoesNotAcceptNearStartBrowserTimelineSibling_AsImmediateWinner()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track",
            "SyntheticBaselineArtist",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot wrongSiblingAtStart = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            2);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.PlaybackInfoChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.MediaPropertiesChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongSiblingAtStart,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongSiblingAtStart with { PositionSeconds = 3 },
            }),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track loading");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackIgnoresWrongSameSourceStreamAtStart_WhenRealTrackAppearsLaterAtStart()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Jet Set Ratio",
            "Een Glish - Topic",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot wrongStream = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "PUNISHMENT STREAM",
            "Rosiiwun",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot realTrack = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "things you don't mean (Slowed)",
            "SHOVGEN - Topic",
            null,
            "brave",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongStream,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = realTrack,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = realTrack,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "things you don't mean (Slowed)",
        ExpectedArtist: "SHOVGEN - Topic");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackReturnsLoading_WhenWrongSameSourceStreamDriftsWithoutRealWinner()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "LUZ ROJA (Slowed)",
            "SyntheticArtistBeta - Topic",
            null,
            "brave",
            0);
        MediaOverlaySessionSnapshot wrongAtTwo = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "PUNISHMENT STREAM",
            "Rosiiwun",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot wrongAtSix = wrongAtTwo with { PositionSeconds = 6 };

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave")),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongAtTwo,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = wrongAtSix,
            }),
        ],
        MediaOverlayResultKind.PlainMessage,
        ExpectedMessage: "Next track loading",
        ExpectedDiagnostics: new MediaOverlayTrackNavigationDiagnostics(
            FinalPhase: "final-fallback",
            Outcome: "loading",
            FinalChangeKind: "track-changed",
            SawSessionDrop: true,
            UsedSessionDropRecovery: true,
            UsedLateTrackLoadRecovery: true,
            UsedRecoveredAlternateSource: false,
            FinalFallbackClassification: "loading",
            SameSourceConflictObserved: true));

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackLateWinnerShortCircuitsChanged_AfterPausedSiblingConflict()
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
        MediaOverlaySessionSnapshot lateWinner = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "jenny - slowed to perfection",
            "breezyves",
            null,
            "brave",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
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
                ["brave"] = lateWinner,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = lateWinner,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "jenny - slowed to perfection",
        ExpectedArtist: "breezyves",
        ExpectedDiagnostics: new MediaOverlayTrackNavigationDiagnostics(
            FinalPhase: "initial-preferred-source-sampling",
            Outcome: "changed",
            FinalChangeKind: "track-changed",
            SawSessionDrop: true,
            UsedSessionDropRecovery: false,
            UsedLateTrackLoadRecovery: false,
            UsedRecoveredAlternateSource: false,
            FinalFallbackClassification: "changed"));

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackPromotesCloseDeltaCandidate_AfterPausedSiblingRivalGoesStale()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track Gamma",
            "SyntheticChannelDelta",
            null,
            "brave",
            600);
        MediaOverlaySessionSnapshot pausedSibling = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Synthetic Paused Stream",
            "SyntheticPausedHost",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot candidateAtOldPosition = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Corrected Track",
            "SyntheticChannelEpsilon",
            null,
            "brave",
            393);
        MediaOverlaySessionSnapshot candidateAtCloseDelta = candidateAtOldPosition with { PositionSeconds = 601 };

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.PlaybackInfoChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.TimelinePropertiesChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = pausedSibling,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = candidateAtOldPosition,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = candidateAtCloseDelta,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = candidateAtCloseDelta,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Synthetic Corrected Track",
        ExpectedArtist: "SyntheticChannelEpsilon");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackPromotesCloseDeltaCandidate_AfterAmbiguousNearStartRivalGoesStale()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Baseline Track Delta",
            "SyntheticChannelZeta",
            null,
            "brave",
            667);
        MediaOverlaySessionSnapshot ambiguousNearStartRival = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Rival Stream",
            "SyntheticStreamHost",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot candidateAtRivalPosition = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Synthetic Corrected Track Delta",
            "SyntheticChannelEta",
            null,
            "brave",
            2);
        MediaOverlaySessionSnapshot candidateAtCloseDelta = candidateAtRivalPosition with { PositionSeconds = 673 };

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.PlaybackInfoChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "brave", MediaEventAssistKind.MediaPropertiesChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = ambiguousNearStartRival,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: MediaOverlaySessionSnapshot.Empty),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = candidateAtRivalPosition,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = candidateAtCloseDelta,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = candidateAtCloseDelta,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Synthetic Corrected Track Delta",
        ExpectedArtist: "SyntheticChannelEta");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackPromotesCorrectedFarPositionCandidate_WhenItSnapsBackToStart()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "LUZ ROJA (Slowed)",
            "SyntheticArtistBeta - Topic",
            null,
            "brave",
            1);
        MediaOverlaySessionSnapshot wrongAtEighteen = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "bc she cares",
            "Bupin - Topic",
            null,
            "brave",
            18);
        MediaOverlaySessionSnapshot correctedAtZero = wrongAtEighteen with { PositionSeconds = 0 };

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
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
                ["brave"] = wrongAtEighteen,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = correctedAtZero,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = correctedAtZero,
            }),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "bc she cares",
        ExpectedArtist: "Bupin - Topic");

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackReusesRecoveredSourceAcrossRapidCommands_AndRebasesComparison()
    {
        MediaOverlaySessionSnapshot browserA = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Browser Track A",
            "Browser Artist",
            null,
            "chrome",
            120);
        MediaOverlaySessionSnapshot spotifyB = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track B",
            "Spotify Artist",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot spotifyC = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track C",
            "Spotify Artist",
            null,
            "spotify",
            1);
        Queue<List<MediaOverlaySessionSnapshot>> sessionSnapshotQueue = new(
        [
            [browserA, spotifyB],
            [browserA, spotifyB],
        ]);
        Queue<Dictionary<string, MediaOverlaySessionSnapshot>> snapshotsBySourceQueue = new(
        [
            new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome"] = browserA,
            },
            new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome"] = browserA,
            },
        ]);
        Lock gate = new();
        int sessionSnapshotRequests = 0;
        int spotifyPreferredRequests = 0;

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase))
                {
                    spotifyPreferredRequests++;
                    if (spotifyPreferredRequests == 1)
                    {
                        return Task.FromResult(spotifyB);
                    }

                    if (spotifyPreferredRequests == 2 && sessionSnapshotRequests == 1)
                    {
                        return Task.FromResult(spotifyC);
                    }

                    if (spotifyPreferredRequests == 2)
                    {
                        return Task.FromResult(spotifyB);
                    }

                    return Task.FromResult(spotifyC);
                }

                return Task.FromResult(browserA);
            },
            snapshotsBySourceOverride: (_, _) =>
            {
                lock (gate)
                {
                    Dictionary<string, MediaOverlaySessionSnapshot> snapshots = snapshotsBySourceQueue.Count > 0
                        ? snapshotsBySourceQueue.Dequeue()
                        : new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["chrome"] = browserA,
                        };
                    return Task.FromResult(snapshots);
                }
            },
            sessionSnapshotsOverride: (_, _) =>
            {
                lock (gate)
                {
                    sessionSnapshotRequests++;
                    List<MediaOverlaySessionSnapshot> snapshots = sessionSnapshotQueue.Count > 0
                        ? sessionSnapshotQueue.Dequeue()
                        : [browserA, spotifyB];
                    return Task.FromResult(snapshots);
                }
            });

        MediaOverlayResult firstResult = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);
        MediaOverlayResult secondResult = await engine.SendWithBestEffortOverlayAsync(MediaOverlayCommand.NextTrack, () => true);

        Assert.Equal(MediaOverlayResultKind.TrackMessage, firstResult.Kind);
        Assert.Equal("Spotify Track B", firstResult.Title);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, secondResult.Kind);
        Assert.Equal("Spotify Track C", secondResult.Title);
        Assert.Equal("Spotify Artist", secondResult.Artist);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackUsesEventAssistToResolvePreferredSourceSooner()
    {
        bool commandSent = false;
        bool eventObserved = false;
        MediaOverlaySessionSnapshot spotifyBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track A",
            "Spotify Artist",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot spotifyNext = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track B",
            "Spotify Artist",
            null,
            "spotify",
            1);
        MediaOverlaySessionSnapshot browserCurrent = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Browser Audio",
            "Browser Source",
            null,
            "chrome",
            120);

        int currentSnapshotCalls = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(spotifyBaseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase) && eventObserved)
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
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                spotifyNext,
                browserCurrent,
            }),
            eventWaitOverride: (_, _, _, _) =>
            {
                eventObserved = true;
                return Task.FromResult(new MediaEventAssistOutcome(true, null));
            });

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.True(eventObserved);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Spotify Track B", result.Title);
        Assert.Equal("Spotify Artist", result.Artist);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackAcceptsPendingCorroborationCandidate_WhenSourceRecentlySignaled()
    {
        bool commandSent = false;
        bool eventObserved = false;
        MediaOverlaySessionSnapshot spotifyBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track A",
            "Spotify Artist",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot spotifyChangedWithoutTimelineReset = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track B",
            "Spotify Artist",
            null,
            "spotify",
            126);
        MediaOverlaySessionSnapshot browserCurrent = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Browser Audio",
            "Browser Source",
            null,
            "chrome",
            120);

        int currentSnapshotCalls = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                currentSnapshotCalls++;
                if (currentSnapshotCalls == 1)
                {
                    return Task.FromResult(spotifyBaseline);
                }

                if (string.Equals(preferredSource, "spotify", StringComparison.OrdinalIgnoreCase) && eventObserved)
                {
                    return Task.FromResult(spotifyChangedWithoutTimelineReset);
                }

                return Task.FromResult(browserCurrent);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = spotifyBaseline,
                ["chrome"] = browserCurrent,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                spotifyChangedWithoutTimelineReset,
                browserCurrent,
            }),
            eventWaitOverride: (_, _, _, _) =>
            {
                eventObserved = true;
                return Task.FromResult(new MediaEventAssistOutcome(true, "spotify"));
            });

        MediaOverlayResult result = await engine.SendWithBestEffortOverlayAsync(
            MediaOverlayCommand.NextTrack,
            () =>
            {
                commandSent = true;
                return true;
            });

        Assert.True(commandSent);
        Assert.True(eventObserved);
        Assert.Equal(MediaOverlayResultKind.TrackMessage, result.Kind);
        Assert.Equal("Spotify Track B", result.Title);
        Assert.Equal("Spotify Artist", result.Artist);
    }

    [Fact]
    public async Task ReplayScenario_PlayPauseEmitsDiagnostics_ForImmediateCurrentSnapshotEvidence()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Track A",
            "Artist A",
            null,
            "spotify",
            12);
        MediaOverlaySessionSnapshot resumed = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            12);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: resumed),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Track A",
        ExpectedArtist: "Artist A",
        ExpectedPlayPauseDiagnostics: new MediaOverlayPlayPauseDiagnostics(
            FinalPath: "immediate-current-snapshot",
            Outcome: "changed",
            UsedEventAssist: false,
            UsedChangedBySourceSnapshots: false,
            UsedImmediateCurrentEvidence: true,
            ReusedBaselineMetadata: false));

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario, MediaOverlayCommand.PlayPause);
    }

    [Fact]
    public async Task ReplayScenario_PreviousTrackSameSongRestart_EmitsSameTrackRestartDiagnostics()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            8);
        MediaOverlaySessionSnapshot restarted = baseline with { PositionSeconds = 0 };

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: restarted),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Track A",
        ExpectedArtist: "Artist A",
        ExpectedDiagnostics: new MediaOverlayTrackNavigationDiagnostics(
            FinalPhase: "initial-preferred-source-sampling",
            Outcome: "changed",
            FinalChangeKind: "same-track-restart",
            SawSessionDrop: false,
            UsedSessionDropRecovery: false,
            UsedLateTrackLoadRecovery: false,
            UsedRecoveredAlternateSource: false,
            FinalFallbackClassification: "changed"));

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario, MediaOverlayCommand.PreviousTrack);
    }

    [Fact]
    public async Task ReplayScenario_NextTrackCurrentSessionSwitchRace_PicksSettledCorroboratedSource()
    {
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Browser Track",
            "Browser Artist",
            null,
            "brave",
            240);
        MediaOverlaySessionSnapshot preCommandSpotify = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track A",
            "Spotify Artist",
            null,
            "spotify",
            84);
        MediaOverlaySessionSnapshot settledSpotify = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Spotify Track B",
            "Spotify Artist",
            null,
            "spotify",
            0);

        var scenario = new MediaOverlayTestHarness.ReplayScenario(
        [
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(SnapshotsBySource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
                ["spotify"] = preCommandSpotify,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: baseline),
            new MediaOverlayTestHarness.ReplayStep(EventAssistOutcome: new MediaEventAssistOutcome(true, "spotify", MediaEventAssistKind.CurrentSessionChanged)),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshotsByPreferredSource: new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["brave"] = baseline,
            }),
            new MediaOverlayTestHarness.ReplayStep(CurrentSnapshot: settledSpotify),
        ],
        MediaOverlayResultKind.TrackMessage,
        ExpectedTitle: "Spotify Track B",
        ExpectedArtist: "Spotify Artist",
        ExpectedDiagnostics: new MediaOverlayTrackNavigationDiagnostics(
            FinalPhase: "initial-preferred-source-sampling",
            Outcome: "changed",
            FinalChangeKind: "source-switched",
            SawSessionDrop: false,
            UsedSessionDropRecovery: false,
            UsedLateTrackLoadRecovery: false,
            UsedRecoveredAlternateSource: false,
            FinalFallbackClassification: "changed"));

        await MediaOverlayTestHarness.AssertReplayScenarioAsync(scenario);
    }
}
