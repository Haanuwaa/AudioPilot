using AudioPilot.Logging;
using Windows.Media.Control;

namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayPrivacyTests
{
    [Fact]
    public void CurrentSnapshotLog_ReportsAvailabilityWithoutTrackText()
    {
        var snapshot = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "private title", "private artist", "private album", "private-source", 54);

        string message = MediaOverlayEngine.BuildCurrentMediaSnapshotLog(snapshot, true, null, 8);

        Assert.Contains("outcome=available", message, StringComparison.Ordinal);
        Assert.Contains("hasTitle=True", message, StringComparison.Ordinal);
        Assert.Contains($"source={LogPrivacy.Id(snapshot.SourceAppUserModelId)}", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private title", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private artist", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private album", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedSameSourceConflictLog_DoesNotIncludeWinningTrackFingerprint()
    {
        const string sensitiveFingerprint = "source|private title|private artist|private album";
        var winner = new BrowserSameSourceWinnerElectionResult(
            HasWinner: true,
            WinnerIsCurrentCandidate: true,
            WinningTrackFingerprint: sensitiveFingerprint,
            WinningReasonClass: BrowserPendingCandidateReasonClass.AmbiguousNearStart,
            PromotionKind: BrowserSameSourcePromotionKind.StableRepetition,
            ActiveRivalCount: 1,
            ReinforcedRivalCount: 1,
            StaleRivalCount: 0,
            RivalReasonClasses: "AmbiguousNearStart",
            StaleRivalIgnored: false);
        var summary = new BrowserSameSourceCommandSummary(
            ConflictObserved: true,
            ActiveRivalCount: 1,
            ReinforcedRivalCount: 1,
            RivalReasonClasses: "AmbiguousNearStart",
            WinnerElection: winner);

        string message = MediaOverlayTrackNavigationRecoveryCoordinator.BuildUnresolvedSameSourceConflictLog(
            "private-source",
            summary);

        Assert.Contains("winnerPresent=True", message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveFingerprint, message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-source", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private title", message, StringComparison.Ordinal);
    }
}
