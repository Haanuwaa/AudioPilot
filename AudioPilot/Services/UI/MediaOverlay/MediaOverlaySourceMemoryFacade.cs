using AudioPilot.Constants;
using AudioPilot.Logging;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlaySourceMemoryFacade(MediaOverlayStateStore state)
    {
        private const string GlobalMediaCommandGroupKey = "media";
        private readonly MediaOverlayStateStore _state = state;

        public string? TryGetStickySource(string commandGroupKey)
        {
            string? commandSpecific = _state.TryGetStickySource(
                commandGroupKey,
                AppConstants.MediaOverlay.StickySourceTtlSeconds);
            if (!string.IsNullOrWhiteSpace(commandSpecific))
            {
                return commandSpecific;
            }

            if (string.Equals(commandGroupKey, GlobalMediaCommandGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return _state.TryGetStickySource(
                GlobalMediaCommandGroupKey,
                AppConstants.MediaOverlay.StickySourceTtlSeconds);
        }

        public void UpdateStickySource(string commandGroupKey, string sourceAppUserModelId)
        {
            _state.UpdateStickySource(commandGroupKey, sourceAppUserModelId);
            if (!string.Equals(commandGroupKey, GlobalMediaCommandGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                _state.UpdateStickySource(GlobalMediaCommandGroupKey, sourceAppUserModelId);
            }
        }

        public void ClearStickySource(string commandGroupKey)
        {
            _state.ClearStickySource(commandGroupKey);
        }

        public string? TryGetRecoveredSource(string commandGroupKey)
        {
            return _state.TryGetRecoveredSource(
                commandGroupKey,
                AppConstants.MediaOverlay.StickySourceTtlSeconds);
        }

        public void ClearRecoveredSource(string commandGroupKey)
        {
            _state.ClearRecoveredSource(commandGroupKey);
        }

        public string? GetValidatedStickySource(
            string commandGroupKey,
            string? stickySource,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots)
        {
            string? validatedStickySource = MediaOverlaySourceSelector.ValidateStickySourceForSampling(
                stickySource,
                baseline,
                postCommandCurrent,
                preCommandSnapshots);

            if (!string.IsNullOrWhiteSpace(stickySource)
                && !string.Equals(stickySource, validatedStickySource, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Discarding stale sticky media sampling source. {MediaOverlaySourceSelector.DescribeStickySourceValidation(stickySource, baseline, postCommandCurrent, preCommandSnapshots)} baseline={MediaOverlayEngine.FormatSnapshot(baseline)} postCommandCurrent={MediaOverlayEngine.FormatSnapshot(postCommandCurrent)}",
                    nameof(MediaOverlayEngine.SendWithBestEffortOverlayAsync));
                ClearStickySource(commandGroupKey);
            }

            return validatedStickySource;
        }

        public string? GetValidatedRecoveredSource(
            string commandGroupKey,
            string? recoveredSource,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots,
            bool recoveredSourceIsTrusted)
        {
            string? validatedRecoveredSource = MediaOverlaySourceSelector.ValidateStickySourceForSampling(
                recoveredSource,
                baseline,
                postCommandCurrent,
                preCommandSnapshots);
            if (!string.IsNullOrWhiteSpace(validatedRecoveredSource))
            {
                return validatedRecoveredSource;
            }

            if (!string.IsNullOrWhiteSpace(recoveredSource) && recoveredSourceIsTrusted)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Preserving recently trusted track-nav recovery source. source={LogPrivacy.Id(recoveredSource)} baseline={MediaOverlayEngine.FormatSnapshot(baseline)} postCommandCurrent={MediaOverlayEngine.FormatSnapshot(postCommandCurrent)}",
                    nameof(MediaOverlayEngine.SendWithBestEffortOverlayAsync));
                return recoveredSource;
            }

            if (!string.IsNullOrWhiteSpace(recoveredSource)
                && !string.Equals(recoveredSource, validatedRecoveredSource, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"Discarding stale track-nav recovery source. {MediaOverlaySourceSelector.DescribeStickySourceValidation(recoveredSource, baseline, postCommandCurrent, preCommandSnapshots)} baseline={MediaOverlayEngine.FormatSnapshot(baseline)} postCommandCurrent={MediaOverlayEngine.FormatSnapshot(postCommandCurrent)}",
                    nameof(MediaOverlayEngine.SendWithBestEffortOverlayAsync));
                ClearRecoveredSource(commandGroupKey);
            }

            return null;
        }

        public void RecordRecoveredSourceOutcome(
            MediaOverlayCommand command,
            string commandGroupKey,
            SessionSnapshot baseline,
            SessionSnapshot finalSnapshot)
        {
            if (command is not MediaOverlayCommand.NextTrack and not MediaOverlayCommand.PreviousTrack)
            {
                return;
            }

            if (MediaOverlayEngine.IsSessionMissing(finalSnapshot)
                || string.IsNullOrWhiteSpace(finalSnapshot.SourceAppUserModelId)
                || string.IsNullOrWhiteSpace(baseline.SourceAppUserModelId))
            {
                ClearRecoveredSource(commandGroupKey);
                return;
            }

            if (!string.Equals(finalSnapshot.SourceAppUserModelId, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                _state.UpdateRecoveredSource(commandGroupKey, finalSnapshot.SourceAppUserModelId!);
                return;
            }

            ClearRecoveredSource(commandGroupKey);
        }

        public void RecordRecentSignal(string? sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return;
            }

            _state.MarkRecentlySignaledSource(sourceAppUserModelId);
        }

        public bool HasRecentSignalForSource(string? sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return false;
            }

            return _state.HasRecentSignal(
                sourceAppUserModelId,
                AppConstants.MediaOverlay.RecentSignalTtlMs);
        }

        public void RecordTrustedTrackNavigationSource(string? sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return;
            }

            _state.MarkTrustedTrackNavigationSource(sourceAppUserModelId);
        }

        public bool HasTrustedTrackNavigationSource(string? sourceAppUserModelId)
        {
            if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
            {
                return false;
            }

            return _state.HasTrustedTrackNavigationSource(
                sourceAppUserModelId,
                AppConstants.MediaOverlay.RecentTrustedSourceTtlMs);
        }

        public bool IsInFirstCommandGraceWindow(
            string commandGroupKey,
            string? currentSourceAppUserModelId,
            int? firstCommandGraceWindowMs = null)
        {
            if (string.IsNullOrWhiteSpace(commandGroupKey) || string.IsNullOrWhiteSpace(currentSourceAppUserModelId))
            {
                return false;
            }

            return _state.IsInFirstCommandGraceWindow(
                commandGroupKey,
                currentSourceAppUserModelId,
                firstCommandGraceWindowMs ?? AppConstants.MediaOverlay.FirstCommandGraceWindowMs);
        }
    }
}
