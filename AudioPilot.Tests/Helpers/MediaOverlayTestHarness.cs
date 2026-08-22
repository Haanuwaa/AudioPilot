using AudioPilot.Constants;

namespace AudioPilot.Tests.Helpers;

internal static class MediaOverlayTestHarness
{
    private sealed class VirtualClock(DateTimeOffset initialUtc)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _utcNow = initialUtc;

        public DateTimeOffset UtcNow()
        {
            lock (_gate)
            {
                DateTimeOffset current = _utcNow;
                _utcNow = _utcNow.AddMilliseconds(1);
                return current;
            }
        }

        public void Advance(int milliseconds)
        {
            lock (_gate)
            {
                _utcNow = _utcNow.AddMilliseconds(Math.Max(1, milliseconds));
            }
        }
    }

    internal sealed record ReplayStep(
        MediaOverlaySessionSnapshot? CurrentSnapshot = null,
        Dictionary<string, MediaOverlaySessionSnapshot>? CurrentSnapshotsByPreferredSource = null,
        Dictionary<string, MediaOverlaySessionSnapshot>? SnapshotsBySource = null,
        List<MediaOverlaySessionSnapshot>? SessionSnapshots = null,
        MediaEventAssistOutcome? EventAssistOutcome = null);

    internal sealed record ReplayScenario(
        IReadOnlyList<ReplayStep> Steps,
        MediaOverlayResultKind ExpectedKind,
        string? ExpectedTitle = null,
        string? ExpectedArtist = null,
        string? ExpectedMessage = null,
        MediaOverlayTrackNavigationDiagnostics? ExpectedDiagnostics = null,
        MediaOverlayPlayPauseDiagnostics? ExpectedPlayPauseDiagnostics = null);

    public static Func<string?, long, CancellationToken, Task<MediaOverlaySessionSnapshot>> CreateQueuedCurrentSnapshotOverride(
        params MediaOverlaySessionSnapshot[] snapshots)
    {
        Queue<MediaOverlaySessionSnapshot> queue = new(snapshots);
        MediaOverlaySessionSnapshot fallback = snapshots.Length > 0 ? snapshots[^1] : MediaOverlaySessionSnapshot.Empty;
        Lock gate = new();

        return (_, _, _) =>
        {
            lock (gate)
            {
                MediaOverlaySessionSnapshot snapshot = queue.Count > 0 ? queue.Dequeue() : fallback;
                return Task.FromResult(snapshot);
            }
        };
    }

    public static Func<long, CancellationToken, Task<Dictionary<string, MediaOverlaySessionSnapshot>>> CreateQueuedSnapshotsBySourceOverride(
        params Dictionary<string, MediaOverlaySessionSnapshot>[] snapshots)
    {
        Queue<Dictionary<string, MediaOverlaySessionSnapshot>> queue = new(snapshots);
        Dictionary<string, MediaOverlaySessionSnapshot> fallback = snapshots.Length > 0
            ? snapshots[^1]
            : new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        Lock gate = new();

        return (_, _) =>
        {
            lock (gate)
            {
                Dictionary<string, MediaOverlaySessionSnapshot> snapshot = queue.Count > 0 ? queue.Dequeue() : fallback;
                return Task.FromResult(snapshot);
            }
        };
    }

    public static MediaOverlayEngineTestAdapter CreateReplayAdapter(
        ReplayScenario scenario,
        MediaOverlayTimingProfile? timingProfile = null)
    {
        Queue<ReplayStep> currentQueue = new(scenario.Steps.Where(step => step.CurrentSnapshot != null || step.CurrentSnapshotsByPreferredSource != null));
        Queue<ReplayStep> bySourceQueue = new(scenario.Steps.Where(step => step.SnapshotsBySource != null));
        Queue<ReplayStep> sessionQueue = new(scenario.Steps.Where(step => step.SessionSnapshots != null));
        Queue<ReplayStep> eventQueue = new(scenario.Steps.Where(step => step.EventAssistOutcome != null));
        Lock gate = new();
        var clock = new VirtualClock(DateTimeOffset.UtcNow);

        return new MediaOverlayEngineTestAdapter(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                lock (gate)
                {
                    ReplayStep? step = currentQueue.Count > 0
                        ? currentQueue.Dequeue()
                        : scenario.Steps.LastOrDefault(replayStep => replayStep.CurrentSnapshot != null || replayStep.CurrentSnapshotsByPreferredSource != null);

                    if (step?.CurrentSnapshotsByPreferredSource != null)
                    {
                        if (!string.IsNullOrWhiteSpace(preferredSource)
                            && step.CurrentSnapshotsByPreferredSource.TryGetValue(preferredSource, out MediaOverlaySessionSnapshot preferredSnapshot))
                        {
                            return Task.FromResult(preferredSnapshot);
                        }

                        if (step.CurrentSnapshotsByPreferredSource.TryGetValue("*", out MediaOverlaySessionSnapshot wildcardSnapshot))
                        {
                            return Task.FromResult(wildcardSnapshot);
                        }
                    }

                    return Task.FromResult(step?.CurrentSnapshot ?? MediaOverlaySessionSnapshot.Empty);
                }
            },
            snapshotsBySourceOverride: (_, _) =>
            {
                lock (gate)
                {
                    ReplayStep? step = bySourceQueue.Count > 0 ? bySourceQueue.Dequeue() : scenario.Steps.LastOrDefault(replayStep => replayStep.SnapshotsBySource != null);
                    return Task.FromResult(step?.SnapshotsBySource ?? new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase));
                }
            },
            sessionSnapshotsOverride: (_, _) =>
            {
                lock (gate)
                {
                    ReplayStep? step = sessionQueue.Count > 0 ? sessionQueue.Dequeue() : scenario.Steps.LastOrDefault(replayStep => replayStep.SessionSnapshots != null);
                    return Task.FromResult(step?.SessionSnapshots ?? []);
                }
            },
            eventWaitOverride: (_, delayMs, _, _) =>
            {
                clock.Advance(delayMs);
                lock (gate)
                {
                    ReplayStep? step = eventQueue.Count > 0 ? eventQueue.Dequeue() : scenario.Steps.LastOrDefault(replayStep => replayStep.EventAssistOutcome != null);
                    return Task.FromResult(step?.EventAssistOutcome ?? new MediaEventAssistOutcome(false, null));
                }
            },
            timingProfile: timingProfile,
            utcNow: clock.UtcNow);
    }

    public static MediaOverlayTimingProfile CreateTimingProfile(
        int? initialSettleDelayMs = null,
        int? retryDelayMs = null,
        int? maxAttempts = null,
        int? maxCaptureDurationMs = null,
        int? extraAttemptsAfterSessionDrop = null,
        int? sessionDropRecoveryInitialDelayMs = null,
        int? sessionDropRecoveryRetryDelayMs = null,
        int? sessionDropRecoveryAttempts = null,
        int? sessionDropTrackLoadRecoveryInitialDelayMs = null,
        int? sessionDropTrackLoadRecoveryRetryDelayMs = null,
        int? sessionDropTrackLoadRecoveryAttempts = null,
        int? unchangedRecoveryInitialDelayMs = null,
        int? unchangedRecoveryRetryDelayMs = null,
        int? unchangedRecoveryAttempts = null,
        int? firstCommandGraceWindowMs = null,
        int? graceRecoveryInitialDelayMs = null,
        int? graceRecoveryRetryDelayMs = null,
        int? graceRecoveryAttempts = null,
        int? playPauseSettleInitialDelayMs = null,
        int? playPauseSettleRetryDelayMs = null,
        int? playPauseSettleAttempts = null,
        int? trackLoadRecoveryInitialDelayMs = null,
        int? trackLoadRecoveryRetryDelayMs = null,
        int? trackLoadRecoveryAttempts = null,
        int? stagnantTrackRecoveryInitialDelayMs = null,
        int? stagnantTrackRecoveryRetryDelayMs = null,
        int? stagnantTrackRecoveryAttempts = null)
    {
        MediaOverlayTimingProfile defaults = MediaOverlayTimingProfile.Default;
        return new MediaOverlayTimingProfile(
            initialSettleDelayMs ?? defaults.InitialSettleDelayMs,
            retryDelayMs ?? defaults.RetryDelayMs,
            maxAttempts ?? defaults.MaxAttempts,
            maxCaptureDurationMs ?? defaults.MaxCaptureDurationMs,
            extraAttemptsAfterSessionDrop ?? defaults.ExtraAttemptsAfterSessionDrop,
            sessionDropRecoveryInitialDelayMs ?? defaults.SessionDropRecoveryInitialDelayMs,
            sessionDropRecoveryRetryDelayMs ?? defaults.SessionDropRecoveryRetryDelayMs,
            sessionDropRecoveryAttempts ?? defaults.SessionDropRecoveryAttempts,
            sessionDropTrackLoadRecoveryInitialDelayMs ?? defaults.SessionDropTrackLoadRecoveryInitialDelayMs,
            sessionDropTrackLoadRecoveryRetryDelayMs ?? defaults.SessionDropTrackLoadRecoveryRetryDelayMs,
            sessionDropTrackLoadRecoveryAttempts ?? defaults.SessionDropTrackLoadRecoveryAttempts,
            unchangedRecoveryInitialDelayMs ?? defaults.UnchangedRecoveryInitialDelayMs,
            unchangedRecoveryRetryDelayMs ?? defaults.UnchangedRecoveryRetryDelayMs,
            unchangedRecoveryAttempts ?? defaults.UnchangedRecoveryAttempts,
            firstCommandGraceWindowMs ?? defaults.FirstCommandGraceWindowMs,
            graceRecoveryInitialDelayMs ?? defaults.GraceRecoveryInitialDelayMs,
            graceRecoveryRetryDelayMs ?? defaults.GraceRecoveryRetryDelayMs,
            graceRecoveryAttempts ?? defaults.GraceRecoveryAttempts,
            playPauseSettleInitialDelayMs ?? defaults.PlayPauseSettleInitialDelayMs,
            playPauseSettleRetryDelayMs ?? defaults.PlayPauseSettleRetryDelayMs,
            playPauseSettleAttempts ?? defaults.PlayPauseSettleAttempts,
            trackLoadRecoveryInitialDelayMs ?? defaults.TrackLoadRecoveryInitialDelayMs,
            trackLoadRecoveryRetryDelayMs ?? defaults.TrackLoadRecoveryRetryDelayMs,
            trackLoadRecoveryAttempts ?? defaults.TrackLoadRecoveryAttempts,
            stagnantTrackRecoveryInitialDelayMs ?? defaults.StagnantTrackRecoveryInitialDelayMs,
            stagnantTrackRecoveryRetryDelayMs ?? defaults.StagnantTrackRecoveryRetryDelayMs,
            stagnantTrackRecoveryAttempts ?? defaults.StagnantTrackRecoveryAttempts);
    }

    public static MediaOverlayTimingProfile CreateDeterministicNoDelayTimingProfile(
        int maxAttempts = 5,
        int maxCaptureDurationMs = 1000,
        int unchangedRecoveryAttempts = 4,
        int trackLoadRecoveryAttempts = 3,
        int stagnantTrackRecoveryAttempts = 2)
    {
        return CreateTimingProfile(
            initialSettleDelayMs: 0,
            retryDelayMs: 0,
            maxAttempts: maxAttempts,
            maxCaptureDurationMs: maxCaptureDurationMs,
            sessionDropRecoveryInitialDelayMs: 0,
            sessionDropRecoveryRetryDelayMs: 0,
            unchangedRecoveryInitialDelayMs: 0,
            unchangedRecoveryRetryDelayMs: 0,
            unchangedRecoveryAttempts: unchangedRecoveryAttempts,
            firstCommandGraceWindowMs: 0,
            graceRecoveryInitialDelayMs: 0,
            graceRecoveryRetryDelayMs: 0,
            trackLoadRecoveryInitialDelayMs: 0,
            trackLoadRecoveryRetryDelayMs: 0,
            trackLoadRecoveryAttempts: trackLoadRecoveryAttempts,
            stagnantTrackRecoveryInitialDelayMs: 0,
            stagnantTrackRecoveryRetryDelayMs: 0,
            stagnantTrackRecoveryAttempts: stagnantTrackRecoveryAttempts);
    }

    public static MediaOverlaySourceSelector CreateSourceSelector()
    {
        return new MediaOverlaySourceSelector();
    }

    public static MediaOverlayTrackNavigationRecoveryCoordinator CreateTrackNavigationRecoveryCoordinator(
        Func<string?, long, CancellationToken, Task<MediaOverlaySessionSnapshot>> currentSnapshotOverride,
        MediaOverlayTimingProfile? timingProfile = null,
        Action<MediaOverlayTrackNavigationDiagnostics>? diagnosticsSink = null)
    {
        MediaOverlayTimingProfile profile = timingProfile ?? MediaOverlayTimingProfile.Default;
        var clock = new VirtualClock(DateTimeOffset.UtcNow);
        return new MediaOverlayTrackNavigationRecoveryCoordinator(
            (delayMs, _, deadlineUtc, _, _) =>
            {
                bool withinBudget = clock.UtcNow().AddMilliseconds(delayMs) <= deadlineUtc;
                clock.Advance(delayMs);
                return Task.FromResult(new MediaOverlayDelayAssistResult(withinBudget, ObservedEvent: false));
            },
            (preferredSourceAppUserModelId, commandSequence, _, _, cancellationToken) =>
                currentSnapshotOverride(preferredSourceAppUserModelId, commandSequence, cancellationToken),
            (_, _, _, _, _, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            (_, _, _) => new TrackNavigationStreakDecision(false, 0, 0),
            (_, _, _) => { },
            _ => false,
            _ => false,
            (_, _) => default,
            _ => false,
            diagnosticsSink,
            profile,
            clock.UtcNow);
    }

    public static MediaOverlaySessionDropRecoveryRunner CreateSessionDropRecoveryRunner(
        Func<string?, long, CancellationToken, Task<MediaOverlaySessionSnapshot>> currentSnapshotOverride,
        MediaOverlayTimingProfile? timingProfile = null)
    {
        MediaOverlayTimingProfile profile = timingProfile ?? MediaOverlayTimingProfile.Default;
        var clock = new VirtualClock(DateTimeOffset.UtcNow);
        return new MediaOverlaySessionDropRecoveryRunner(
            (delayMs, _, deadlineUtc, _, _) =>
            {
                bool withinBudget = clock.UtcNow().AddMilliseconds(delayMs) <= deadlineUtc;
                clock.Advance(delayMs);
                return Task.FromResult(withinBudget);
            },
            (preferredSourceAppUserModelId, commandSequence, _, _, cancellationToken) =>
                currentSnapshotOverride(preferredSourceAppUserModelId, commandSequence, cancellationToken),
            (command, baseline, latest) =>
            {
                if (MediaOverlayEngine.IsSessionMissing(latest) || !MediaOverlayEngine.HasTrackData(latest))
                {
                    return false;
                }

                if (!MediaOverlayEngine.IsSameTrack(baseline, latest))
                {
                    return MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, latest);
                }

                return command == MediaOverlayCommand.PreviousTrack
                    && baseline.PositionSeconds.HasValue
                    && latest.PositionSeconds.HasValue
                    && baseline.PositionSeconds.Value >= 2
                    && latest.PositionSeconds.Value <= AppConstants.MediaOverlay.TimelineResetToSeconds
                    && latest.PositionSeconds.Value < baseline.PositionSeconds.Value;
            },
            (_, _, _) => false,
            profile,
            clock.UtcNow);
    }

    public static async Task<MediaOverlayResult> AssertReplayScenarioAsync(
        ReplayScenario scenario,
        MediaOverlayCommand command = MediaOverlayCommand.NextTrack,
        MediaOverlayTimingProfile? timingProfile = null)
    {
        bool commandSent = false;
        MediaOverlayEngineTestAdapter adapter = CreateReplayAdapter(scenario, timingProfile);
        MediaOverlayEngineTestAdapterResult adapterResult = await adapter.SendWithBestEffortOverlayAsync(
            command,
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
        Assert.Equal(scenario.ExpectedMessage, result.Message);

        if (scenario.ExpectedDiagnostics is { } expectedDiagnostics)
        {
            MediaOverlayTrackNavigationDiagnostics? actualDiagnostics = adapterResult.TrackNavigationDiagnostics;
            Assert.NotNull(actualDiagnostics);
            Assert.Equal(expectedDiagnostics.FinalPhase, actualDiagnostics.Value.FinalPhase);
            Assert.Equal(expectedDiagnostics.Outcome, actualDiagnostics.Value.Outcome);
            Assert.Equal(expectedDiagnostics.FinalChangeKind, actualDiagnostics.Value.FinalChangeKind);
            Assert.Equal(expectedDiagnostics.SawSessionDrop, actualDiagnostics.Value.SawSessionDrop);
            Assert.Equal(expectedDiagnostics.UsedSessionDropRecovery, actualDiagnostics.Value.UsedSessionDropRecovery);
            Assert.Equal(expectedDiagnostics.UsedLateTrackLoadRecovery, actualDiagnostics.Value.UsedLateTrackLoadRecovery);
            Assert.Equal(expectedDiagnostics.UsedRecoveredAlternateSource, actualDiagnostics.Value.UsedRecoveredAlternateSource);
            Assert.Equal(expectedDiagnostics.FinalFallbackClassification, actualDiagnostics.Value.FinalFallbackClassification);
        }

        if (scenario.ExpectedPlayPauseDiagnostics is { } expectedPlayPauseDiagnostics)
        {
            MediaOverlayPlayPauseDiagnostics? actualDiagnostics = adapterResult.PlayPauseDiagnostics;
            Assert.NotNull(actualDiagnostics);
            Assert.Equal(expectedPlayPauseDiagnostics.FinalPath, actualDiagnostics.Value.FinalPath);
            Assert.Equal(expectedPlayPauseDiagnostics.Outcome, actualDiagnostics.Value.Outcome);
            Assert.Equal(expectedPlayPauseDiagnostics.UsedEventAssist, actualDiagnostics.Value.UsedEventAssist);
            Assert.Equal(expectedPlayPauseDiagnostics.UsedChangedBySourceSnapshots, actualDiagnostics.Value.UsedChangedBySourceSnapshots);
            Assert.Equal(expectedPlayPauseDiagnostics.UsedImmediateCurrentEvidence, actualDiagnostics.Value.UsedImmediateCurrentEvidence);
            Assert.Equal(expectedPlayPauseDiagnostics.ReusedBaselineMetadata, actualDiagnostics.Value.ReusedBaselineMetadata);
        }

        return result;
    }
}
