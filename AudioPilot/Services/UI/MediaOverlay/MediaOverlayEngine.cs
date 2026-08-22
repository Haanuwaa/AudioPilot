using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.Media.Control;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed partial class MediaOverlayEngine
    {
        private readonly MediaOverlayStateStore _state = new();
        private readonly MediaOverlayCommandSnapshotCache _commandSnapshotCache = new();
        private readonly MediaOverlaySessionTrackingCoordinator _sessionTracker;
        private readonly MediaOverlaySourceMemoryFacade _sourceMemory;
        private readonly MediaOverlayPreferredSourceResolver _preferredSourceResolver;
        private readonly MediaOverlayPlayPauseResolver _playPauseResolver;
        private readonly MediaOverlayTrackNavigationRecoveryCoordinator _trackNavigationRecoveryCoordinator;
        private readonly MediaOverlayTimingProfile _timingProfile;
        private readonly Func<string?, long, CancellationToken, Task<SessionSnapshot>>? _currentSnapshotOverride;
        private readonly Func<long, CancellationToken, Task<Dictionary<string, SessionSnapshot>>>? _snapshotsBySourceOverride;
        private readonly Func<long, CancellationToken, Task<List<SessionSnapshot>>>? _sessionSnapshotsOverride;
        private readonly Func<string?, int, long, CancellationToken, Task<MediaEventAssistOutcome>>? _eventWaitOverride;
        private readonly Action<MediaOverlayTrackNavigationDiagnostics>? _trackNavigationDiagnosticsSink;
        private readonly Action<MediaOverlayPlayPauseDiagnostics>? _playPauseDiagnosticsSink;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly bool _usesExternalClock;
        private readonly AsyncLocal<MediaOverlayCommandDiagnosticsCapture?> _activeCommandDiagnosticsCapture = new();
        private readonly AsyncLocal<MediaOverlayCommandObservability?> _activeCommandObservability = new();
        private readonly Lock _eventWaiterLock = new();
        private readonly Dictionary<long, GsmtcEventWaiterRegistration> _eventWaitersByCommandSequence = [];
        public MediaOverlayEngine()
            : this(null, null, null)
        {
        }

        internal MediaOverlayEngine(
            Func<string?, long, CancellationToken, Task<SessionSnapshot>>? currentSnapshotOverride,
            Func<long, CancellationToken, Task<Dictionary<string, SessionSnapshot>>>? snapshotsBySourceOverride,
            Func<long, CancellationToken, Task<List<SessionSnapshot>>>? sessionSnapshotsOverride,
            Func<string?, int, long, CancellationToken, Task<MediaEventAssistOutcome>>? eventWaitOverride = null,
            MediaOverlayTimingProfile? timingProfile = null,
            Action<MediaOverlayTrackNavigationDiagnostics>? trackNavigationDiagnosticsSink = null,
            Action<MediaOverlayPlayPauseDiagnostics>? playPauseDiagnosticsSink = null,
            Func<DateTimeOffset>? utcNow = null)
        {
            _currentSnapshotOverride = currentSnapshotOverride;
            _snapshotsBySourceOverride = snapshotsBySourceOverride;
            _sessionSnapshotsOverride = sessionSnapshotsOverride;
            _eventWaitOverride = eventWaitOverride;
            _timingProfile = timingProfile ?? MediaOverlayTimingProfile.Default;
            _trackNavigationDiagnosticsSink = trackNavigationDiagnosticsSink;
            _playPauseDiagnosticsSink = playPauseDiagnosticsSink;
            _usesExternalClock = utcNow != null;
            _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
            _sessionTracker = new MediaOverlaySessionTrackingCoordinator(_state, _utcNow);
            _sourceMemory = new MediaOverlaySourceMemoryFacade(_state);
            _preferredSourceResolver = new MediaOverlayPreferredSourceResolver(
                _sourceMemory.HasRecentSignalForSource,
                new MediaOverlayPreferredSourceTraceLimiter(),
                RecordPreferredSourceObservation,
                TryBuildSnapshotAsync);
            _playPauseResolver = new MediaOverlayPlayPauseResolver(
                (delayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken) =>
                    DelayWithEventAssistOutcomeIfWithinBudgetAsync(
                        delayMs,
                        preferredSourceAppUserModelId,
                        deadlineUtc,
                        commandSequence,
                        cancellationToken),
                (preferredSourceAppUserModelId, commandSequence, preferredReferenceSnapshot, allowSingleCandidateMetadataChangeFallback, cancellationToken) =>
                    TryGetCurrentSnapshotAsync(
                        preferredSourceAppUserModelId,
                        commandSequence,
                        preferredReferenceSnapshot,
                        allowSingleCandidateMetadataChangeFallback,
                        cancellationToken),
                _timingProfile);
            _trackNavigationRecoveryCoordinator = new MediaOverlayTrackNavigationRecoveryCoordinator(
                (delayMs, preferredSourceAppUserModelId, deadlineUtc, commandSequence, cancellationToken) =>
                    DelayWithEventAssistOutcomeIfWithinBudgetAsync(
                        delayMs,
                        preferredSourceAppUserModelId,
                        deadlineUtc,
                        commandSequence,
                        cancellationToken),
                (preferredSourceAppUserModelId, commandSequence, preferredReferenceSnapshot, allowSingleCandidateMetadataChangeFallback, cancellationToken) =>
                    TryGetCurrentSnapshotAsync(
                        preferredSourceAppUserModelId,
                        commandSequence,
                        preferredReferenceSnapshot,
                        allowSingleCandidateMetadataChangeFallback,
                        cancellationToken),
                (baseline, preferredFallback, preCommandSnapshots, forceAlternateAfterStreak, preferredSourceForCommand, commandSequence, cancellationToken) =>
                    TryFindChangedAlternateSnapshotAsync(
                        baseline,
                        preferredFallback,
                        preCommandSnapshots,
                        forceAlternateAfterStreak,
                        preferredSourceForCommand,
                        commandSequence,
                        cancellationToken),
                EvaluateTrackNavigationStreakDecision,
                ResetStreakIfChanged,
                HasRecentSignalForSource,
                HasTrustedTrackNavigationSource,
                _preferredSourceResolver.GetBrowserSameSourceCommandSummary,
                snapshot => _activeCommandObservability.Value?.HasCommittedBrowserConvergenceForSnapshot(snapshot) == true,
                RecordTrackNavigationDiagnostics,
                _timingProfile,
                _utcNow);
        }

        public Task<MediaOverlayResult> SendWithBestEffortOverlayAsync(MediaOverlayCommand command, Func<bool> sendCommand)
        {
            ArgumentNullException.ThrowIfNull(sendCommand);

            return SendWithBestEffortOverlayAsync(command, () => Task.FromResult(sendCommand()));
        }

        public async Task<MediaOverlayResult> SendWithBestEffortOverlayAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync)
        {
            MediaOverlayCommandResult result = await SendWithDetailedResultAsync(command, sendCommandAsync);
            return result.Overlay;
        }

        internal Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(MediaOverlayCommand command, Func<bool> sendCommand)
        {
            ArgumentNullException.ThrowIfNull(sendCommand);

            return SendWithDetailedResultAsync(command, () => Task.FromResult(sendCommand()));
        }

        internal async Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(MediaOverlayCommand command, Func<Task<bool>> sendCommandAsync)
        {
            return await SendWithDetailedResultAsync(command, sendCommandAsync, commandTargetSourceProvider: null);
        }

        internal async Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            Func<string?>? commandTargetSourceProvider)
        {
            return await SendWithDetailedResultAsync(
                command,
                sendCommandAsync,
                commandTargetSourceProvider,
                CancellationToken.None);
        }

        internal async Task<MediaOverlayCommandResult> SendWithDetailedResultAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            Func<string?>? commandTargetSourceProvider,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sendCommandAsync);

            var capture = new MediaOverlayCommandDiagnosticsCapture();
            var observability = new MediaOverlayCommandObservability();
            MediaOverlayCommandDiagnosticsCapture? previousCapture = _activeCommandDiagnosticsCapture.Value;
            MediaOverlayCommandObservability? previousObservability = _activeCommandObservability.Value;
            _activeCommandDiagnosticsCapture.Value = capture;
            _activeCommandObservability.Value = observability;

            try
            {
                MediaOverlayResult overlay = await SendWithBestEffortOverlayCoreAsync(
                    command,
                    sendCommandAsync,
                    commandTargetSourceProvider,
                    cancellationToken);
                return MediaOverlayCommandResult.From(
                    command,
                    overlay,
                    capture.TrackNavigationDiagnostics,
                    capture.PlayPauseDiagnostics,
                    capture.DiagCodeOverride);
            }
            finally
            {
                _activeCommandDiagnosticsCapture.Value = previousCapture;
                _activeCommandObservability.Value = previousObservability;
            }
        }

        private async Task<MediaOverlayResult> SendWithBestEffortOverlayCoreAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            Func<string?>? commandTargetSourceProvider,
            CancellationToken callerCancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sendCommandAsync);

            long commandSequence = _sessionTracker.BeginCommand();
            long commandStartTimestamp = Stopwatch.GetTimestamp();
            DateTimeOffset deadlineUtc = _utcNow().AddMilliseconds(_timingProfile.MaxCaptureDurationMs);
            string? preferredSourceForCleanup = null;

            // The logical deadline stops recovery polling. Keep hard cancellation slightly later so
            // deadline-driven loops can classify their final snapshot instead of racing cancellation.
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
            if (!_usesExternalClock)
            {
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(
                    _timingProfile.MaxCaptureDurationMs + AppConstants.MediaOverlay.CaptureCancellationGraceMs));
            }

            CancellationToken cancellationToken = timeoutCts.Token;

            try
            {
                ThrowIfSuperseded(commandSequence, cancellationToken);

                return command == MediaOverlayCommand.PlayPause
                    ? await ResolvePlayPauseOverlayAsync(
                        command,
                        sendCommandAsync,
                        commandSequence,
                        commandStartTimestamp,
                        deadlineUtc,
                        source => preferredSourceForCleanup = source,
                        cancellationToken)
                    : await ResolveTrackNavigationOverlayAsync(
                        command,
                        sendCommandAsync,
                        commandTargetSourceProvider,
                        commandSequence,
                        deadlineUtc,
                        source => preferredSourceForCleanup = source,
                        cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RecordOverlayTelemetry(MediaOverlayTelemetryEvent.HiddenCanceled, MediaOverlayTelemetryOutcomeClass.None);
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"media-overlay-capture-canceled | command={command}",
                    nameof(SendWithBestEffortOverlayAsync));
                SetDetailedDiagCodeOverride("media-overlay-canceled");
                return MediaOverlayResult.Hidden;
            }
            finally
            {
                ClearGsmtcEventWaiter(commandSequence);
                _preferredSourceResolver.ClearCommandState(preferredSourceForCleanup, commandSequence);
                _commandSnapshotCache.Clear(commandSequence);
            }
        }

        private async Task<MediaOverlayResult> ResolvePlayPauseOverlayAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            long commandSequence,
            long commandStartTimestamp,
            DateTimeOffset deadlineUtc,
            Action<string?> setPreferredSourceForCleanup,
            CancellationToken cancellationToken)
        {
            SessionSnapshot baselinePlayPause = await TryGetCurrentSnapshotAsync(commandSequence, cancellationToken);
            string playPauseCommandGroupKey = GetCommandGroupKey(command);
            string? playPauseStickySource = TryGetStickySource(playPauseCommandGroupKey);
            Dictionary<string, SessionSnapshot> playPausePreCommandSnapshots = await TryGetSnapshotsBySourceAsync(commandSequence, cancellationToken);
            string? validatedPlayPauseStickySource = MediaOverlaySourceSelector.ValidateStickySourceForPlayPauseSampling(
                playPauseStickySource,
                baselinePlayPause,
                playPausePreCommandSnapshots);
            if (!await TrySendCommandAsync(sendCommandAsync, command).ConfigureAwait(false))
            {
                SetDetailedDiagCodeOverride("media-command-send-failed");
                return BuildCommandSendFailureResult(command);
            }

            ThrowIfSuperseded(commandSequence, cancellationToken);
            _commandSnapshotCache.InvalidateSnapshots(commandSequence);

            Dictionary<string, SessionSnapshot> immediatePostSnapshotsBySource = await TryGetSnapshotsBySourceAsync(commandSequence, cancellationToken);
            if (MediaOverlayPlayPauseResolver.TryResolveChangedPlayPauseSnapshot(
                playPausePreCommandSnapshots,
                immediatePostSnapshotsBySource,
                baselinePlayPause.SourceAppUserModelId,
                validatedPlayPauseStickySource,
                out PlayPauseSnapshotResolution immediatePlayPauseResolution))
            {
                if (!IsSessionMissing(immediatePlayPauseResolution.Snapshot)
                    && !string.IsNullOrWhiteSpace(immediatePlayPauseResolution.Snapshot.SourceAppUserModelId))
                {
                    UpdateStickySource(playPauseCommandGroupKey, immediatePlayPauseResolution.Snapshot.SourceAppUserModelId!);
                }

                MediaOverlayResult immediateResolutionResult = MediaOverlayMessageFormatter.BuildPlayPauseMessage(
                    immediatePlayPauseResolution.Snapshot,
                    immediatePlayPauseResolution.Baseline);
                EmitPlayPauseDiagnostics(new MediaOverlayPlayPauseDiagnostics(
                    FinalPath: "changed-by-source-snapshots",
                    Outcome: "changed",
                    UsedEventAssist: false,
                    UsedChangedBySourceSnapshots: true,
                    UsedImmediateCurrentEvidence: false,
                    ReusedBaselineMetadata: false,
                    CommandSequence: commandSequence,
                    ElapsedMs: Stopwatch.GetElapsedTime(commandStartTimestamp).TotalMilliseconds));
                RecordOverlayTelemetry(
                    MapTelemetryEvent(immediateResolutionResult, hiddenNoSession: false, hiddenCanceled: false),
                    MediaOverlayTelemetryOutcomeClass.DirectChange);
                return immediateResolutionResult;
            }

            string? preferredPlayPauseSource = MediaOverlaySourceSelector.SelectPreferredSourceForPlayPauseSampling(
                baselinePlayPause.SourceAppUserModelId,
                validatedPlayPauseStickySource);
            setPreferredSourceForCleanup(preferredPlayPauseSource);

            SessionSnapshot immediatePostPlayPause = await TryGetCurrentSnapshotAsync(
                preferredPlayPauseSource,
                commandSequence,
                baselinePlayPause,
                allowSingleCandidateMetadataChangeFallback: true,
                cancellationToken);
            if (IsSessionMissing(baselinePlayPause) && IsSessionMissing(immediatePostPlayPause))
            {
                RecordOverlayTelemetry(MediaOverlayTelemetryEvent.HiddenNoSession, MediaOverlayTelemetryOutcomeClass.None);
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    "media-overlay-playpause-hidden | reason=no-session-context",
                    nameof(SendWithBestEffortOverlayAsync));
                EmitPlayPauseDiagnostics(new MediaOverlayPlayPauseDiagnostics(
                    FinalPath: "no-session-context",
                    Outcome: "hidden",
                    UsedEventAssist: false,
                    UsedChangedBySourceSnapshots: false,
                    UsedImmediateCurrentEvidence: true,
                    ReusedBaselineMetadata: false,
                    CommandSequence: commandSequence,
                    ElapsedMs: Stopwatch.GetElapsedTime(commandStartTimestamp).TotalMilliseconds));
                return MediaOverlayResult.Hidden;
            }

            if (!IsSessionMissing(immediatePostPlayPause) && MediaOverlayMessageFormatter.IsSnapshotEvidenceForPlayPause(immediatePostPlayPause, baselinePlayPause))
            {
                MediaOverlayResult immediateResult = MediaOverlayMessageFormatter.BuildPlayPauseMessage(immediatePostPlayPause, baselinePlayPause);
                EmitPlayPauseDiagnostics(new MediaOverlayPlayPauseDiagnostics(
                    FinalPath: "immediate-current-snapshot",
                    Outcome: "changed",
                    UsedEventAssist: false,
                    UsedChangedBySourceSnapshots: false,
                    UsedImmediateCurrentEvidence: true,
                    ReusedBaselineMetadata: false,
                    CommandSequence: commandSequence,
                    ElapsedMs: Stopwatch.GetElapsedTime(commandStartTimestamp).TotalMilliseconds));
                RecordOverlayTelemetry(
                    MapTelemetryEvent(immediateResult, hiddenNoSession: false, hiddenCanceled: false),
                    MediaOverlayTelemetryOutcomeClass.DirectChange);
                return immediateResult;
            }

            MediaOverlayPlayPauseResolutionResult playPauseResolutionResult = await _playPauseResolver.ResolveSnapshotAsync(
                baselinePlayPause,
                playPausePreCommandSnapshots,
                validatedPlayPauseStickySource,
                preferredPlayPauseSource,
                commandSequence,
                deadlineUtc,
                cancellationToken);
            PlayPauseSnapshotResolution resolvedPlayPause = playPauseResolutionResult.Resolution;

            if (!IsSessionMissing(resolvedPlayPause.Snapshot) && !string.IsNullOrWhiteSpace(resolvedPlayPause.Snapshot.SourceAppUserModelId))
            {
                UpdateStickySource(playPauseCommandGroupKey, resolvedPlayPause.Snapshot.SourceAppUserModelId!);
            }

            MediaOverlayResult resolvedResult = MediaOverlayMessageFormatter.BuildPlayPauseMessage(resolvedPlayPause.Snapshot, resolvedPlayPause.Baseline);
            EmitPlayPauseDiagnostics(playPauseResolutionResult.Diagnostics with
            {
                CommandSequence = commandSequence,
                ElapsedMs = Stopwatch.GetElapsedTime(commandStartTimestamp).TotalMilliseconds,
            });
            RecordOverlayTelemetry(
                MapTelemetryEvent(resolvedResult, hiddenNoSession: false, hiddenCanceled: false),
                MediaOverlayTelemetryOutcomeClass.DirectChange);
            return resolvedResult;
        }

        private async Task<MediaOverlayResult> ResolveTrackNavigationOverlayAsync(
            MediaOverlayCommand command,
            Func<Task<bool>> sendCommandAsync,
            Func<string?>? commandTargetSourceProvider,
            long commandSequence,
            DateTimeOffset deadlineUtc,
            Action<string?> setPreferredSourceForCleanup,
            CancellationToken cancellationToken)
        {
            MediaOverlayCommandPolicy commandPolicy = MediaOverlayCommandPolicy.For(command);
            SessionSnapshot baseline = await TryGetCurrentSnapshotAsync(commandSequence, cancellationToken);
            string commandGroupKey = GetCommandGroupKey(command);
            string? stickySource = TryGetStickySource(commandGroupKey);
            string? recoveredSource = TryGetRecoveredSource(commandGroupKey);

            Dictionary<string, SessionSnapshot> preCommandSnapshots = await TryGetSnapshotsBySourceAsync(commandSequence, cancellationToken);
            if (!await TrySendCommandAsync(sendCommandAsync, command).ConfigureAwait(false))
            {
                SetDetailedDiagCodeOverride("media-command-send-failed");
                return BuildCommandSendFailureResult(command);
            }

            ThrowIfSuperseded(commandSequence, cancellationToken);
            _commandSnapshotCache.InvalidateSnapshots(commandSequence);

            SessionSnapshot postCommandCurrent = await TryGetCurrentSnapshotAsync(commandSequence, cancellationToken);
            if (IsSessionMissing(baseline)
                && IsSessionMissing(postCommandCurrent)
                && preCommandSnapshots.Count == 0)
            {
                RecordOverlayTelemetry(MediaOverlayTelemetryEvent.HiddenNoSession, MediaOverlayTelemetryOutcomeClass.None);
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"media-overlay-command-hidden | command={command} reason=no-session-context",
                    nameof(SendWithBestEffortOverlayAsync));
                SetDetailedDiagCodeOverride("media-overlay-no-session");
                return MediaOverlayResult.Hidden;
            }

            string? commandTargetSource = TryResolveCommandTargetSource(
                command,
                commandTargetSourceProvider,
                baseline,
                postCommandCurrent,
                preCommandSnapshots);
            MediaOverlaySourceMemorySelection sourceMemorySelection = ResolveValidatedSourceMemory(
                commandGroupKey,
                stickySource,
                recoveredSource,
                baseline,
                postCommandCurrent,
                preCommandSnapshots,
                commandPolicy.AllowRecoveredSourceOverride);

            string? preferredSourceForCommand = MediaOverlaySourceSelector.SelectPreferredSourceForSampling(
                baseline.SourceAppUserModelId,
                IsUsableForSampling(baseline),
                postCommandCurrent.SourceAppUserModelId,
                IsUsablePlayingSnapshot(postCommandCurrent),
                commandTargetSource,
                sourceMemorySelection.StickySource,
                sourceMemorySelection.RecoveredSource,
                commandPolicy);
            setPreferredSourceForCleanup(preferredSourceForCommand);
            SessionSnapshot effectiveBaseline = MediaOverlaySourceSelector.ResolveEffectiveBaselineForSampling(
                baseline,
                preferredSourceForCommand,
                preCommandSnapshots,
                commandPolicy);

            if (!string.Equals(preferredSourceForCommand, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"media-overlay-sampling-retargeted | command={command} fromSource={LogPrivacy.Id(baseline.SourceAppUserModelId)} toSource={LogPrivacy.Id(preferredSourceForCommand)} commandTarget={LogPrivacy.Id(commandTargetSource)} baseline={FormatSnapshot(baseline)} postCommandCurrent={FormatSnapshot(postCommandCurrent)} sticky={LogPrivacy.Id(sourceMemorySelection.StickySource)}",
                    nameof(SendWithBestEffortOverlayAsync));
            }

            if (!string.Equals(effectiveBaseline.SourceAppUserModelId, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Instance?.Debug(
                    "MediaOverlayHelper",
                    $"media-overlay-baseline-rebased | command={command} fromSource={LogPrivacy.Id(baseline.SourceAppUserModelId)} toSource={LogPrivacy.Id(effectiveBaseline.SourceAppUserModelId)} baseline={FormatSnapshot(baseline)} effectiveBaseline={FormatSnapshot(effectiveBaseline)}",
                    nameof(SendWithBestEffortOverlayAsync));
            }

            bool isInGraceWindow = IsInFirstCommandGraceWindow(commandGroupKey, baseline.SourceAppUserModelId);
            bool preferredSourceIsCommandTarget = !string.IsNullOrWhiteSpace(commandTargetSource)
                && string.Equals(preferredSourceForCommand, commandTargetSource, StringComparison.OrdinalIgnoreCase);

            SnapshotCaptureResult capture = await CaptureSnapshotWithRetryAsync(
                command,
                baseline,
                effectiveBaseline,
                preferredSourceForCommand,
                preCommandSnapshots,
                isInGraceWindow,
                commandSequence,
                deadlineUtc,
                preferredSourceIsCommandTarget,
                cancellationToken);

            if (!IsSessionMissing(capture.Snapshot) && !string.IsNullOrWhiteSpace(capture.Snapshot.SourceAppUserModelId))
            {
                UpdateStickySource(commandGroupKey, capture.Snapshot.SourceAppUserModelId!);
            }

            RecordRecoveredSourceOutcome(command, commandGroupKey, baseline, capture.Snapshot);
            RecordTrustedTrackNavigationSourceOutcome(effectiveBaseline, capture);

            MediaOverlayResult trackResult = MediaOverlayMessageFormatter.BuildOverlayMessage(command, effectiveBaseline, capture);
            if (IsTrackNavigationCommand(command))
            {
                RecordOverlayTelemetry(
                    MapTelemetryEvent(trackResult, hiddenNoSession: false, hiddenCanceled: false),
                    ClassifyTrackTelemetryOutcome(trackResult, capture),
                    trackResult.IsTrackMessage ? capture.ChangeKind : null);
            }

            return trackResult;
        }

        private static string? TryResolveCommandTargetSource(
            MediaOverlayCommand command,
            Func<string?>? commandTargetSourceProvider,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            Dictionary<string, SessionSnapshot> preCommandSnapshots)
        {
            if (commandTargetSourceProvider is null)
            {
                return null;
            }

            string? commandTargetSource;
            try
            {
                commandTargetSource = commandTargetSourceProvider();
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"media-overlay-command-target-source-provider-failed | command={command} error={ex.GetType().Name}",
                    nameof(SendWithBestEffortOverlayAsync));
                return null;
            }

            if (string.IsNullOrWhiteSpace(commandTargetSource))
            {
                return null;
            }

            if (MediaOverlaySourceSelector.IsCorroboratedSourceForSampling(
                    commandTargetSource,
                    baseline,
                    postCommandCurrent,
                    preCommandSnapshots,
                    includePostCommandMatch: true))
            {
                return commandTargetSource;
            }

            Logger.Instance?.Trace(
                "MediaOverlayHelper",
                () => $"media-overlay-command-target-source-ignored | command={command} target={LogPrivacy.Id(commandTargetSource)} reason=not-corroborated baseline={FormatSnapshot(baseline)} postCommandCurrent={FormatSnapshot(postCommandCurrent)} preCommandSources={preCommandSnapshots.Count}",
                nameof(SendWithBestEffortOverlayAsync));
            return null;
        }

        internal static MediaOverlayResult BuildCommandSendFailureResult(MediaOverlayCommand command)
        {
            return MediaOverlayResult.Plain(command switch
            {
                MediaOverlayCommand.PlayPause => "Play/pause failed",
                MediaOverlayCommand.NextTrack => "Next track failed",
                MediaOverlayCommand.PreviousTrack => "Previous track failed",
                _ => "Media command failed",
            });
        }

        internal static async Task<bool> TrySendCommandAsync(Func<Task<bool>> sendCommandAsync, MediaOverlayCommand command)
        {
            ArgumentNullException.ThrowIfNull(sendCommandAsync);

            try
            {
                if (await sendCommandAsync().ConfigureAwait(false))
                {
                    return true;
                }

                Logger.Instance?.Warning(
                    "MediaOverlayHelper",
                    () => $"media-command-send-failed | command={command}",
                    nameof(SendWithBestEffortOverlayAsync));
                return false;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Warning(
                    "MediaOverlayHelper",
                    $"media-command-send-threw | command={command} error={ex.GetType().Name}",
                    nameof(SendWithBestEffortOverlayAsync),
                    ex);
                return false;
            }
        }

        internal static string FormatSnapshot(SessionSnapshot snapshot)
        {
            string title = LogPrivacy.Label(snapshot.Title);
            string artist = LogPrivacy.Label(snapshot.Artist);
            string album = LogPrivacy.Label(snapshot.AlbumTitle);
            string source = LogPrivacy.Id(snapshot.SourceAppUserModelId);
            string position = snapshot.PositionSeconds?.ToString() ?? "<null>";
            return $"title='{title}', artist='{artist}', album='{album}', source='{source}', positionSec='{position}', status='{snapshot.PlaybackStatus}'";
        }

        internal static bool HasTrackData(SessionSnapshot snapshot)
        {
            return !string.IsNullOrWhiteSpace(snapshot.Title)
                || !string.IsNullOrWhiteSpace(snapshot.Artist)
                || !string.IsNullOrWhiteSpace(snapshot.AlbumTitle);
        }

        private static bool IsUsableForSampling(SessionSnapshot snapshot)
        {
            return !IsSessionMissing(snapshot) && HasTrackData(snapshot);
        }

        private static bool IsUsablePlayingSnapshot(SessionSnapshot snapshot)
        {
            return IsUsableForSampling(snapshot)
                && snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }

        private MediaOverlaySourceMemorySelection ResolveValidatedSourceMemory(
            string commandGroupKey,
            string? stickySource,
            string? recoveredSource,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots,
            bool allowRecoveredSourceOverride)
        {
            string? validatedStickySource = _sourceMemory.GetValidatedStickySource(
                commandGroupKey,
                stickySource,
                baseline,
                postCommandCurrent,
                preCommandSnapshots);
            string? validatedRecoveredSource = allowRecoveredSourceOverride
                ? _sourceMemory.GetValidatedRecoveredSource(
                    commandGroupKey,
                    recoveredSource,
                    baseline,
                    postCommandCurrent,
                    preCommandSnapshots,
                    HasTrustedTrackNavigationSource(recoveredSource))
                : null;

            return new MediaOverlaySourceMemorySelection(validatedStickySource, validatedRecoveredSource);
        }

        internal static bool IsSessionMissing(SessionSnapshot snapshot)
        {
            return snapshot.PlaybackStatus == null
                && string.IsNullOrWhiteSpace(snapshot.Title)
                && string.IsNullOrWhiteSpace(snapshot.Artist)
                && string.IsNullOrWhiteSpace(snapshot.AlbumTitle)
                && string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId)
                && !snapshot.PositionSeconds.HasValue;
        }

        private TrackNavigationStreakDecision EvaluateTrackNavigationStreakDecision(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot fallback)
        {
            bool forceAlternateAfterStreak = ShouldForceAlternateAfterStagnantUnchanged(
                command,
                baseline,
                fallback,
                out int unchangedStreak,
                out int stagnantPositionStreak);
            return new TrackNavigationStreakDecision(forceAlternateAfterStreak, unchangedStreak, stagnantPositionStreak);
        }

        private bool ShouldForceAlternateAfterStagnantUnchanged(
            MediaOverlayCommand command,
            SessionSnapshot baseline,
            SessionSnapshot fallback,
            out int unchangedStreak,
            out int stagnantPositionStreak)
        {
            return _sessionTracker.ShouldForceAlternateAfterStagnantUnchanged(
                command,
                baseline,
                fallback,
                out unchangedStreak,
                out stagnantPositionStreak);
        }

        private void ResetStreakIfChanged(MediaOverlayCommand command, SessionSnapshot baseline, SessionSnapshot finalSnapshot)
        {
            _sessionTracker.ResetStreakIfChanged(command, baseline, finalSnapshot);
        }

        internal static bool HasTimelineTransition(SessionSnapshot baseline, SessionSnapshot latest)
        {
            return IsTimelineTransition(
                baseline.PositionSeconds,
                latest.PositionSeconds,
                AppConstants.MediaOverlay.TimelineJumpThresholdSeconds,
                AppConstants.MediaOverlay.TimelineResetFromSeconds,
                AppConstants.MediaOverlay.TimelineResetToSeconds);
        }

        internal static bool IsTimelineTransition(
            long? baselinePositionSeconds,
            long? latestPositionSeconds,
            int jumpThresholdSeconds,
            int resetFromSeconds,
            int resetToSeconds)
        {
            if (!baselinePositionSeconds.HasValue || !latestPositionSeconds.HasValue)
            {
                return false;
            }

            long baselinePosition = baselinePositionSeconds.Value;
            long latestPosition = latestPositionSeconds.Value;
            long delta = latestPosition - baselinePosition;

            if (delta <= -jumpThresholdSeconds)
            {
                return true;
            }

            bool likelySmallRestart = baselinePosition >= resetFromSeconds
                && latestPosition <= resetToSeconds
                && delta < 0;

            return likelySmallRestart;
        }

        private void ThrowIfSuperseded(long commandSequence, CancellationToken cancellationToken)
        {
            _sessionTracker.ThrowIfSuperseded(commandSequence, cancellationToken);
        }

        internal static bool IsSameTrack(SessionSnapshot left, SessionSnapshot right)
        {
            return string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.AlbumTitle, right.AlbumTitle, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.SourceAppUserModelId, right.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCommandGroupKey(MediaOverlayCommand command)
        {
            return command switch
            {
                MediaOverlayCommand.NextTrack or MediaOverlayCommand.PreviousTrack => "track-nav",
                _ => command.ToString(),
            };
        }

        private static bool IsTrackNavigationCommand(MediaOverlayCommand command)
        {
            return command is MediaOverlayCommand.NextTrack or MediaOverlayCommand.PreviousTrack;
        }

        private string? TryGetStickySource(string commandGroupKey)
        {
            return _sourceMemory.TryGetStickySource(commandGroupKey);
        }

        private void UpdateStickySource(string commandGroupKey, string sourceAppUserModelId)
        {
            _sourceMemory.UpdateStickySource(commandGroupKey, sourceAppUserModelId);
        }

        private void ClearStickySource(string commandGroupKey)
        {
            _sourceMemory.ClearStickySource(commandGroupKey);
        }

        private string? TryGetRecoveredSource(string commandGroupKey)
        {
            return _sourceMemory.TryGetRecoveredSource(commandGroupKey);
        }

        private void ClearRecoveredSource(string commandGroupKey)
        {
            _sourceMemory.ClearRecoveredSource(commandGroupKey);
        }

        private void RecordRecoveredSourceOutcome(
            MediaOverlayCommand command,
            string commandGroupKey,
            SessionSnapshot baseline,
            SessionSnapshot finalSnapshot)
        {
            _sourceMemory.RecordRecoveredSourceOutcome(command, commandGroupKey, baseline, finalSnapshot);
        }

        private void RecordTrustedTrackNavigationSourceOutcome(
            SessionSnapshot baseline,
            SnapshotCaptureResult capture)
        {
            if (capture.Outcome != TrackNavigationRecoveryOutcome.Changed
                || string.IsNullOrWhiteSpace(capture.Snapshot.SourceAppUserModelId)
                || MediaOverlayBrowserSameSourcePolicy.IsBrowserLikeSource(capture.Snapshot.SourceAppUserModelId))
            {
                return;
            }

            bool sameSource = string.Equals(
                capture.Snapshot.SourceAppUserModelId,
                baseline.SourceAppUserModelId,
                StringComparison.OrdinalIgnoreCase);
            bool trustworthySourceSwitch = capture.ChangeKind == TrackNavigationChangeKind.SourceSwitched;
            bool sameTrackRestart = capture.ChangeKind == TrackNavigationChangeKind.SameTrackRestart;

            if (sameSource || trustworthySourceSwitch || sameTrackRestart)
            {
                _sourceMemory.RecordTrustedTrackNavigationSource(capture.Snapshot.SourceAppUserModelId);
            }
        }

        private void MarkRecentlySignaledSource(string? sourceAppUserModelId)
        {
            _sourceMemory.RecordRecentSignal(sourceAppUserModelId);
        }

        private bool HasRecentSignalForSource(string? sourceAppUserModelId)
        {
            return _sourceMemory.HasRecentSignalForSource(sourceAppUserModelId);
        }

        private bool HasTrustedTrackNavigationSource(string? sourceAppUserModelId)
        {
            return _sourceMemory.HasTrustedTrackNavigationSource(sourceAppUserModelId);
        }

        private bool IsInFirstCommandGraceWindow(string commandGroupKey, string? currentSourceAppUserModelId)
        {
            return _sourceMemory.IsInFirstCommandGraceWindow(
                commandGroupKey,
                currentSourceAppUserModelId,
                _timingProfile.FirstCommandGraceWindowMs);
        }

        private void TrimStateIfNeeded()
        {
            _sessionTracker.TrimStateIfNeeded();
        }

        private void RecordOverlayTelemetry(
            MediaOverlayTelemetryEvent telemetryEvent,
            MediaOverlayTelemetryOutcomeClass outcomeClass,
            TrackNavigationChangeKind? trackChangeKind = null)
        {
            _sessionTracker.RecordOverlayTelemetry(telemetryEvent, outcomeClass, trackChangeKind);
        }

        private void EmitPlayPauseDiagnostics(MediaOverlayPlayPauseDiagnostics diagnostics)
        {
            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                () => $"media-overlay-playpause-diagnostics | commandSequence={diagnostics.CommandSequence?.ToString() ?? "none"} finalPath={diagnostics.FinalPath} outcome={diagnostics.Outcome} usedEventAssist={diagnostics.UsedEventAssist} changedBySourceSnapshots={diagnostics.UsedChangedBySourceSnapshots} usedImmediateCurrentEvidence={diagnostics.UsedImmediateCurrentEvidence} reusedBaselineMetadata={diagnostics.ReusedBaselineMetadata} elapsedMs={(diagnostics.ElapsedMs.HasValue ? diagnostics.ElapsedMs.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "none")}",
                nameof(SendWithBestEffortOverlayAsync));
            _activeCommandDiagnosticsCapture.Value?.Record(diagnostics);
            _playPauseDiagnosticsSink?.Invoke(diagnostics);
        }

        private void RecordPreferredSourceObservation(MediaOverlayPreferredSourceObservation observation)
        {
            _activeCommandObservability.Value?.RecordPreferredSourceObservation(observation);
        }

        private void RecordTrackNavigationDiagnostics(MediaOverlayTrackNavigationDiagnostics diagnostics)
        {
            Logger.Instance?.Debug(
                "MediaOverlayHelper",
                () => $"media-overlay-tracknav-diagnostics | commandSequence={diagnostics.CommandSequence?.ToString() ?? "none"} finalPhase={diagnostics.FinalPhase} outcome={diagnostics.Outcome} finalChangeKind={diagnostics.FinalChangeKind} sawSessionDrop={diagnostics.SawSessionDrop} usedSessionDropRecovery={diagnostics.UsedSessionDropRecovery} usedLateTrackLoadRecovery={diagnostics.UsedLateTrackLoadRecovery} usedRecoveredAlternateSource={diagnostics.UsedRecoveredAlternateSource} finalFallbackClassification={diagnostics.FinalFallbackClassification} sameSourceConflictObserved={diagnostics.SameSourceConflictObserved} elapsedMs={(diagnostics.ElapsedMs.HasValue ? diagnostics.ElapsedMs.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "none")}",
                nameof(SendWithBestEffortOverlayAsync));
            _activeCommandDiagnosticsCapture.Value?.Record(diagnostics);
            _activeCommandObservability.Value?.RecordTrackNavigationDiagnostics(diagnostics);
            _trackNavigationDiagnosticsSink?.Invoke(diagnostics);
        }

        private void SetDetailedDiagCodeOverride(string diagCode)
        {
            _activeCommandDiagnosticsCapture.Value?.SetDiagCodeOverride(diagCode);
        }

        private MediaOverlayTelemetryOutcomeClass ClassifyTrackTelemetryOutcome(
            MediaOverlayResult result,
            SnapshotCaptureResult capture)
        {
            return _activeCommandObservability.Value?.ClassifyTrackTelemetryOutcome(result, capture)
                ?? MediaOverlayTelemetryOutcomeClass.None;
        }

        internal int BrowserSameSourceCommandStateCountForTests => _preferredSourceResolver.CommandStateCountForTests;

        private sealed class MediaOverlayCommandDiagnosticsCapture
        {
            public MediaOverlayTrackNavigationDiagnostics? TrackNavigationDiagnostics { get; private set; }
            public MediaOverlayPlayPauseDiagnostics? PlayPauseDiagnostics { get; private set; }
            public string? DiagCodeOverride { get; private set; }

            public void Record(MediaOverlayTrackNavigationDiagnostics diagnostics)
            {
                TrackNavigationDiagnostics = diagnostics;
            }

            public void Record(MediaOverlayPlayPauseDiagnostics diagnostics)
            {
                PlayPauseDiagnostics = diagnostics;
            }

            public void SetDiagCodeOverride(string diagCode)
            {
                DiagCodeOverride = diagCode;
            }
        }

        private async Task<SnapshotCaptureResult> CaptureSnapshotWithRetryAsync(
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
            return await _trackNavigationRecoveryCoordinator.CaptureSnapshotWithRetryAsync(
                command: command,
                originalBaseline: originalBaseline,
                baseline: baseline,
                preferredSourceForCommand: preferredSourceForCommand,
                preCommandSnapshots: preCommandSnapshots,
                isInGraceWindow: isInGraceWindow,
                commandSequence: commandSequence,
                deadlineUtc: deadlineUtc,
                preferredSourceIsCommandTarget: preferredSourceIsCommandTarget,
                cancellationToken: cancellationToken);
        }

        private static MediaOverlayTelemetryEvent MapTelemetryEvent(MediaOverlayResult result, bool hiddenNoSession, bool hiddenCanceled)
        {
            if (hiddenNoSession)
            {
                return MediaOverlayTelemetryEvent.HiddenNoSession;
            }

            if (hiddenCanceled)
            {
                return MediaOverlayTelemetryEvent.HiddenCanceled;
            }

            if (result.IsTrackMessage)
            {
                return MediaOverlayTelemetryEvent.TrackShown;
            }

            if (result.IsPlainMessage)
            {
                return MediaOverlayTelemetryEvent.PlainShown;
            }

            return MediaOverlayTelemetryEvent.HiddenOther;
        }
    }
}
