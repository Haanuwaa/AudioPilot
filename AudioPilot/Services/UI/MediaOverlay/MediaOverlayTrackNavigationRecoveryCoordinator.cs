using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayTrackNavigationRecoveryCoordinator(
        Func<int, string?, DateTimeOffset, long, CancellationToken, Task<MediaOverlayDelayAssistResult>> delayWithEventAssistIfWithinBudgetAsync,
        Func<string?, long, SessionSnapshot?, bool, CancellationToken, Task<SessionSnapshot>> captureCurrentSnapshotAsync,
        Func<SessionSnapshot, SessionSnapshot, Dictionary<string, SessionSnapshot>, bool, string?, long, CancellationToken, Task<SessionSnapshot>> findChangedAlternateSnapshotAsync,
        Func<MediaOverlayCommand, SessionSnapshot, SessionSnapshot, TrackNavigationStreakDecision> evaluateTrackNavigationStreakDecision,
        Action<MediaOverlayCommand, SessionSnapshot, SessionSnapshot> resetStreakIfChanged,
        Func<string?, bool> hasRecentSignalForSource,
        Func<string?, bool> hasTrustedTrackNavigationSource,
        Func<string?, long, BrowserSameSourceCommandSummary> getBrowserSameSourceCommandSummary,
        Func<SessionSnapshot, bool> hasCommittedBrowserConvergenceForSnapshot,
        Action<MediaOverlayTrackNavigationDiagnostics>? recordDiagnostics,
        MediaOverlayTimingProfile timingProfile,
        Func<DateTimeOffset>? utcNow = null)
    {
        private readonly Func<int, string?, DateTimeOffset, long, CancellationToken, Task<MediaOverlayDelayAssistResult>> _delayWithEventAssistIfWithinBudgetAsync = delayWithEventAssistIfWithinBudgetAsync;
        private readonly Func<string?, long, SessionSnapshot?, bool, CancellationToken, Task<SessionSnapshot>> _captureCurrentSnapshotAsync = captureCurrentSnapshotAsync;
        private readonly Func<SessionSnapshot, SessionSnapshot, Dictionary<string, SessionSnapshot>, bool, string?, long, CancellationToken, Task<SessionSnapshot>> _findChangedAlternateSnapshotAsync = findChangedAlternateSnapshotAsync;
        private readonly Func<MediaOverlayCommand, SessionSnapshot, SessionSnapshot, TrackNavigationStreakDecision> _evaluateTrackNavigationStreakDecision = evaluateTrackNavigationStreakDecision;
        private readonly Action<MediaOverlayCommand, SessionSnapshot, SessionSnapshot> _resetStreakIfChanged = resetStreakIfChanged;
        private readonly Func<string?, bool> _hasRecentSignalForSource = hasRecentSignalForSource;
        private readonly Func<string?, bool> _hasTrustedTrackNavigationSource = hasTrustedTrackNavigationSource;
        private readonly Func<string?, long, BrowserSameSourceCommandSummary> _getBrowserSameSourceCommandSummary = getBrowserSameSourceCommandSummary;
        private readonly Func<SessionSnapshot, bool> _hasCommittedBrowserConvergenceForSnapshot = hasCommittedBrowserConvergenceForSnapshot;
        private readonly Action<MediaOverlayTrackNavigationDiagnostics>? _recordDiagnostics = recordDiagnostics;
        private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        private readonly MediaOverlayBrowserPendingCorroborationStrategy _browserPendingCorroborationStrategy = new(
            getBrowserSameSourceCommandSummary,
            timingProfile);
        private readonly MediaOverlaySessionDropRecoveryRunner _sessionDropRecoveryRunner = new(
            async (delayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken) =>
                (await delayWithEventAssistIfWithinBudgetAsync(
                    delayMs,
                    preferredSourceAppUserModelId,
                    deadlineUtc,
                    commandSequence,
                    cancellationToken)).CompletedWithinBudget,
            captureCurrentSnapshotAsync,
            (command, baseline, latest) =>
                HasConfirmedTrackNavigationTransition(command, baseline, latest)
                || (!MediaOverlayEngine.IsSessionMissing(latest)
                    && !MediaOverlayEngine.IsSameTrack(baseline, latest)
                    && MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(baseline, latest)
                    && hasCommittedBrowserConvergenceForSnapshot(latest)),
            (preferredSourceAppUserModelId, baseline, commandSequence) =>
                new MediaOverlayBrowserPendingCorroborationStrategy(
                    getBrowserSameSourceCommandSummary,
                    timingProfile).ShouldAbortConflictedBrowserRecoveryEarly(
                        preferredSourceAppUserModelId,
                        baseline,
                        commandSequence),
            timingProfile,
            utcNow);
        private readonly MediaOverlayTimingProfile _timingProfile = timingProfile;

        public async Task<SnapshotCaptureResult> CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand command,
            SessionSnapshot originalBaseline,
            SessionSnapshot baseline,
            string? preferredSourceForCommand,
            Dictionary<string, SessionSnapshot> preCommandSnapshots,
            bool isInGraceWindow,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            return await CaptureSnapshotWithRetryAsync(
                command,
                originalBaseline,
                baseline,
                preferredSourceForCommand,
                preCommandSnapshots,
                isInGraceWindow,
                commandSequence,
                deadlineUtc,
                preferredSourceIsCommandTarget: false,
                cancellationToken);
        }

        public async Task<SnapshotCaptureResult> CaptureSnapshotWithRetryAsync(
            MediaOverlayCommand command,
            SessionSnapshot originalBaseline,
            SessionSnapshot baseline,
            string? preferredSourceForCommand,
            Dictionary<string, SessionSnapshot> preCommandSnapshots,
            bool isInGraceWindow,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            bool preferredSourceIsCommandTarget,
            CancellationToken cancellationToken)
        {
            MediaOverlayCommandPolicy commandPolicy = MediaOverlayCommandPolicy.For(command);
            TrackNavigationRecoveryContext context = new(
                command,
                originalBaseline,
                baseline,
                baseline,
                preferredSourceForCommand,
                preCommandSnapshots,
                commandPolicy,
                preferredSourceIsCommandTarget,
                isInGraceWindow,
                commandSequence,
                deadlineUtc);
            var stateMachine = new MediaOverlayTrackNavigationRecoveryStateMachine();
            long recoveryStartTimestamp = Stopwatch.GetTimestamp();

            InitialTrackNavigationSamplingResult initialSampling = await RunInitialTrackNavigationSamplingPhaseAsync(
                context,
                cancellationToken);
            if (initialSampling.Completed)
            {
                stateMachine.Complete(TrackNavigationRecoveryOutcome.Changed);
                RecordDiagnostics("initial-preferred-source-sampling", "changed", initialSampling.ChangeKind, initialSampling.SawSessionDrop, false, false, false, "changed", default, context.CommandSequence, Stopwatch.GetElapsedTime(recoveryStartTimestamp).TotalMilliseconds);
                return new SnapshotCaptureResult(initialSampling.ResolvedSnapshot, initialSampling.SawSessionDrop, TrackNavigationRecoveryDisposition.Changed, initialSampling.ChangeKind);
            }

            SessionSnapshot latest = initialSampling.Latest;
            SessionSnapshot fallback = initialSampling.Fallback;
            bool sawSessionDrop = initialSampling.SawSessionDrop;
            SessionDropResolutionResult sessionDropResolution = default;
            bool usedSessionDropRecovery = false;
            bool usedLateTrackLoadRecovery = false;
            bool usedRecoveredAlternateSource = false;
            bool stableUnchangedNonBrowser = false;

            if (sawSessionDrop)
            {
                stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.SessionDropRecovery);
                usedSessionDropRecovery = true;
                LogTrackNavigationRecoveryPhase(
                    "session-drop-recovery",
                    context,
                    $"starting fallback={MediaOverlayEngine.FormatSnapshot(fallback)}");
                MediaOverlaySessionDropRecoveryResult resolvedSessionDrop = await _sessionDropRecoveryRunner.ResolveAsync(
                    context.Command,
                    context.Baseline,
                    fallback,
                    sawSessionDrop,
                    context.PreferredSourceForCommand,
                    context.CommandSequence,
                    context.DeadlineUtc,
                    nameof(CaptureSnapshotWithRetryAsync),
                    cancellationToken);
                sessionDropResolution = new SessionDropResolutionResult(
                    resolvedSessionDrop.Snapshot,
                    resolvedSessionDrop.PollAttempts,
                    resolvedSessionDrop.EndedByDeadline,
                    resolvedSessionDrop.UsedExtendedTrackLoadRecovery,
                    resolvedSessionDrop.ElapsedMs);

                if (!MediaOverlayEngine.IsSessionMissing(sessionDropResolution.Snapshot))
                {
                    fallback = sessionDropResolution.Snapshot;
                }
            }

            if (!sawSessionDrop
                && !MediaOverlayEngine.IsSessionMissing(fallback)
                && MediaOverlayEngine.IsSameTrack(context.Baseline, fallback)
                && MediaOverlayEngine.HasTrackData(fallback))
            {
                stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.UnchangedRecovery);
                LogTrackNavigationRecoveryPhase(
                    "unchanged-track-recovery",
                    context,
                    $"starting fallback={MediaOverlayEngine.FormatSnapshot(fallback)}");
                TrackNavigationStreakDecision streakDecision = _evaluateTrackNavigationStreakDecision(
                    context.Command,
                    context.Baseline,
                    fallback);

                SessionSnapshot alternate = await _findChangedAlternateSnapshotAsync(
                    context.Baseline,
                    fallback,
                    context.PreCommandSnapshots,
                    streakDecision.ForceAlternateAfterStreak,
                    context.PreferredSourceForCommand,
                    context.CommandSequence,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(alternate))
                {
                    SessionSnapshot confirmedAlternate = await ConfirmChangedAlternateSnapshotAsync(
                        context,
                        alternate,
                        cancellationToken);
                    if (!MediaOverlayEngine.IsSessionMissing(confirmedAlternate))
                    {
                        usedRecoveredAlternateSource = true;
                        Logger.Instance?.Debug(
                            "MediaOverlayHelper",
                            () => $"Adopting confirmed alternate source after unchanged preferred-source streak={streakDecision.UnchangedStreak} stagnantPositionStreak={streakDecision.StagnantPositionStreak}. baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} preferred={MediaOverlayEngine.FormatSnapshot(fallback)} alternate={MediaOverlayEngine.FormatSnapshot(alternate)} confirmed={MediaOverlayEngine.FormatSnapshot(confirmedAlternate)}",
                            nameof(CaptureSnapshotWithRetryAsync));
                        fallback = confirmedAlternate;
                    }
                    else
                    {
                        Logger.Instance?.Debug(
                            "MediaOverlayHelper",
                            () => $"Rejected transient alternate source after unchanged preferred-source streak={streakDecision.UnchangedStreak} stagnantPositionStreak={streakDecision.StagnantPositionStreak}. baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} preferred={MediaOverlayEngine.FormatSnapshot(fallback)} alternate={MediaOverlayEngine.FormatSnapshot(alternate)}",
                            nameof(CaptureSnapshotWithRetryAsync));
                        UnchangedRecoveryProbeResult unchangedRecovery = await RecoverAfterUnchangedAsync(context, fallback, cancellationToken);
                        fallback = unchangedRecovery.Snapshot;
                        stableUnchangedNonBrowser = unchangedRecovery.StableBaselineRepeated;
                    }
                }
                else
                {
                    UnchangedRecoveryProbeResult unchangedRecovery = await RecoverAfterUnchangedAsync(context, fallback, cancellationToken);
                    fallback = unchangedRecovery.Snapshot;
                    stableUnchangedNonBrowser = unchangedRecovery.StableBaselineRepeated;
                }
            }

            if (!MediaOverlayEngine.IsSessionMissing(fallback) && MediaOverlayEngine.HasTrackData(fallback))
            {
                _resetStreakIfChanged(context.Command, context.Baseline, fallback);

                bool hasObservedTransition = IsConfirmedOrCommittedTrackNavigationTransition(
                    context.Command,
                    context.Baseline,
                    fallback);

                if (hasObservedTransition)
                {
                    stateMachine.Complete(TrackNavigationRecoveryOutcome.Changed);
                    TrackNavigationChangeKind changeKind = ResolveChangeKind(context, fallback);
                    string finalPhase = usedLateTrackLoadRecovery
                        ? "late-track-load-recovery"
                        : usedRecoveredAlternateSource
                        ? "unchanged-track-recovery"
                        : (usedSessionDropRecovery ? "session-drop-recovery" : "initial-preferred-source-sampling");
                    RecordDiagnostics(finalPhase, "changed", changeKind, sawSessionDrop, usedSessionDropRecovery, usedLateTrackLoadRecovery, usedRecoveredAlternateSource, "changed", default, context.CommandSequence, Stopwatch.GetElapsedTime(recoveryStartTimestamp).TotalMilliseconds);
                    Logger.Instance?.Debug(
                        "MediaOverlayHelper",
                        () => $"Recovered media snapshot with observed transition for {context.Command}. sawSessionDrop={sawSessionDrop} baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} fallback={MediaOverlayEngine.FormatSnapshot(fallback)}",
                        nameof(CaptureSnapshotWithRetryAsync));

                    return new SnapshotCaptureResult(fallback, sawSessionDrop, TrackNavigationRecoveryDisposition.Changed, changeKind, usedRecoveredAlternateSource);
                }

                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => $"Recovered usable media snapshot but still unchanged for {context.Command}. sawSessionDrop={sawSessionDrop} baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} fallback={MediaOverlayEngine.FormatSnapshot(fallback)}",
                    nameof(CaptureSnapshotWithRetryAsync));

                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => $"Rejecting unchanged fallback track metadata for {context.Command}. baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} fallback={MediaOverlayEngine.FormatSnapshot(fallback)}",
                    nameof(CaptureSnapshotWithRetryAsync));

                if (stableUnchangedNonBrowser
                    && IsEligibleForEarlyUnchangedExit(context, fallback, sawSessionDrop, usedRecoveredAlternateSource))
                {
                    stateMachine.Complete(TrackNavigationRecoveryOutcome.Unchanged);
                    RecordDiagnostics(
                        "unchanged-track-recovery",
                        "unchanged",
                        TrackNavigationChangeKind.TrackChanged,
                        sawSessionDrop,
                        usedSessionDropRecovery,
                        usedLateTrackLoadRecovery,
                        usedRecoveredAlternateSource,
                        "unchanged",
                        default,
                        context.CommandSequence,
                        Stopwatch.GetElapsedTime(recoveryStartTimestamp).TotalMilliseconds);
                    return new SnapshotCaptureResult(
                        fallback,
                        sawSessionDrop,
                        TrackNavigationRecoveryDisposition.Unchanged,
                        TrackNavigationChangeKind.TrackChanged,
                        usedRecoveredAlternateSource);
                }
            }

            if (MediaOverlayEngine.IsSessionMissing(fallback)
                || !MediaOverlayEngine.HasTrackData(fallback)
                || !IsConfirmedOrCommittedTrackNavigationTransition(context.Command, context.Baseline, fallback))
            {
                stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.LateTrackLoadRecovery);
                usedLateTrackLoadRecovery = true;
                LogTrackNavigationRecoveryPhase(
                    "late-track-load-recovery",
                    context,
                    $"starting fallback={MediaOverlayEngine.FormatSnapshot(fallback)}");
                SessionSnapshot recoveredTrackLoad = await TryRecoverAfterTrackLoadAsync(
                    context.Command,
                    context.PreferredSourceForCommand,
                    context.Baseline,
                    true,
                    context.CommandSequence,
                    context.DeadlineUtc,
                    cancellationToken);

                if (!MediaOverlayEngine.IsSessionMissing(recoveredTrackLoad))
                {
                    fallback = recoveredTrackLoad;
                    if (TryBuildChangedResult(
                        context,
                        fallback,
                        sawSessionDrop,
                        usedSessionDropRecovery,
                        usedLateTrackLoadRecovery,
                        usedRecoveredAlternateSource,
                        recoveryStartTimestamp,
                        out SnapshotCaptureResult changedResult))
                    {
                        stateMachine.Complete(TrackNavigationRecoveryOutcome.Changed);
                        return changedResult;
                    }
                }
            }

            if (ShouldProbeForLateTrackLoadAfterSessionDrop(context.Baseline, fallback, sawSessionDrop))
            {
                stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.LateTrackLoadRecovery);
                usedLateTrackLoadRecovery = true;
                SessionSnapshot recoveredLateTrackLoad = await TryRecoverChangedTrackUntilDeadlineAsync(
                    context.Command,
                    context.PreferredSourceForCommand,
                    context.Baseline,
                    context.CommandSequence,
                    context.DeadlineUtc,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(recoveredLateTrackLoad))
                {
                    fallback = recoveredLateTrackLoad;
                    if (TryBuildChangedResult(
                        context,
                        fallback,
                        sawSessionDrop,
                        usedSessionDropRecovery,
                        usedLateTrackLoadRecovery,
                        usedRecoveredAlternateSource,
                        recoveryStartTimestamp,
                        out SnapshotCaptureResult changedResult))
                    {
                        stateMachine.Complete(TrackNavigationRecoveryOutcome.Changed);
                        return changedResult;
                    }
                }
            }

            if (!MediaOverlayEngine.IsSessionMissing(fallback)
                && MediaOverlayEngine.HasTrackData(fallback)
                && !MediaOverlayEngine.IsSameTrack(context.Baseline, fallback)
                && !MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(context.Baseline, fallback))
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    () => $"Ignoring non-playing track-navigation fallback after recovery. baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} fallback={MediaOverlayEngine.FormatSnapshot(fallback)}",
                    nameof(CaptureSnapshotWithRetryAsync));
                fallback = MediaOverlayEngine.HasTrackData(context.Baseline) ? context.Baseline : SessionSnapshot.Empty;
            }

            Logger.Instance?.Trace(
                "MediaOverlayHelper",
                () => $"No useful media transition detected after retries for {context.Command}. sawSessionDrop={sawSessionDrop} sessionDropAttempts={sessionDropResolution.PollAttempts} sessionDropEndedByDeadline={sessionDropResolution.EndedByDeadline} sessionDropUsedExtendedTrackLoad={sessionDropResolution.UsedExtendedTrackLoadRecovery} sessionDropElapsedMs={sessionDropResolution.ElapsedMs} baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} latest={MediaOverlayEngine.FormatSnapshot(latest)} fallback={MediaOverlayEngine.FormatSnapshot(fallback)}",
                nameof(CaptureSnapshotWithRetryAsync));

            string? recoverySource = context.PreferredSourceForCommand ?? context.Baseline.SourceAppUserModelId;
            stateMachine.AdvanceTo(TrackNavigationRecoveryPhase.FinalEvaluation);
            BrowserSameSourceCommandSummary sameSourceCommandSummary = _getBrowserSameSourceCommandSummary(recoverySource, context.CommandSequence);
            TrackNavigationRecoveryDisposition recoveryDisposition = MediaOverlayTrackNavigationRecoveryPolicy.ClassifyFinalRecoveryDisposition(
                context.Baseline,
                fallback,
                sawSessionDrop,
                _hasRecentSignalForSource(recoverySource),
                sameSourceCommandSummary);
            if (recoveryDisposition.Outcome == TrackNavigationRecoveryOutcome.Loading
                && sameSourceCommandSummary.ConflictObserved)
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    () => BuildUnresolvedSameSourceConflictLog(recoverySource, sameSourceCommandSummary),
                    nameof(CaptureSnapshotWithRetryAsync));
            }
            RecordDiagnostics(
                "final-fallback",
                MediaOverlayTrackNavigationRecoveryPolicy.DescribeRecoveryOutcome(recoveryDisposition.Outcome),
                TrackNavigationChangeKind.TrackChanged,
                sawSessionDrop,
                usedSessionDropRecovery,
                usedLateTrackLoadRecovery,
                usedRecoveredAlternateSource,
                MediaOverlayTrackNavigationRecoveryPolicy.DescribeFallbackClassification(recoveryDisposition.FallbackClassification),
                sameSourceCommandSummary,
                context.CommandSequence,
                Stopwatch.GetElapsedTime(recoveryStartTimestamp).TotalMilliseconds);
            stateMachine.Complete(recoveryDisposition.Outcome);
            return new SnapshotCaptureResult(fallback, sawSessionDrop, recoveryDisposition, TrackNavigationChangeKind.TrackChanged, usedRecoveredAlternateSource);
        }

        private async Task<InitialTrackNavigationSamplingResult> RunInitialTrackNavigationSamplingPhaseAsync(
            TrackNavigationRecoveryContext context,
            CancellationToken cancellationToken)
        {
            int initialSettleDelayMs = ResolveInitialSettleDelayMs(context);
            MediaOverlayDelayAssistResult delayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                initialSettleDelayMs,
                context.PreferredSourceForCommand,
                context.DeadlineUtc,
                context.CommandSequence,
                cancellationToken);
            if (!delayResult.CompletedWithinBudget)
            {
                return new InitialTrackNavigationSamplingResult(
                    Latest: SessionSnapshot.Empty,
                    Fallback: SessionSnapshot.Empty,
                    ResolvedSnapshot: SessionSnapshot.Empty,
                    SawSessionDrop: false,
                    Completed: false);
            }

            SessionSnapshot latest = SessionSnapshot.Empty;
            SessionSnapshot lastWithTrackData = SessionSnapshot.Empty;
            SessionSnapshot lastConvergedBrowserWinner = SessionSnapshot.Empty;
            SessionSnapshot lastStableChangedCandidate = SessionSnapshot.Empty;
            bool sawSessionDrop = false;
            int maxAttempts = _timingProfile.MaxAttempts;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    delayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                        _timingProfile.RetryDelayMs,
                        context.PreferredSourceForCommand,
                        context.DeadlineUtc,
                        context.CommandSequence,
                        cancellationToken);
                    if (!delayResult.CompletedWithinBudget)
                    {
                        break;
                    }
                }

                latest = await _captureCurrentSnapshotAsync(
                    context.PreferredSourceForCommand,
                    context.CommandSequence,
                    context.EffectiveBaseline,
                    context.CommandPolicy.AllowSingleCandidateMetadataChangeFallback,
                    cancellationToken);

                if (MediaOverlayEngine.IsSessionMissing(latest))
                {
                    if (!MediaOverlayEngine.IsSessionMissing(context.Baseline))
                    {
                        sawSessionDrop = true;
                        maxAttempts = Math.Max(maxAttempts, _timingProfile.MaxAttempts + _timingProfile.ExtraAttemptsAfterSessionDrop);
                    }
                }
                else if (MediaOverlayEngine.HasTrackData(latest))
                {
                    lastWithTrackData = latest;

                    if (TryResolveCommittedBrowserWinner(context, latest, out SessionSnapshot committedBrowserWinner))
                    {
                        lastConvergedBrowserWinner = committedBrowserWinner;
                    }
                }

                SessionSnapshot trustworthySignalCandidate = await TryResolveTrustworthySignalSnapshotAsync(
                    context,
                    latest,
                    delayResult.EventAssistOutcome,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(trustworthySignalCandidate))
                {
                    LogTrackNavigationRecoveryPhase(
                        "initial-preferred-source-sampling",
                        context,
                        $"completed-trustworthy-signal eventKind={delayResult.EventAssistOutcome.EventKind} latest={MediaOverlayEngine.FormatSnapshot(latest)} resolved={MediaOverlayEngine.FormatSnapshot(trustworthySignalCandidate)}");
                    return new InitialTrackNavigationSamplingResult(
                        latest,
                        trustworthySignalCandidate,
                        trustworthySignalCandidate,
                        sawSessionDrop,
                        Completed: true,
                        ChangeKind: ResolveChangeKind(context, trustworthySignalCandidate));
                }

                if (HasUsefulData(context.Command, context.Baseline, latest))
                {
                    LogTrackNavigationRecoveryPhase(
                        "initial-preferred-source-sampling",
                        context,
                        $"completed latest={MediaOverlayEngine.FormatSnapshot(latest)}");
                    return new InitialTrackNavigationSamplingResult(
                        latest,
                        latest,
                        latest,
                        sawSessionDrop,
                        Completed: true,
                        ChangeKind: ResolveChangeKind(context, latest));
                }

                if (TryResolveCommittedBrowserWinner(context, latest, out SessionSnapshot immediateCommittedBrowserWinner))
                {
                    LogTrackNavigationRecoveryPhase(
                        "initial-preferred-source-sampling",
                        context,
                        $"completed-browser-convergence latest={MediaOverlayEngine.FormatSnapshot(latest)} resolved={MediaOverlayEngine.FormatSnapshot(immediateCommittedBrowserWinner)}");
                    return new InitialTrackNavigationSamplingResult(
                        latest,
                        immediateCommittedBrowserWinner,
                        immediateCommittedBrowserWinner,
                        sawSessionDrop,
                        Completed: true,
                        ChangeKind: ResolveChangeKind(context, immediateCommittedBrowserWinner));
                }

                if (TryAcceptStableRepeatedChangedCandidate(context, lastStableChangedCandidate, latest))
                {
                    LogTrackNavigationRecoveryPhase(
                        "initial-preferred-source-sampling",
                        context,
                        $"completed-stable-repeat latest={MediaOverlayEngine.FormatSnapshot(latest)}");
                    return new InitialTrackNavigationSamplingResult(
                        latest,
                        latest,
                        latest,
                        sawSessionDrop,
                        Completed: true,
                        ChangeKind: ResolveChangeKind(context, latest));
                }

                lastStableChangedCandidate = IsStableRepeatedChangedCandidateEligible(context, latest)
                    ? latest
                    : SessionSnapshot.Empty;
            }

            if (!MediaOverlayEngine.IsSessionMissing(lastConvergedBrowserWinner))
            {
                LogTrackNavigationRecoveryPhase(
                    "initial-preferred-source-sampling",
                    context,
                    $"completed-from-last-converged-browser-winner latest={MediaOverlayEngine.FormatSnapshot(latest)} resolved={MediaOverlayEngine.FormatSnapshot(lastConvergedBrowserWinner)}");
                return new InitialTrackNavigationSamplingResult(
                    latest,
                    lastConvergedBrowserWinner,
                    lastConvergedBrowserWinner,
                    sawSessionDrop,
                    Completed: true,
                    ChangeKind: ResolveChangeKind(context, lastConvergedBrowserWinner));
            }

            LogTrackNavigationRecoveryPhase(
                "initial-preferred-source-sampling",
                context,
                $"completed-without-transition latest={MediaOverlayEngine.FormatSnapshot(latest)} fallback={MediaOverlayEngine.FormatSnapshot(!MediaOverlayEngine.IsSessionMissing(lastWithTrackData) ? lastWithTrackData : latest)}");
            return new InitialTrackNavigationSamplingResult(
                latest,
                !MediaOverlayEngine.IsSessionMissing(lastWithTrackData) ? lastWithTrackData : latest,
                SessionSnapshot.Empty,
                sawSessionDrop,
                Completed: false);
        }

        private async Task<SessionSnapshot> TryResolveTrustworthySignalSnapshotAsync(
            TrackNavigationRecoveryContext context,
            SessionSnapshot latest,
            MediaEventAssistOutcome eventAssistOutcome,
            CancellationToken cancellationToken)
        {
            if (!eventAssistOutcome.ObservedEvent)
            {
                return SessionSnapshot.Empty;
            }

            if (IsTrustworthySameSourceSignalCandidate(context, latest, eventAssistOutcome))
            {
                return latest;
            }

            if (!ShouldProbeCurrentSessionForTrustworthySignal(eventAssistOutcome))
            {
                return SessionSnapshot.Empty;
            }

            SessionSnapshot currentSessionSnapshot = await _captureCurrentSnapshotAsync(
                null,
                context.CommandSequence,
                context.Baseline,
                true,
                cancellationToken);

            return IsTrustworthyCurrentSessionSwitchCandidate(context, currentSessionSnapshot, eventAssistOutcome)
                ? currentSessionSnapshot
                : SessionSnapshot.Empty;
        }

        private int ResolveInitialSettleDelayMs(TrackNavigationRecoveryContext context)
        {
            if (!HasSingleSourceSamplingContext(context, out string? preferredSource))
            {
                return _timingProfile.InitialSettleDelayMs;
            }

            if (MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(preferredSource))
            {
                return Math.Min(_timingProfile.InitialSettleDelayMs, AppConstants.MediaOverlay.BrowserSingleSourceInitialSettleDelayMs);
            }

            if (_hasTrustedTrackNavigationSource(preferredSource))
            {
                return Math.Min(_timingProfile.InitialSettleDelayMs, AppConstants.MediaOverlay.ConfidentSourceInitialSettleDelayMs);
            }

            return Math.Min(_timingProfile.InitialSettleDelayMs, AppConstants.MediaOverlay.SingleSourceInitialSettleDelayMs);
        }

        private static bool HasSingleSourceSamplingContext(
            TrackNavigationRecoveryContext context,
            out string? preferredSource)
        {
            preferredSource = context.PreferredSourceForCommand
                ?? context.EffectiveBaseline.SourceAppUserModelId
                ?? context.Baseline.SourceAppUserModelId;
            if (string.IsNullOrWhiteSpace(preferredSource))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(context.Baseline.SourceAppUserModelId)
                && !string.Equals(context.Baseline.SourceAppUserModelId, preferredSource, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (context.PreCommandSnapshots.Count == 0)
            {
                return MediaOverlayEngine.HasTrackData(context.Baseline)
                    && string.Equals(context.Baseline.SourceAppUserModelId, preferredSource, StringComparison.OrdinalIgnoreCase);
            }

            return context.PreCommandSnapshots.Count == 1
                && context.PreCommandSnapshots.ContainsKey(preferredSource);
        }

        private static bool ShouldProbeCurrentSessionForTrustworthySignal(MediaEventAssistOutcome eventAssistOutcome)
        {
            return eventAssistOutcome.EventKind is MediaEventAssistKind.CurrentSessionChanged
                or MediaEventAssistKind.SessionsChanged;
        }

        private bool TryResolveCommittedBrowserWinner(
            TrackNavigationRecoveryContext context,
            SessionSnapshot candidate,
            out SessionSnapshot committedWinner)
        {
            committedWinner = SessionSnapshot.Empty;

            if (MediaOverlayEngine.IsSessionMissing(candidate)
                || !MediaOverlayEngine.HasTrackData(candidate)
                || MediaOverlayEngine.IsSameTrack(context.Baseline, candidate)
                || !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(context.Baseline, candidate)
                || string.IsNullOrWhiteSpace(candidate.SourceAppUserModelId)
                || !_hasCommittedBrowserConvergenceForSnapshot(candidate))
            {
                return false;
            }

            committedWinner = candidate;
            return true;
        }

        private bool IsTrustworthySameSourceSignalCandidate(
            TrackNavigationRecoveryContext context,
            SessionSnapshot candidate,
            MediaEventAssistOutcome eventAssistOutcome)
        {
            if (MediaOverlayEngine.IsSessionMissing(candidate)
                || !MediaOverlayEngine.HasTrackData(candidate)
                || string.IsNullOrWhiteSpace(candidate.SourceAppUserModelId))
            {
                return false;
            }

            if (!string.Equals(candidate.SourceAppUserModelId, context.PreferredSourceForCommand, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.SourceAppUserModelId, context.Baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(context.Baseline, candidate))
            {
                if (IsTrustworthyCommandTargetBrowserSignalCandidate(context, candidate, eventAssistOutcome))
                {
                    return true;
                }

                if (!IsConfirmedOrCommittedTrackNavigationTransition(context.Command, context.Baseline, candidate))
                {
                    return false;
                }

                if (_hasCommittedBrowserConvergenceForSnapshot(candidate))
                {
                    return true;
                }

                PreferredSourceCandidateResolution resolution = MediaOverlayPreferredSourceCandidateEvaluator.ResolveCandidateResolution(
                    context.Baseline,
                    candidate,
                    allowSingleCandidateMetadataChangeFallback: false,
                    _hasRecentSignalForSource(candidate.SourceAppUserModelId));

                return resolution.IsAccepted;
            }

            if (!IsConfirmedOrCommittedTrackNavigationTransition(context.Command, context.Baseline, candidate))
            {
                return false;
            }

            bool timelineTransition = MediaOverlayEngine.HasTimelineTransition(context.Baseline, candidate);
            bool movedBackward = context.Baseline.PositionSeconds.HasValue
                && candidate.PositionSeconds.HasValue
                && candidate.PositionSeconds.Value < context.Baseline.PositionSeconds.Value;
            return timelineTransition
                || movedBackward
                || eventAssistOutcome.EventKind is MediaEventAssistKind.CurrentSessionChanged
                    or MediaEventAssistKind.SessionsChanged
                    or MediaEventAssistKind.MediaPropertiesChanged
                    or MediaEventAssistKind.PlaybackInfoChanged
                    or MediaEventAssistKind.TimelinePropertiesChanged;
        }

        private static bool IsTrustworthyCommandTargetBrowserSignalCandidate(
            TrackNavigationRecoveryContext context,
            SessionSnapshot candidate,
            MediaEventAssistOutcome eventAssistOutcome)
        {
            return context.PreferredSourceIsCommandTarget
                && eventAssistOutcome.EventKind == MediaEventAssistKind.MediaPropertiesChanged
                && !string.IsNullOrWhiteSpace(eventAssistOutcome.SignaledSourceAppUserModelId)
                && string.Equals(eventAssistOutcome.SignaledSourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.SourceAppUserModelId, context.PreferredSourceForCommand, StringComparison.OrdinalIgnoreCase)
                && !MediaOverlayEngine.IsSameTrack(context.Baseline, candidate)
                && MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(context.Baseline, candidate);
        }

        private static bool IsTrustworthyCurrentSessionSwitchCandidate(
            TrackNavigationRecoveryContext context,
            SessionSnapshot candidate,
            MediaEventAssistOutcome eventAssistOutcome)
        {
            if (!MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(context.Baseline, candidate)
                || string.IsNullOrWhiteSpace(candidate.SourceAppUserModelId))
            {
                return false;
            }

            if (string.Equals(candidate.SourceAppUserModelId, context.Baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.SourceAppUserModelId, context.PreferredSourceForCommand, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool corroboratedSource =
                (!string.IsNullOrWhiteSpace(eventAssistOutcome.SignaledSourceAppUserModelId)
                    && string.Equals(eventAssistOutcome.SignaledSourceAppUserModelId, candidate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
                || MediaOverlaySourceSelector.IsCorroboratedSourceForSampling(
                    candidate.SourceAppUserModelId,
                    context.Baseline,
                    SessionSnapshot.Empty,
                    context.PreCommandSnapshots,
                    includePostCommandMatch: false);

            return corroboratedSource;
        }

        private static void LogTrackNavigationRecoveryPhase(
            string phase,
            TrackNavigationRecoveryContext context,
            string details)
        {
            Logger.Instance?.Trace(
                "MediaOverlayHelper",
                () => $"Track-nav recovery phase={phase} command={context.Command} preferredSource={LogPrivacy.Id(context.PreferredSourceForCommand)} baselineSource={LogPrivacy.Id(context.Baseline.SourceAppUserModelId)} effectiveBaselineSource={LogPrivacy.Id(context.EffectiveBaseline.SourceAppUserModelId)} {details}",
                nameof(CaptureSnapshotWithRetryAsync));
        }

        private static TrackNavigationChangeKind ResolveChangeKind(
            TrackNavigationRecoveryContext context,
            SessionSnapshot snapshot)
        {
            if (IsPreviousTrackReplayTransition(context.Command, context.Baseline, snapshot))
            {
                return TrackNavigationChangeKind.SameTrackRestart;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId)
                && !string.Equals(snapshot.SourceAppUserModelId, context.OriginalBaseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return TrackNavigationChangeKind.SourceSwitched;
            }

            return TrackNavigationChangeKind.TrackChanged;
        }

        private static string DescribeChangeKind(TrackNavigationChangeKind changeKind)
        {
            return changeKind switch
            {
                TrackNavigationChangeKind.SameTrackRestart => "same-track-restart",
                TrackNavigationChangeKind.SourceSwitched => "source-switched",
                _ => "track-changed",
            };
        }

        private static bool IsStableRepeatedChangedCandidateEligible(
            TrackNavigationRecoveryContext context,
            SessionSnapshot snapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot)
                || string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId)
                || MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(snapshot.SourceAppUserModelId)
                || !MediaOverlayEngine.HasTrackData(snapshot)
                || MediaOverlayEngine.IsSameTrack(context.Baseline, snapshot)
                || !string.Equals(snapshot.SourceAppUserModelId, context.EffectiveBaseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return snapshot.PositionSeconds.HasValue
                && snapshot.PositionSeconds.Value <= AppConstants.MediaOverlay.StableRepeatedChangedCandidateNearStartWindowSeconds;
        }

        private static bool TryAcceptStableRepeatedChangedCandidate(
            TrackNavigationRecoveryContext context,
            SessionSnapshot previousCandidate,
            SessionSnapshot latest)
        {
            if (MediaOverlayEngine.IsSessionMissing(previousCandidate)
                || !IsStableRepeatedChangedCandidateEligible(context, latest)
                || !string.Equals(previousCandidate.SourceAppUserModelId, latest.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                || !MediaOverlayEngine.IsSameTrack(previousCandidate, latest))
            {
                return false;
            }

            if (!previousCandidate.PositionSeconds.HasValue || !latest.PositionSeconds.HasValue)
            {
                return false;
            }

            long positionDelta = latest.PositionSeconds.Value - previousCandidate.PositionSeconds.Value;
            return positionDelta >= 0
                && positionDelta <= AppConstants.MediaOverlay.StableRepeatedChangedCandidateMaxForwardAdvanceSeconds
                && MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(context.Baseline, latest);
        }

        private static bool IsStableBaselineRepeatCandidate(
            SessionSnapshot baseline,
            SessionSnapshot latest)
        {
            if (MediaOverlayEngine.IsSessionMissing(latest)
                || MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(latest.SourceAppUserModelId)
                || !MediaOverlayEngine.HasTrackData(baseline)
                || !MediaOverlayEngine.HasTrackData(latest)
                || !MediaOverlayEngine.IsSameTrack(baseline, latest)
                || !string.Equals(baseline.SourceAppUserModelId, latest.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
                || MediaOverlayEngine.HasTimelineTransition(baseline, latest))
            {
                return false;
            }

            if (!baseline.PositionSeconds.HasValue || !latest.PositionSeconds.HasValue)
            {
                return false;
            }

            return latest.PositionSeconds.Value >= baseline.PositionSeconds.Value
                && latest.PositionSeconds.Value - baseline.PositionSeconds.Value <= AppConstants.MediaOverlay.StableRepeatedChangedCandidateMaxForwardAdvanceSeconds;
        }

        private static bool IsEligibleForEarlyUnchangedExit(
            TrackNavigationRecoveryContext context,
            SessionSnapshot fallback,
            bool sawSessionDrop,
            bool usedRecoveredAlternateSource)
        {
            return !sawSessionDrop
                && !usedRecoveredAlternateSource
                && !MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(context.PreferredSourceForCommand ?? context.EffectiveBaseline.SourceAppUserModelId)
                && !MediaOverlayEngine.IsSessionMissing(fallback)
                && MediaOverlayEngine.HasTrackData(fallback)
                && MediaOverlayEngine.IsSameTrack(context.Baseline, fallback)
                && string.Equals(fallback.SourceAppUserModelId, context.EffectiveBaseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        private void RecordDiagnostics(
            string finalPhase,
            string outcome,
            TrackNavigationChangeKind finalChangeKind,
            bool sawSessionDrop,
            bool usedSessionDropRecovery,
            bool usedLateTrackLoadRecovery,
            bool usedRecoveredAlternateSource,
            string finalFallbackClassification,
            BrowserSameSourceCommandSummary sameSourceCommandSummary,
            long commandSequence,
            double elapsedMs)
        {
            _recordDiagnostics?.Invoke(new MediaOverlayTrackNavigationDiagnostics(
                finalPhase,
                outcome,
                DescribeChangeKind(finalChangeKind),
                sawSessionDrop,
                usedSessionDropRecovery,
                usedLateTrackLoadRecovery,
                usedRecoveredAlternateSource,
                finalFallbackClassification,
                sameSourceCommandSummary.ConflictObserved,
                sameSourceCommandSummary.ConflictActive,
                sameSourceCommandSummary.DistinctCandidateCount,
                sameSourceCommandSummary.ActiveRivalCount,
                sameSourceCommandSummary.ReinforcedRivalCount,
                sameSourceCommandSummary.StaleRivalCount,
                commandSequence,
                elapsedMs));
        }

        private bool TryBuildChangedResult(
            TrackNavigationRecoveryContext context,
            SessionSnapshot fallback,
            bool sawSessionDrop,
            bool usedSessionDropRecovery,
            bool usedLateTrackLoadRecovery,
            bool usedRecoveredAlternateSource,
            long recoveryStartTimestamp,
            out SnapshotCaptureResult result)
        {
            result = default;
            if (MediaOverlayEngine.IsSessionMissing(fallback)
                || !MediaOverlayEngine.HasTrackData(fallback)
                || !IsConfirmedOrCommittedTrackNavigationTransition(context.Command, context.Baseline, fallback))
            {
                return false;
            }

            _resetStreakIfChanged(context.Command, context.Baseline, fallback);
            TrackNavigationChangeKind changeKind = ResolveChangeKind(context, fallback);
            string finalPhase = usedLateTrackLoadRecovery
                ? "late-track-load-recovery"
                : usedRecoveredAlternateSource
                ? "unchanged-track-recovery"
                : (usedSessionDropRecovery ? "session-drop-recovery" : "initial-preferred-source-sampling");
            RecordDiagnostics(finalPhase, "changed", changeKind, sawSessionDrop, usedSessionDropRecovery, usedLateTrackLoadRecovery, usedRecoveredAlternateSource, "changed", default, context.CommandSequence, Stopwatch.GetElapsedTime(recoveryStartTimestamp).TotalMilliseconds);
            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                () => $"Recovered media snapshot with observed transition for {context.Command}. sawSessionDrop={sawSessionDrop} baseline={MediaOverlayEngine.FormatSnapshot(context.Baseline)} fallback={MediaOverlayEngine.FormatSnapshot(fallback)}",
                nameof(CaptureSnapshotWithRetryAsync));
            result = new SnapshotCaptureResult(fallback, sawSessionDrop, TrackNavigationRecoveryDisposition.Changed, changeKind, usedRecoveredAlternateSource);
            return true;
        }

        private async Task<UnchangedRecoveryProbeResult> RecoverAfterUnchangedAsync(
            TrackNavigationRecoveryContext context,
            SessionSnapshot fallback,
            CancellationToken cancellationToken)
        {
            bool stableBaselineRepeated = false;
            (int unchangedInitialDelayMs, int unchangedRetryDelayMs) = ResolveUnchangedRecoveryCadence(context);
            UnchangedRecoveryProbeResult recoveredUnchanged = await TryRecoverAfterUnchangedAsync(
                context.PreferredSourceForCommand,
                context.Baseline,
                context.CommandPolicy.AllowSingleCandidateMetadataChangeFallback,
                unchangedInitialDelayMs,
                unchangedRetryDelayMs,
                _timingProfile.UnchangedRecoveryAttempts,
                nameof(TryRecoverAfterUnchangedAsync),
                context.CommandSequence,
                context.DeadlineUtc,
                cancellationToken);
            if (!MediaOverlayEngine.IsSessionMissing(recoveredUnchanged.Snapshot))
            {
                fallback = recoveredUnchanged.Snapshot;
            }
            stableBaselineRepeated = recoveredUnchanged.StableBaselineRepeated;

            if (context.IsInGraceWindow
                && !MediaOverlayEngine.IsSessionMissing(fallback)
                && MediaOverlayEngine.IsSameTrack(context.Baseline, fallback)
                && MediaOverlayEngine.HasTrackData(fallback))
            {
                UnchangedRecoveryProbeResult recoveredGrace = await TryRecoverAfterUnchangedAsync(
                    context.PreferredSourceForCommand,
                    context.Baseline,
                    context.CommandPolicy.AllowSingleCandidateMetadataChangeFallback,
                    _timingProfile.GraceRecoveryInitialDelayMs,
                    _timingProfile.GraceRecoveryRetryDelayMs,
                    _timingProfile.GraceRecoveryAttempts,
                    nameof(CaptureSnapshotWithRetryAsync),
                    context.CommandSequence,
                    context.DeadlineUtc,
                    cancellationToken);

                if (!MediaOverlayEngine.IsSessionMissing(recoveredGrace.Snapshot))
                {
                    fallback = recoveredGrace.Snapshot;
                    stableBaselineRepeated |= recoveredGrace.StableBaselineRepeated;
                }
            }

            if (!MediaOverlayEngine.IsSessionMissing(fallback)
                && MediaOverlayEngine.IsSameTrack(context.Baseline, fallback)
                && MediaOverlayEngine.HasTrackData(fallback)
                && !MediaOverlayEngine.HasTimelineTransition(context.Baseline, fallback))
            {
                UnchangedRecoveryProbeResult recoveredStagnantTrack = await TryRecoverAfterUnchangedAsync(
                    context.PreferredSourceForCommand,
                    context.Baseline,
                    true,
                    _timingProfile.StagnantTrackRecoveryInitialDelayMs,
                    _timingProfile.StagnantTrackRecoveryRetryDelayMs,
                    _timingProfile.StagnantTrackRecoveryAttempts,
                    nameof(CaptureSnapshotWithRetryAsync),
                    context.CommandSequence,
                    context.DeadlineUtc,
                    cancellationToken);

                if (!MediaOverlayEngine.IsSessionMissing(recoveredStagnantTrack.Snapshot))
                {
                    fallback = recoveredStagnantTrack.Snapshot;
                    stableBaselineRepeated |= recoveredStagnantTrack.StableBaselineRepeated;
                }
            }

            return new UnchangedRecoveryProbeResult(fallback, stableBaselineRepeated);
        }

        private (int InitialDelayMs, int RetryDelayMs) ResolveUnchangedRecoveryCadence(TrackNavigationRecoveryContext context)
        {
            bool hasSingleSourceSamplingContext = HasSingleSourceSamplingContext(context, out string? preferredSource);
            return _browserPendingCorroborationStrategy.ResolveUnchangedRecoveryCadence(
                hasSingleSourceSamplingContext,
                preferredSource);
        }

        private async Task<SessionSnapshot> ConfirmChangedAlternateSnapshotAsync(
            TrackNavigationRecoveryContext context,
            SessionSnapshot alternate,
            CancellationToken cancellationToken)
        {
            if (MediaOverlayEngine.IsSessionMissing(alternate)
                || !MediaOverlayEngine.HasTrackData(alternate)
                || string.IsNullOrWhiteSpace(alternate.SourceAppUserModelId))
            {
                return SessionSnapshot.Empty;
            }

            MediaOverlayDelayAssistResult alternateDelayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                _timingProfile.RetryDelayMs,
                alternate.SourceAppUserModelId,
                context.DeadlineUtc,
                context.CommandSequence,
                cancellationToken);
            if (!alternateDelayResult.CompletedWithinBudget)
            {
                return SessionSnapshot.Empty;
            }

            SessionSnapshot confirmed = await _captureCurrentSnapshotAsync(
                alternate.SourceAppUserModelId,
                context.CommandSequence,
                null,
                true,
                cancellationToken);

            if (MediaOverlayEngine.IsSessionMissing(confirmed)
                || !MediaOverlayEngine.HasTrackData(confirmed)
                || !string.Equals(confirmed.SourceAppUserModelId, alternate.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return SessionSnapshot.Empty;
            }

            if (MediaOverlayEngine.IsSameTrack(alternate, confirmed))
            {
                return confirmed;
            }

            if (!MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(context.Baseline, confirmed))
            {
                return SessionSnapshot.Empty;
            }

            context.PreCommandSnapshots.TryGetValue(alternate.SourceAppUserModelId, out SessionSnapshot preCommandAlternate);
            bool nearStart = confirmed.PositionSeconds.HasValue && confirmed.PositionSeconds.Value <= 3;
            bool backwardFromAlternate = alternate.PositionSeconds.HasValue
                && confirmed.PositionSeconds.HasValue
                && confirmed.PositionSeconds.Value < alternate.PositionSeconds.Value;
            bool backwardFromPre = !MediaOverlayEngine.IsSessionMissing(preCommandAlternate)
                && preCommandAlternate.PositionSeconds.HasValue
                && confirmed.PositionSeconds.HasValue
                && confirmed.PositionSeconds.Value < preCommandAlternate.PositionSeconds.Value;
            bool timelineTransitionObserved = MediaOverlayEngine.HasTimelineTransition(alternate, confirmed)
                || (!MediaOverlayEngine.IsSessionMissing(preCommandAlternate)
                    && MediaOverlayEngine.HasTimelineTransition(preCommandAlternate, confirmed));
            bool remainsChangedVsPre = MediaOverlayEngine.IsSessionMissing(preCommandAlternate)
                || !MediaOverlayEngine.IsSameTrack(preCommandAlternate, confirmed)
                || MediaOverlayEngine.HasTimelineTransition(preCommandAlternate, confirmed);

            if (!remainsChangedVsPre)
            {
                return SessionSnapshot.Empty;
            }

            return nearStart || backwardFromAlternate || backwardFromPre || timelineTransitionObserved
                ? confirmed
                : SessionSnapshot.Empty;
        }

        private async Task<UnchangedRecoveryProbeResult> TryRecoverAfterUnchangedAsync(
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            bool allowSingleCandidateMetadataChangeFallback,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            return await TryRecoverAfterUnchangedAsync(
                preferredSourceAppUserModelId,
                baseline,
                allowSingleCandidateMetadataChangeFallback,
                _timingProfile.UnchangedRecoveryInitialDelayMs,
                _timingProfile.UnchangedRecoveryRetryDelayMs,
                _timingProfile.UnchangedRecoveryAttempts,
                nameof(TryRecoverAfterUnchangedAsync),
                commandSequence,
                deadlineUtc,
                cancellationToken);
        }

        private async Task<UnchangedRecoveryProbeResult> TryRecoverAfterUnchangedAsync(
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            bool allowSingleCandidateMetadataChangeFallback,
            int initialDelayMs,
            int retryDelayMs,
            int attempts,
            string operationName,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            MediaOverlayDelayAssistResult unchangedDelayResult = await _delayWithEventAssistIfWithinBudgetAsync(initialDelayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken);
            if (!unchangedDelayResult.CompletedWithinBudget)
            {
                return new UnchangedRecoveryProbeResult(SessionSnapshot.Empty, StableBaselineRepeated: false);
            }

            SessionSnapshot latest = SessionSnapshot.Empty;
            SessionSnapshot lastNonMissing = SessionSnapshot.Empty;
            int stableBaselineRepeatCount = 0;
            bool expediteBrowserCorroborationRetry = false;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    int effectiveRetryDelayMs = MediaOverlayBrowserPendingCorroborationStrategy.ResolveRetryDelayMs(
                        retryDelayMs,
                        expediteBrowserCorroborationRetry);
                    expediteBrowserCorroborationRetry = false;
                    unchangedDelayResult = await _delayWithEventAssistIfWithinBudgetAsync(effectiveRetryDelayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken);
                    if (!unchangedDelayResult.CompletedWithinBudget)
                    {
                        break;
                    }
                }

                latest = await _captureCurrentSnapshotAsync(
                    preferredSourceAppUserModelId,
                    commandSequence,
                    baseline,
                    allowSingleCandidateMetadataChangeFallback,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(latest))
                {
                    lastNonMissing = latest;

                    if (MediaOverlayEngine.HasTrackData(latest) && !MediaOverlayEngine.IsSameTrack(baseline, latest))
                    {
                        Logger.Instance?.Debug(
                            "MediaOverlayHelper",
                            () => $"Recovered updated track metadata after unchanged probe. baseline={MediaOverlayEngine.FormatSnapshot(baseline)} latest={MediaOverlayEngine.FormatSnapshot(latest)}",
                            operationName);
                        return new UnchangedRecoveryProbeResult(latest, StableBaselineRepeated: false);
                    }

                    if (IsStableBaselineRepeatCandidate(baseline, latest))
                    {
                        stableBaselineRepeatCount++;
                    }
                    else
                    {
                        stableBaselineRepeatCount = 0;
                    }

                    expediteBrowserCorroborationRetry = false;
                }
                else
                {
                    expediteBrowserCorroborationRetry = _browserPendingCorroborationStrategy.ShouldExpediteLatePendingRetry(
                        preferredSourceAppUserModelId,
                        baseline,
                        commandSequence);
                }
            }

            SessionSnapshot resolvedSnapshot = !MediaOverlayEngine.IsSessionMissing(lastNonMissing) ? lastNonMissing : latest;
            return new UnchangedRecoveryProbeResult(
                resolvedSnapshot,
                StableBaselineRepeated: stableBaselineRepeatCount >= 2 && IsStableBaselineRepeatCandidate(baseline, resolvedSnapshot));
        }

        private async Task<SessionSnapshot> TryRecoverAfterTrackLoadAsync(
            MediaOverlayCommand command,
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            bool allowSingleCandidateMetadataChangeFallback,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            TrackLoadRecoveryResult recovery = await TryRecoverAfterTrackLoadDetailedAsync(
                command,
                preferredSourceAppUserModelId,
                baseline,
                allowSingleCandidateMetadataChangeFallback,
                _timingProfile.TrackLoadRecoveryInitialDelayMs,
                _timingProfile.TrackLoadRecoveryRetryDelayMs,
                _timingProfile.TrackLoadRecoveryAttempts,
                nameof(TryRecoverAfterTrackLoadAsync),
                commandSequence,
                deadlineUtc,
                cancellationToken);

            return recovery.Snapshot;
        }

        private async Task<TrackLoadRecoveryResult> TryRecoverAfterTrackLoadDetailedAsync(
            MediaOverlayCommand command,
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            bool allowSingleCandidateMetadataChangeFallback,
            int initialDelayMs,
            int retryDelayMs,
            int attempts,
            string operationName,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            MediaOverlayDelayAssistResult trackLoadDelayResult = await _delayWithEventAssistIfWithinBudgetAsync(initialDelayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken);
            if (!trackLoadDelayResult.CompletedWithinBudget)
            {
                return new TrackLoadRecoveryResult(SessionSnapshot.Empty, Attempted: false, EndedByDeadline: true);
            }

            SessionSnapshot latest = SessionSnapshot.Empty;
            SessionSnapshot lastNonMissing = SessionSnapshot.Empty;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    trackLoadDelayResult = await _delayWithEventAssistIfWithinBudgetAsync(retryDelayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken);
                    if (!trackLoadDelayResult.CompletedWithinBudget)
                    {
                        return new TrackLoadRecoveryResult(!MediaOverlayEngine.IsSessionMissing(lastNonMissing) ? lastNonMissing : latest, Attempted: true, EndedByDeadline: true);
                    }
                }

                latest = await _captureCurrentSnapshotAsync(
                    preferredSourceAppUserModelId,
                    commandSequence,
                    baseline,
                    allowSingleCandidateMetadataChangeFallback,
                    cancellationToken);
                if (!MediaOverlayEngine.IsSessionMissing(latest))
                {
                    lastNonMissing = latest;

                    if (IsConfirmedOrCommittedTrackNavigationTransition(command, baseline, latest))
                    {
                        Logger.Instance?.Debug(
                            "MediaOverlayHelper",
                            () => $"Recovered track metadata after post-command load wait. baseline={MediaOverlayEngine.FormatSnapshot(baseline)} latest={MediaOverlayEngine.FormatSnapshot(latest)}",
                            operationName);
                        return new TrackLoadRecoveryResult(latest, Attempted: true, EndedByDeadline: false);
                    }
                }
            }

            return new TrackLoadRecoveryResult(!MediaOverlayEngine.IsSessionMissing(lastNonMissing) ? lastNonMissing : latest, Attempted: true, EndedByDeadline: false);
        }

        private async Task<SessionSnapshot> TryRecoverChangedTrackUntilDeadlineAsync(
            MediaOverlayCommand command,
            string? preferredSourceAppUserModelId,
            SessionSnapshot baseline,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
        {
            SessionSnapshot latest = SessionSnapshot.Empty;
            while (_utcNow() < deadlineUtc)
            {
                MediaOverlayDelayAssistResult lateRecoveryDelayResult = await _delayWithEventAssistIfWithinBudgetAsync(
                    _timingProfile.SessionDropTrackLoadRecoveryRetryDelayMs,
                    preferredSourceAppUserModelId,
                    deadlineUtc,
                    commandSequence,
                    cancellationToken);
                if (!lateRecoveryDelayResult.CompletedWithinBudget)
                {
                    break;
                }

                latest = await _captureCurrentSnapshotAsync(
                    preferredSourceAppUserModelId,
                    commandSequence,
                    baseline,
                    true,
                    cancellationToken);
                if (IsConfirmedOrCommittedTrackNavigationTransition(command, baseline, latest))
                {
                    Logger.Instance?.Debug(
                        "MediaOverlayHelper",
                        () => $"Recovered updated track metadata during late post-drop wait. baseline={MediaOverlayEngine.FormatSnapshot(baseline)} latest={MediaOverlayEngine.FormatSnapshot(latest)}",
                        nameof(CaptureSnapshotWithRetryAsync));
                    return latest;
                }

                if (_browserPendingCorroborationStrategy.ShouldAbortConflictedBrowserRecoveryEarly(
                    preferredSourceAppUserModelId,
                    baseline,
                    commandSequence))
                {
                    break;
                }
            }

            return SessionSnapshot.Empty;
        }

        internal static string BuildUnresolvedSameSourceConflictLog(
            string? recoverySource,
            BrowserSameSourceCommandSummary summary)
        {
            return $"same-source conflict unresolved: source={LogPrivacy.Id(recoverySource)} rivals={summary.RivalReasonClasses} activeRivals={summary.ActiveRivalCount} reinforcedRivals={summary.ReinforcedRivalCount} staleRivals={summary.StaleRivalCount} winnerPresent={summary.WinnerElection.HasWinner}";
        }

        private bool HasUsefulData(MediaOverlayCommand command, SessionSnapshot baseline, SessionSnapshot latest)
        {
            return IsConfirmedOrCommittedTrackNavigationTransition(command, baseline, latest);
        }

        private bool IsConfirmedOrCommittedTrackNavigationTransition(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot latest)
        {
            if (MediaOverlayEngine.IsSessionMissing(latest)
                || !MediaOverlayEngine.HasTrackData(latest))
            {
                return false;
            }

            if (!MediaOverlayEngine.IsSameTrack(baseline, latest)
                && MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSameSource(baseline, latest))
            {
                return _hasCommittedBrowserConvergenceForSnapshot(latest);
            }

            return HasConfirmedTrackNavigationTransition(command, baseline, latest);
        }

        private static bool HasConfirmedTrackNavigationTransition(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot latest)
        {
            if (MediaOverlayEngine.IsSessionMissing(latest)
                || !MediaOverlayEngine.HasTrackData(latest))
            {
                return false;
            }

            if (!MediaOverlayEngine.IsSameTrack(baseline, latest))
            {
                return MediaOverlayTrackNavigationRecoveryPolicy.IsUsableTrackNavigationCandidate(baseline, latest);
            }

            return IsPreviousTrackReplayTransition(command, baseline, latest);
        }

        private static bool IsPreviousTrackReplayTransition(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot latest)
        {
            if (command != MediaOverlayCommand.PreviousTrack
                || !MediaOverlayEngine.HasTrackData(baseline)
                || !MediaOverlayEngine.HasTrackData(latest)
                || !MediaOverlayEngine.IsSameTrack(baseline, latest)
                || !baseline.PositionSeconds.HasValue
                || !latest.PositionSeconds.HasValue)
            {
                return false;
            }

            long baselinePosition = baseline.PositionSeconds.Value;
            long latestPosition = latest.PositionSeconds.Value;
            return baselinePosition >= 2
                && latestPosition <= AppConstants.MediaOverlay.TimelineResetToSeconds
                && latestPosition < baselinePosition;
        }

        private static bool ShouldProbeForLateTrackLoadAfterSessionDrop(
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            bool sawSessionDrop)
        {
            return MediaOverlayTrackNavigationRecoveryPolicy.IsSameTrackAtStartAfterSessionDrop(baseline, fallback, sawSessionDrop);
        }
    }
}
