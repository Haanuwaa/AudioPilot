using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal static class MediaOverlayBrowserSameSourcePolicy
    {
        private static readonly string[] BrowserLikeSourceTokens =
        [
            "brave",
            "chromium",
            "chrome",
            "msedge",
            "edge",
            "firefox",
            "opera",
            "vivaldi",
            "arc",
        ];

        internal static bool IsBrowserLikeSource(string? sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return false;
            }

            string normalized = sourceAppUserModelId.Trim().ToLowerInvariant();
            for (int index = 0; index < BrowserLikeSourceTokens.Length; index++)
            {
                if (normalized.Contains(BrowserLikeSourceTokens[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsBrowserLikeSameSource(
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate)
        {
            if (preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(reference.SourceAppUserModelId)
                && string.Equals(reference.SourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                && IsBrowserLikeSource(candidate.SourceAppUserModelId);
        }

        internal static PreferredSourceCandidateDecision EvaluateGenericNonPlayingDecision(
            SessionSnapshot reference,
            SessionSnapshot candidate)
        {
            if (reference.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value <= RuntimeTuningConfig.MediaOverlaySameSourcePausedCandidateNearStartWindowSeconds)
            {
                return PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.PausedNearStartWhileReferencePaused);
            }

            return PreferredSourceCandidateDecision.Reject(PreferredSourceCandidateReason.PausedSibling);
        }

        internal static PreferredSourceCandidateDecision EvaluateBrowserNonPlayingDecision()
        {
            return PreferredSourceCandidateDecision.Reject(PreferredSourceCandidateReason.PausedSibling);
        }

        internal static PreferredSourceCandidateDecision EvaluateTrackDataAvailabilityDecision(bool browserLikeSameSource)
        {
            return browserLikeSameSource
                ? PreferredSourceCandidateDecision.Pending(PreferredSourceCandidateReason.PlayingWithoutMetadata)
                : PreferredSourceCandidateDecision.Accept(PreferredSourceCandidateReason.PlayingWithoutMetadata);
        }

        internal static bool CanPendingCandidateConverge(PreferredSourceCandidateDecision initialDecision)
        {
            return GetPendingCandidateReasonClass(initialDecision) is BrowserPendingCandidateReasonClass.AmbiguousNearStart
                or BrowserPendingCandidateReasonClass.MetadataPending;
        }

        internal static bool CanPendingCandidateConvergeAfterConflict(PreferredSourceCandidateDecision initialDecision)
        {
            return GetPendingCandidateReasonClass(initialDecision) == BrowserPendingCandidateReasonClass.AmbiguousNearStart;
        }

        internal static bool CanStableObservationCorroborate(
            BrowserPendingCandidateReasonClass reasonClass,
            long? previousPositionSeconds,
            long? currentPositionSeconds)
        {
            return reasonClass switch
            {
                BrowserPendingCandidateReasonClass.MetadataPending => true,
                BrowserPendingCandidateReasonClass.AmbiguousNearStart => IsWithinStrictAmbiguousNearStartWindow(previousPositionSeconds)
                    && IsWithinStrictAmbiguousNearStartWindow(currentPositionSeconds),
                BrowserPendingCandidateReasonClass.FarPosition => previousPositionSeconds.HasValue
                    && currentPositionSeconds.HasValue
                    && Math.Abs(currentPositionSeconds.Value - previousPositionSeconds.Value) <= RuntimeTuningConfig.MediaOverlayBrowserPendingConvergencePositionBucketSeconds,
                _ => false,
            };
        }

        internal static bool CanFarPositionCandidateConvergeByPositionCorrection(PreferredSourceCandidateDecision initialDecision)
        {
            return GetPendingCandidateReasonClass(initialDecision) is BrowserPendingCandidateReasonClass.FarPosition
                or BrowserPendingCandidateReasonClass.AmbiguousNearStart;
        }

        internal static bool IsRecoverablePendingReason(PreferredSourceCandidateReason reason)
        {
            return reason == PreferredSourceCandidateReason.PlayingWithoutMetadata
                || reason == PreferredSourceCandidateReason.AmbiguousSameSourceNearStart;
        }

        internal static string GetPendingConvergenceEligibilityReason(PreferredSourceCandidateDecision decision)
        {
            if (!decision.IsPending)
            {
                return "<none>";
            }

            return GetPendingCandidateReasonClass(decision) switch
            {
                BrowserPendingCandidateReasonClass.AmbiguousNearStart => "ambiguous-near-start",
                BrowserPendingCandidateReasonClass.MetadataPending => "metadata-pending",
                BrowserPendingCandidateReasonClass.FarPosition => "far-position",
                _ => "<none>",
            };
        }

        internal static BrowserPendingCandidateReasonClass GetPendingCandidateReasonClass(PreferredSourceCandidateDecision decision)
        {
            if (!decision.IsPending)
            {
                return BrowserPendingCandidateReasonClass.None;
            }

            return decision.Reason switch
            {
                PreferredSourceCandidateReason.AmbiguousSameSourceNearStart => BrowserPendingCandidateReasonClass.AmbiguousNearStart,
                PreferredSourceCandidateReason.PlayingWithoutMetadata => BrowserPendingCandidateReasonClass.MetadataPending,
                PreferredSourceCandidateReason.FarPositionDelta => BrowserPendingCandidateReasonClass.FarPosition,
                _ => BrowserPendingCandidateReasonClass.None,
            };
        }

        internal static BrowserPendingCandidateReasonClass GetSameSourceCandidateReasonClass(PreferredSourceCandidateDecision decision)
        {
            BrowserPendingCandidateReasonClass pendingReasonClass = GetPendingCandidateReasonClass(decision);
            if (pendingReasonClass != BrowserPendingCandidateReasonClass.None)
            {
                return pendingReasonClass;
            }

            if (!decision.IsRejected)
            {
                return BrowserPendingCandidateReasonClass.None;
            }

            return decision.Reason switch
            {
                PreferredSourceCandidateReason.PausedSibling => BrowserPendingCandidateReasonClass.PausedSibling,
                _ => BrowserPendingCandidateReasonClass.RejectedOther,
            };
        }

        internal static bool TryCreateSameSourceCandidateEvidence(
            PreferredSourceCandidateDecision initialDecision,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            bool hasRecentSignalForSource,
            out BrowserSameSourceEvidence evidence)
        {
            evidence = default;
            if (!IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate))
            {
                return false;
            }

            if (preferredReferenceSnapshot is not SessionSnapshot reference
                || MediaOverlayEngine.IsSessionMissing(reference)
                || MediaOverlayEngine.IsSessionMissing(candidate)
                || MediaOverlayEngine.IsSameTrack(reference, candidate))
            {
                return false;
            }

            BrowserPendingCandidateReasonClass reasonClass = GetSameSourceCandidateReasonClass(initialDecision);
            if (reasonClass == BrowserPendingCandidateReasonClass.None)
            {
                return false;
            }

            evidence = new BrowserSameSourceEvidence(
                reasonClass,
                BuildPendingCandidateFingerprint(candidate),
                BuildPendingCandidateTrackFingerprint(candidate),
                candidate.PositionSeconds,
                hasRecentSignalForSource);
            return true;
        }

        internal static bool ShouldIgnoreRecentSignal(
            bool hasRecentSignalForSource,
            SessionSnapshot? preferredReferenceSnapshot,
            SessionSnapshot candidate,
            PreferredSourceCandidateResolution resolution)
        {
            return hasRecentSignalForSource
                && resolution.InitialDecision.IsPending
                && !resolution.WasUpgraded
                && IsBrowserLikeSameSource(preferredReferenceSnapshot, candidate);
        }

        internal static string BuildPendingCandidateFingerprint(SessionSnapshot snapshot)
        {
            string source = CleanValue(snapshot.SourceAppUserModelId)?.ToLowerInvariant() ?? string.Empty;
            string title = CleanValue(snapshot.Title)?.ToLowerInvariant() ?? string.Empty;
            string artist = CleanValue(snapshot.Artist)?.ToLowerInvariant() ?? string.Empty;
            string status = snapshot.PlaybackStatus?.ToString() ?? string.Empty;
            long bucket = snapshot.PositionSeconds.GetValueOrDefault()
                / RuntimeTuningConfig.MediaOverlayBrowserPendingConvergencePositionBucketSeconds;
            return string.Concat(source, "|", title, "|", artist, "|", status, "|", bucket.ToString());
        }

        internal static string BuildPendingCandidateTrackFingerprint(SessionSnapshot snapshot)
        {
            string source = CleanValue(snapshot.SourceAppUserModelId)?.ToLowerInvariant() ?? string.Empty;
            string title = CleanValue(snapshot.Title)?.ToLowerInvariant() ?? string.Empty;
            string artist = CleanValue(snapshot.Artist)?.ToLowerInvariant() ?? string.Empty;
            string album = CleanValue(snapshot.AlbumTitle)?.ToLowerInvariant() ?? string.Empty;
            return string.Concat(source, "|", title, "|", artist, "|", album);
        }

        internal static bool IsWithinStrictAmbiguousNearStartWindow(long? positionSeconds)
        {
            return positionSeconds.HasValue
                && positionSeconds.Value <= RuntimeTuningConfig.MediaOverlayAmbiguousSameSourceNearStartWindowSeconds;
        }

        internal static string DescribeReasonClass(BrowserPendingCandidateReasonClass reasonClass)
        {
            return reasonClass switch
            {
                BrowserPendingCandidateReasonClass.AmbiguousNearStart => "ambiguous-near-start",
                BrowserPendingCandidateReasonClass.MetadataPending => "metadata-pending",
                BrowserPendingCandidateReasonClass.FarPosition => "far-position",
                BrowserPendingCandidateReasonClass.PausedSibling => "paused-sibling",
                BrowserPendingCandidateReasonClass.RejectedOther => "rejected-other",
                _ => "<none>",
            };
        }

        private static string? CleanValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim();
        }
    }
}
