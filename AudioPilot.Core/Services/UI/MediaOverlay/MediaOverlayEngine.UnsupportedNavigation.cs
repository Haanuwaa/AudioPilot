using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed partial class MediaOverlayEngine
    {
        /// <summary>
        /// Observes an explicitly unsupported single-player command briefly without suppressing its native-key fallback.
        /// New non-timeline evidence or uncertainty leaves confirmation to normal recovery.
        /// </summary>
        private async Task<(MediaOverlayResult? Overlay, bool Attempted)> TryResolveUnsupportedNavigationAsync(
            MediaOverlayCommand command,
            Func<MediaKeyHelper.MediaCommandSendOutcome?>? outcomeProvider,
            SessionSnapshot baseline,
            SessionSnapshot current,
            Dictionary<string, SessionSnapshot> preCommandSnapshots,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            MediaKeyHelper.MediaCommandSendOutcome? outcome;
            try
            {
                outcome = outcomeProvider?.Invoke();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace("MediaOverlayHelper", $"Navigation capability outcome unavailable. {ex.GetType().Name}");
                return default;
            }

            if (outcome is not { Sent: true, UsedSendInputFallback: true, UnsupportedNavigationSourceAppUserModelId: { } source }
                || !string.Equals(source, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                || preCommandSnapshots.Count != 1
                || !IsUnchangedUnsupportedNavigation(command, baseline, baseline)
                || !IsUnchangedUnsupportedNavigation(command, baseline, current)
                || !IsUnchangedUnsupportedNavigation(command, baseline, preCommandSnapshots.Values.First()))
            {
                return default;
            }

            long started = Stopwatch.GetTimestamp();
            long? eventVersion = await TryGetNavigationEventVersionAsync(commandSequence, cancellationToken);
            if (eventVersion != 0) return (null, true);
            int remainingMs = AppConstants.MediaOverlay.UnsupportedNavigationObservationMs;
            int timelineRechecks = 0;
            while (true)
            {
                MediaOverlayDelayAssistResult delay = await DelayWithEventAssistOutcomeIfWithinBudgetAsync(
                    remainingMs, source, deadlineUtc, commandSequence, cancellationToken);
                if (!delay.CompletedWithinBudget
                    || (delay.ObservedEvent && delay.EventAssistOutcome.EventKind != MediaEventAssistKind.TimelinePropertiesChanged))
                    return (null, true);

                SessionSnapshot soleCurrent = await TryGetSoleUnsupportedSessionAsync(command, baseline, commandSequence, cancellationToken);
                _sessionTracker.ThrowIfSuperseded(commandSequence, cancellationToken);
                long? currentEventVersion = await TryGetNavigationEventVersionAsync(commandSequence, cancellationToken);
                _sessionTracker.ThrowIfSuperseded(commandSequence, cancellationToken);
                if (currentEventVersion != eventVersion
                    || !IsUnchangedUnsupportedNavigation(command, baseline, soleCurrent)
                    || !IsOrdinaryTimelineProgress(current, soleCurrent, Stopwatch.GetElapsedTime(started), delay.ObservedEvent))
                    return (null, true);

                if (!delay.ObservedEvent) break;
                double remaining = AppConstants.MediaOverlay.UnsupportedNavigationObservationMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining <= 0) break;
                if (++timelineRechecks >= AppConstants.MediaOverlay.UnsupportedNavigationMaxTimelineRechecks) return (null, true);
                remainingMs = (int)Math.Ceiling(remaining);
            }

            RecordTrackNavigationDiagnostics(new MediaOverlayTrackNavigationDiagnostics(
                "unsupported-navigation-observation", "unchanged", "none", false, false, false, false, "unchanged",
                CommandSequence: commandSequence, ElapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            RecordOverlayTelemetry(MediaOverlayTelemetryEvent.PlainShown, MediaOverlayTelemetryOutcomeClass.UnchangedFallback);
            return (MediaOverlayResult.Plain("No track change detected"), true);
        }

        private static bool IsOrdinaryTimelineProgress(SessionSnapshot previous, SessionSnapshot current, TimeSpan elapsed, bool requirePosition)
        {
            if (!previous.PositionSeconds.HasValue || !current.PositionSeconds.HasValue) return !requirePosition;
            long delta = current.PositionSeconds.Value - previous.PositionSeconds.Value;
            return delta >= 0 && delta <= Math.Ceiling(elapsed.TotalSeconds) + 1;
        }

        private static bool IsUnchangedUnsupportedNavigation(MediaOverlayCommand command, SessionSnapshot baseline, SessionSnapshot snapshot)
        {
            bool? supported = command switch
            {
                MediaOverlayCommand.NextTrack => snapshot.IsNextEnabled,
                MediaOverlayCommand.PreviousTrack => snapshot.IsPreviousEnabled,
                _ => null,
            };
            return supported == false && HasTrackData(snapshot) && !string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId)
                && snapshot.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                && snapshot.PlaybackStatus == baseline.PlaybackStatus && IsSameTrack(baseline, snapshot)
                && !MediaOverlayTrackNavigationRecoveryCoordinator.HasConfirmedTrackNavigationTransition(command, baseline, snapshot);
        }

        private async Task<SessionSnapshot> TryGetSoleUnsupportedSessionAsync(
            MediaOverlayCommand command, SessionSnapshot baseline, long commandSequence, CancellationToken cancellationToken)
        {
            try
            {
                _sessionTracker.ThrowIfSuperseded(commandSequence, cancellationToken);
                if (_sessionSnapshotsOverride is not null)
                {
                    List<SessionSnapshot> sessions = await _sessionSnapshotsOverride(commandSequence, cancellationToken);
                    SessionSnapshot current = await TryGetCurrentSnapshotAsync(commandSequence, cancellationToken);
                    if (sessions.Count != 1 || !IsUnchangedUnsupportedNavigation(command, baseline, current)) return SessionSnapshot.Empty;
                    sessions = await _sessionSnapshotsOverride(commandSequence, cancellationToken);
                    return sessions.Count == 1 ? sessions[0] : SessionSnapshot.Empty;
                }

                GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence, cancellationToken);
                IReadOnlyList<GlobalSystemMediaTransportControlsSession> nativeSessions = manager.GetSessions();
                GlobalSystemMediaTransportControlsSession? nativeCurrent = manager.GetCurrentSession();
                if (nativeSessions.Count != 1 || nativeCurrent is null
                    || !string.Equals(nativeCurrent.SourceAppUserModelId, nativeSessions[0].SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
                    return SessionSnapshot.Empty;

                SessionSnapshot snapshot = await TryBuildSnapshotAsync(nativeSessions[0], cancellationToken);
                nativeSessions = manager.GetSessions();
                nativeCurrent = manager.GetCurrentSession();
                return nativeSessions.Count == 1 && nativeCurrent is not null
                    && string.Equals(nativeCurrent.SourceAppUserModelId, snapshot.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(nativeSessions[0].SourceAppUserModelId, snapshot.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                        ? snapshot : SessionSnapshot.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace("MediaOverlayHelper", $"Single-player navigation capability could not be revalidated. {ex.GetType().Name}");
                return SessionSnapshot.Empty;
            }
        }
    }
}
