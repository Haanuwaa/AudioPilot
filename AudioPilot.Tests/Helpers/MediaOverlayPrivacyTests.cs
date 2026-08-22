namespace AudioPilot.Tests.Helpers;

public sealed class MediaOverlayPrivacyTests
{
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
