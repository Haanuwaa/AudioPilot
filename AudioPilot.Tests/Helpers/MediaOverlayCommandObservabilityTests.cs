namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayCommandObservabilityTests
{
    [Fact]
    public void ClassifyTrackTelemetryOutcome_ReturnsSameTrackRestartChange_ForRestartedTrackOverlay()
    {
        var observability = new MediaOverlayCommandObservability();
        MediaOverlayResult result = MediaOverlayResult.Track("Previous track", "Track A", "Artist A");
        SnapshotCaptureResult capture = new(
            new MediaOverlaySessionSnapshot(null, "Track A", "Artist A", null, "source-a", 0),
            SawSessionDrop: false,
            TrackNavigationRecoveryDisposition.Changed,
            ChangeKind: TrackNavigationChangeKind.SameTrackRestart);

        MediaOverlayTelemetryOutcomeClass outcome = observability.ClassifyTrackTelemetryOutcome(result, capture);

        Assert.Equal(MediaOverlayTelemetryOutcomeClass.SameTrackRestartChange, outcome);
    }

    [Fact]
    public void ClassifyTrackTelemetryOutcome_ReturnsSourceSwitchedChange_ForSwitchedSourceOverlay()
    {
        var observability = new MediaOverlayCommandObservability();
        MediaOverlayResult result = MediaOverlayResult.Track("Next track", "Track B", "Artist B");
        SnapshotCaptureResult capture = new(
            new MediaOverlaySessionSnapshot(null, "Track B", "Artist B", null, "source-a", 0),
            SawSessionDrop: false,
            TrackNavigationRecoveryDisposition.Changed,
            ChangeKind: TrackNavigationChangeKind.SourceSwitched);

        MediaOverlayTelemetryOutcomeClass outcome = observability.ClassifyTrackTelemetryOutcome(result, capture);

        Assert.Equal(MediaOverlayTelemetryOutcomeClass.SourceSwitchedChange, outcome);
    }

    [Fact]
    public void ClassifyTrackTelemetryOutcome_ReturnsDirectChange_ForOrdinaryTrackChange()
    {
        var observability = new MediaOverlayCommandObservability();
        MediaOverlayResult result = MediaOverlayResult.Track("Next track", "Track C", "Artist C");
        SnapshotCaptureResult capture = new(
            new MediaOverlaySessionSnapshot(null, "Track C", "Artist C", null, "source-a", 0),
            SawSessionDrop: false,
            TrackNavigationRecoveryDisposition.Changed,
            ChangeKind: TrackNavigationChangeKind.TrackChanged);

        MediaOverlayTelemetryOutcomeClass outcome = observability.ClassifyTrackTelemetryOutcome(result, capture);

        Assert.Equal(MediaOverlayTelemetryOutcomeClass.DirectChange, outcome);
    }
}
