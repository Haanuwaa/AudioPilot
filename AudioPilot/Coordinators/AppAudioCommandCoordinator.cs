using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppAudioCommandCoordinator(
        AudioDeviceService audio,
        OverlayService overlay,
        MediaOverlayCommandService mediaOverlayCommands,
        Logger logger,
        Func<Settings?> currentSettingsProvider,
        Action<ExecutionHistoryEntry>? mediaHistoryRecorder = null,
        Action<AudioMixerMode, string, float, bool>? endpointVolumeApplied = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPlayPauseCommandAsync = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaNextTrackCommandAsync = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPreviousTrackCommandAsync = null,
        int mediaShutdownDrainTimeoutMs = AppConstants.MediaOverlay.ShutdownDrainTimeoutMs,
        MediaSeekService? mediaSeekService = null,
        Func<AudioStatusSnapshot>? audioStatusReader = null,
        MediaPlaybackService? mediaPlaybackService = null,
        TimeProvider? mediaTimeProvider = null)
    {
        private readonly TimeProvider _mediaTimeProvider = mediaTimeProvider ?? TimeProvider.System;
        private int _latestMediaOverlayRequestVersion;
        private readonly Lock _mediaLifecycleLock = new();
        private readonly EndpointVolumeStepGate _volumeStepGate = new();
        private readonly CancellationTokenSource _mediaShutdownCts = new();
        private readonly Dictionary<int, Task> _mediaOperations = [];
        private readonly Lock _mediaOperationsLock = new();
        private Task _mediaSendOrderTail = Task.CompletedTask;
        private int _nextMediaOperationId;
        private int _mediaShutdownStarted;
        private readonly int _mediaShutdownDrainTimeoutMs = Math.Max(1, mediaShutdownDrainTimeoutMs);
        private PendingMediaOverlayCapture? _pendingMediaOverlayCapture;
        private MediaCapture? _activeMediaCapture;
        private MediaFeedbackBatch? _mediaFeedbackBatch;
        private readonly Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> _mediaPlayPauseCommand = mediaPlayPauseCommandAsync ?? MediaKeyHelper.TryPressPlayPauseDetailedAsync;
        private readonly Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> _mediaNextTrackCommand = mediaNextTrackCommandAsync ?? MediaKeyHelper.TryPressNextTrackDetailedAsync;
        private readonly Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> _mediaPreviousTrackCommand = mediaPreviousTrackCommandAsync ?? MediaKeyHelper.TryPressPreviousTrackDetailedAsync;
        private const string CliSource = "cli";
        private const string HotkeySource = "hotkey";

        private enum PendingMediaOverlayCaptureKind
        {
            CommandState,
            CurrentTrack,
        }

        private readonly record struct PendingMediaOverlayCapture(
            PendingMediaOverlayCaptureKind Kind,
            MediaOverlayCommand Command,
            int RequestVersion,
            string Source,
            string? TraceId,
            string? CommandTargetSource,
            Task? PriorSends);

        private sealed class MediaFeedbackBatch
        {
            public int AcceptedSends;
        }

        private sealed class MediaCapture(MediaCommandSendReservation? reservation = null) : IAsyncDisposable
        {
            private readonly CancellationTokenSource _source = new();
            private Task _cancel = Task.CompletedTask;

            public CancellationToken Token => _source.Token;

            public void Cancel(bool keepSend)
            {
                if (keepSend) reservation?.AllowSend();
                if (!_source.IsCancellationRequested) _cancel = _source.CancelAsync();
            }

            public async ValueTask DisposeAsync()
            {
                try { await _cancel.ConfigureAwait(false); }
                finally { _source.Dispose(); }
            }
        }

        private sealed class MediaCommandSendReservation
        {
            private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<MediaKeyHelper.MediaCommandSendOutcome> Completion { get; private set; } =
                Task.FromResult(CreateCanceledMediaSendOutcome());

            public void Attach(Task<MediaKeyHelper.MediaCommandSendOutcome> completion)
            {
                Completion = completion;
            }

            public Task<MediaKeyHelper.MediaCommandSendOutcome> SendAsync()
            {
                AllowSend();
                return Completion;
            }

            public void AllowSend() => _ready.TrySetResult(true);

            public void CancelBeforeSend()
            {
                _ready.TrySetResult(false);
            }

            public Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
            {
                return _ready.Task.WaitAsync(cancellationToken);
            }
        }

        internal bool IsMediaOverlayCaptureInFlightForTests
        {
            get { lock (_mediaLifecycleLock) return _activeMediaCapture != null; }
        }

        public void MediaPlayPause(string source = CliSource)
        {
            StartMediaCommand(MediaOverlayCommand.PlayPause, _mediaPlayPauseCommand, NormalizeMediaCommandSource(source));
        }

        public void MediaNextTrack(string source = CliSource)
        {
            StartMediaCommand(MediaOverlayCommand.NextTrack, _mediaNextTrackCommand, NormalizeMediaCommandSource(source));
        }

        public void MediaPreviousTrack(string source = CliSource)
        {
            StartMediaCommand(MediaOverlayCommand.PreviousTrack, _mediaPreviousTrackCommand, NormalizeMediaCommandSource(source));
        }

        public void ShowCurrentTrack()
        {
            lock (_mediaLifecycleLock)
            {
                if (Volatile.Read(ref _mediaShutdownStarted) != 0 || !overlay.IsOverlayEnabled()) return;

                int requestVersion = BeginMediaFeedbackRequest();
                QueuePendingMediaOverlayCapture(PendingMediaOverlayCaptureKind.CurrentTrack, default, requestVersion, CliSource);
            }
        }

        public async Task ShutdownAsync()
        {
            bool cancelOperations = false;
            lock (_mediaLifecycleLock)
            {
                if (Interlocked.Exchange(ref _mediaShutdownStarted, 1) == 0)
                {
                    cancelOperations = true;
                    _pendingMediaOverlayCapture = null;
                    _mediaFeedbackBatch = null;
                    _activeMediaCapture?.Cancel(keepSend: false);
                }
            }

            if (cancelOperations)
            {
                _mediaShutdownCts.Cancel();
            }

            Task[] operations;
            lock (_mediaOperationsLock)
            {
                operations = [.. _mediaOperations.Values];
            }

            if (operations.Length == 0)
            {
                return;
            }

            Task drainTask = Task.WhenAll(operations);
            using var timerCancellation = new CancellationTokenSource();
            Task completedTask;
            try
            {
                completedTask = await Task.WhenAny(
                    drainTask,
                    Task.Delay(_mediaShutdownDrainTimeoutMs, timerCancellation.Token)).ConfigureAwait(false);
            }
            finally { timerCancellation.Cancel(); }
            if (!ReferenceEquals(completedTask, drainTask))
            {
                logger.Warning(
                    "AppAudioCommandCoordinator",
                    () => $"media-overlay-shutdown-drain-timeout | timeoutMs={_mediaShutdownDrainTimeoutMs} pending={operations.Count(operation => !operation.IsCompleted)}",
                    nameof(ShutdownAsync));
                _ = drainTask.ContinueWith(
                    task => logger.Warning(
                        "AppAudioCommandCoordinator",
                        "media-overlay-shutdown-operation-faulted-after-timeout",
                        nameof(ShutdownAsync),
                        task.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return;
            }

            try
            {
                await drainTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Debug(
                    "AppAudioCommandCoordinator",
                    () => $"media-overlay-shutdown-drain-failed | error={ex.GetType().Name}",
                    nameof(ShutdownAsync));
            }
        }

        public async Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(bool throwOnCaptureFailure = false, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _mediaShutdownCts.Token);
            return await mediaOverlayCommands.GetCurrentMediaSnapshotAsync(throwOnCaptureFailure, linkedCancellation.Token).ConfigureAwait(false);
        }

        public static MediaOverlayResult BuildCurrentMediaOverlayResult(MediaOverlaySessionSnapshot snapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot))
            {
                return MediaOverlayResult.Plain(MediaOverlayMessageFormatter.NoSessionMessage);
            }

            if (!MediaOverlayEngine.HasTrackData(snapshot))
            {
                return MediaOverlayResult.Plain(MediaOverlayMessageFormatter.NoTrackInformationMessage);
            }

            string header = snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                ? "Current track paused"
                : "Current track";
            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown title" : snapshot.Title;

            return MediaOverlayResult.Track(header, title, snapshot.Artist, snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? OverlayIcon.Pause : OverlayIcon.Media);
        }

        public static MediaOverlayResult BuildTrailingMediaOverlayResult(MediaOverlayCommand command, MediaOverlaySessionSnapshot snapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) || !MediaOverlayEngine.HasTrackData(snapshot))
            {
                return MediaOverlayResult.Hidden;
            }

            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown title" : snapshot.Title;
            string header = command switch
            {
                MediaOverlayCommand.NextTrack => "Next track",
                MediaOverlayCommand.PreviousTrack => "Previous track",
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Currently paused",
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Currently playing",
                MediaOverlayCommand.PlayPause => "Play/pause command sent",
                _ => "Media command sent",
            };

            OverlayIcon icon = command switch
            {
                MediaOverlayCommand.NextTrack => OverlayIcon.Next,
                MediaOverlayCommand.PreviousTrack => OverlayIcon.Previous,
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => OverlayIcon.Pause,
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => OverlayIcon.Play,
                _ => OverlayIcon.Information,
            };
            return MediaOverlayResult.Track(header, title, snapshot.Artist, icon);
        }

        public void ShowMuteResult(string target, bool enabled, bool succeeded, bool partial = false)
        {
            if (!succeeded)
            {
                overlay.Show(partial ? "Mute change partially applied" : "Could not change mute state",
                    "Check the device states and try again.");
                return;
            }
            string message = target switch
            {
                "mic" => enabled ? "Microphone muted" : "Microphone unmuted",
                "sound" => enabled ? "Sound muted" : "Sound unmuted",
                "deafen" => enabled ? "Deafened" : "Undeafened",
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
            OverlayIcon icon = target == "mic" ? enabled ? OverlayIcon.MicrophoneMuted : OverlayIcon.Microphone
                : enabled ? OverlayIcon.SpeakerMuted : OverlayIcon.Speaker;
            overlay.Show(enabled ? OverlayActionStateKind.Disabled : OverlayActionStateKind.Enabled, message, icon: icon);
        }

        /// <summary>
        /// Toggles the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This operation reflects endpoint-level listen state and is independent from output/input cycle switching.
        /// </remarks>
        public bool ToggleListenToInput()
        {
            Settings? currentSettings = currentSettingsProvider();
            string preferredMonitorOutputDeviceId = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty;
            string preferredMonitorOutputDeviceName = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty;
            InputListenStateChangeResult result = audio.ToggleCurrentInputListenState(preferredMonitorOutputDeviceId, preferredMonitorOutputDeviceName, currentSettings?.Hotkeys.Listen.MonitorOutputDeviceStableId);
            if (!result.Applied)
            {
                logger.Warning("AppAudioCommandCoordinator", () => $"{AppConstants.Audio.LogEvents.Listen.ToggleFailed} | error={result.Error ?? "unknown"}");
            }

            ShowListenToInputOverlay(result);
            return result.Applied;
        }

        /// <summary>
        /// Sets the Windows "Listen to this device" state for the current default input endpoint.
        /// </summary>
        /// <remarks>
        /// This call is idempotent for CLI use: requesting an already-applied state is treated as success.
        /// </remarks>
        public bool SetListenToInput(bool enabled)
        {
            Settings? currentSettings = currentSettingsProvider();
            string preferredMonitorOutputDeviceId = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceId ?? string.Empty;
            string preferredMonitorOutputDeviceName = currentSettings?.Hotkeys.Listen.MonitorOutputDeviceName ?? string.Empty;
            InputListenStateChangeResult result = audio.SetCurrentInputListenState(preferredMonitorOutputDeviceId, preferredMonitorOutputDeviceName, enabled, currentSettings?.Hotkeys.Listen.MonitorOutputDeviceStableId);
            if (!result.Applied)
            {
                logger.Warning("AppAudioCommandCoordinator", () => $"{AppConstants.Audio.LogEvents.Listen.SetFailed} | target={enabled} error={result.Error ?? "unknown"}");
            }

            ShowListenToInputOverlay(result);
            return result.Applied;
        }

        internal static string GetListenToInputOverlayHeader(bool enabled, bool verified = true)
        {
            string state = enabled ? "Input listen enabled" : "Input listen disabled";
            return verified ? state : $"{state} (verification pending)";
        }

        internal static string NormalizeListenToInputOverlayDeviceName(string? friendlyName)
        {
            return string.IsNullOrWhiteSpace(friendlyName)
                ? "Input device"
                : friendlyName;
        }

        internal static string ComposeListenToInputOverlayDeviceText(bool enabled, string inputDeviceName, string? monitorTargetOutputDeviceName)
        {
            if (!enabled)
            {
                return inputDeviceName;
            }

            string outputTarget = string.IsNullOrWhiteSpace(monitorTargetOutputDeviceName)
                ? "Output device"
                : monitorTargetOutputDeviceName;

            return $"{inputDeviceName}\nTo: {outputTarget}";
        }

        internal static string BuildVolumeOverlayMessage(string label, float resultingPercent)
        {
            int roundedPercent = (int)Math.Round(Math.Clamp(resultingPercent, 0f, 100f), MidpointRounding.AwayFromZero);
            return $"{label} {roundedPercent}%";
        }

        internal static OverlayActionStateKind GetVolumeOverlayStateKind(bool increase)
        {
            return increase ? OverlayActionStateKind.Enabled : OverlayActionStateKind.Disabled;
        }

        private void StartMediaCommand(MediaOverlayCommand command, Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> sendCommandAsync, string source)
        {
            lock (_mediaLifecycleLock)
            {
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                bool captureOverlay = overlay.IsOverlayEnabled();
                int requestVersion = BeginMediaFeedbackRequest(keepBatch: captureOverlay);
                MediaFeedbackBatch? batch = captureOverlay ? _mediaFeedbackBatch ??= new() : null;
                MediaCommandSendReservation sendReservation = ReserveMediaCommandSend(command, sendCommandAsync, batch);
                if (!captureOverlay || _activeMediaCapture != null)
                {
                    TrackMediaOperation(SendMediaCommandWithoutCaptureAsync(command, sendReservation, requestVersion, source, captureOverlay));
                    return;
                }

                var capture = new MediaCapture(sendReservation);
                _activeMediaCapture = capture;
                TrackMediaOperation(ShowMediaOverlayAsync(command, sendReservation, requestVersion, source, capture));
            }
        }

        private async Task SendMediaCommandWithoutCaptureAsync(MediaOverlayCommand command, MediaCommandSendReservation sendReservation, int requestVersion, string source, bool captureOverlay)
        {
            long started = Stopwatch.GetTimestamp();
            MediaKeyHelper.MediaCommandSendOutcome sendOutcome = await sendReservation.SendAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _mediaShutdownStarted) != 0)
            {
                return;
            }

            if (!sendOutcome.Sent)
            {
                MediaOverlayResult failureResult = sendOutcome.IsUnconfirmed
                    ? MediaOverlayResult.Plain("Media command result unknown")
                    : MediaOverlayEngine.BuildCommandSendFailureResult(command);
                bool isLatestRequest = TryCompleteMediaFeedback(requestVersion, out int acceptedSends);
                if (captureOverlay && isLatestRequest && Volatile.Read(ref _mediaShutdownStarted) == 0)
                {
                    ApplyMediaOverlayResult(failureResult, requestVersion);
                }

                RecordMediaCommandHistory(
                    command,
                    failureResult,
                    source,
                    success: false,
                    skipped: !isLatestRequest,
                    diagCode: isLatestRequest ? sendOutcome.IsUnconfirmed ? "media-command-unconfirmed" : "media-command-send-failed" : "media-command-send-failed-stale-suppressed",
                    reason: isLatestRequest ? sendOutcome.IsUnconfirmed ? "Media command result is unknown; automatic retry was suppressed." : "Media command send failed." : "A newer media command superseded this failure overlay.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: BuildMediaSendOutcomeDetails(sendOutcome, new Dictionary<string, string>
                    {
                        ["overlayCapture"] = captureOverlay ? "in-flight" : "disabled",
                        ["fallback"] = "send-only",
                        ["acceptedSends"] = FormatInt(acceptedSends),
                    }));
                return;
            }

            if (!captureOverlay || !overlay.IsOverlayEnabled())
            {
                TryCompleteMediaFeedback(requestVersion, out _);
                RecordMediaCommandHistory(command, MediaOverlayResult.Hidden, source,
                    success: true, skipped: true, diagCode: "media-command-sent-overlay-disabled",
                    reason: "Media command sent; overlay capture is disabled. Playback change was not verified.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: BuildMediaSendOutcomeDetails(sendOutcome, new Dictionary<string, string>
                    {
                        ["overlayCapture"] = "disabled",
                        ["confirmation"] = "unconfirmed",
                    }));
                return;
            }

            QueuePendingMediaOverlayCapture(
                PendingMediaOverlayCaptureKind.CommandState,
                command,
                requestVersion,
                source,
                sendOutcome.CommandTargetSourceAppUserModelId);

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace("AppAudioCommandCoordinator", () => $"media-overlay-capture-deferred | command={command} reason=capture-in-flight");
            }
        }

        private async Task ShowMediaOverlayAsync(MediaOverlayCommand command, MediaCommandSendReservation sendReservation, int requestVersion, string source, MediaCapture capture)
        {
            long started = Stopwatch.GetTimestamp();
            MediaKeyHelper.MediaCommandSendOutcome? sendOutcome = null;
            try
            {
                MediaOverlayCommandResult commandResult = await mediaOverlayCommands.SendWithDetailedResultAsync(
                    command,
                    async () =>
                    {
                        sendOutcome = await sendReservation.SendAsync().ConfigureAwait(false);
                        return sendOutcome.Value.Sent;
                    },
                    () => sendOutcome?.CommandTargetSourceAppUserModelId,
                    capture.Token,
                    () => sendOutcome);
                MediaOverlayResult mediaOverlay = commandResult.Overlay;
                bool unconfirmed = sendOutcome is { IsUnconfirmed: true };
                if (unconfirmed) mediaOverlay = MediaOverlayResult.Plain("Media command result unknown");
                if (capture.Token.IsCancellationRequested || Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                if (!TryCompleteMediaFeedback(requestVersion, out int acceptedSends)) return;

                ApplyMediaOverlayResult(mediaOverlay, requestVersion);
                RecordMediaCommandHistory(
                    command,
                    mediaOverlay,
                    source,
                    success: sendOutcome is not { Sent: false } && !IsCommandSendFailure(command, mediaOverlay),
                    skipped: mediaOverlay.Kind == MediaOverlayResultKind.Hidden,
                    diagCode: unconfirmed ? "media-command-unconfirmed" : commandResult.DiagCode,
                    reason: unconfirmed ? "Media command result is unknown; automatic retry was suppressed." : GetMediaOverlayHistoryReason(command, mediaOverlay),
                    elapsedMs: commandResult.ElapsedMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: BuildMediaCommandDetails(commandResult, sendOutcome, new Dictionary<string, string>
                    {
                        ["acceptedSends"] = FormatInt(acceptedSends),
                    }));
            }
            catch (OperationCanceledException) when (capture.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (capture.Token.IsCancellationRequested || !TryCompleteMediaFeedback(requestVersion, out _)) return;
                logger.Warning("AppAudioCommandCoordinator", "Failed to resolve media session state for overlay", nameof(ShowMediaOverlayAsync), ex);

                RecordMediaCommandHistory(
                    command,
                    MediaOverlayResult.Hidden,
                    source,
                    success: false,
                    skipped: false,
                    diagCode: "media-overlay-resolution-failed",
                    reason: $"Overlay resolution failed with {ex.GetType().Name}.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: new Dictionary<string, string>
                    {
                        ["exceptionType"] = ex.GetType().Name,
                    });
                if (command == MediaOverlayCommand.NextTrack)
                {
                    overlay.Show("Next track unknown", isCurrent: () => IsMediaFeedbackCurrent(requestVersion));
                }
                else if (command == MediaOverlayCommand.PreviousTrack)
                {
                    overlay.Show("Previous track unknown", isCurrent: () => IsMediaFeedbackCurrent(requestVersion));
                }
            }
            finally
            {
                sendReservation.CancelBeforeSend();
                await FinishMediaCaptureAsync(capture).ConfigureAwait(false);
            }
        }

        private MediaCommandSendReservation ReserveMediaCommandSend(
            MediaOverlayCommand command,
            Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> sendCommandAsync,
            MediaFeedbackBatch? batch)
        {
            var reservation = new MediaCommandSendReservation();
            Task predecessor = _mediaSendOrderTail;
            Task<MediaKeyHelper.MediaCommandSendOutcome> completion = ProcessReservedMediaCommandSendAsync(
                predecessor,
                reservation,
                command,
                sendCommandAsync,
                batch,
                _mediaShutdownCts.Token);
            reservation.Attach(completion);
            _mediaSendOrderTail = completion.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return reservation;
        }

        private async Task<MediaKeyHelper.MediaCommandSendOutcome> ProcessReservedMediaCommandSendAsync(
            Task predecessor,
            MediaCommandSendReservation reservation,
            MediaOverlayCommand command,
            Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> sendCommandAsync,
            MediaFeedbackBatch? batch,
            CancellationToken cancellationToken)
        {
            try
            {
                await predecessor.ConfigureAwait(false);
                if (!await reservation.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return CreateCanceledMediaSendOutcome();
                }

                cancellationToken.ThrowIfCancellationRequested();
                _mediaSeekService.InvalidatePosition();
                MediaKeyHelper.MediaCommandSendOutcome outcome = await sendCommandAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!outcome.Sent)
                {
                    logger.Warning(
                        "MediaOverlayHelper",
                        () => $"{(outcome.IsUnconfirmed ? "media-command-outcome-unknown" : "media-command-send-failed")} | command={command} route={outcome.Route} failure={outcome.FailureReason ?? "unknown"}");
                }

                if (outcome.Sent && batch != null) Interlocked.Increment(ref batch.AcceptedSends);
                return outcome;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateCanceledMediaSendOutcome();
            }
            catch (Exception ex)
            {
                logger.Warning(
                    "MediaOverlayHelper",
                    $"media-command-send-threw | command={command} error={ex.GetType().Name}",
                    nameof(ProcessReservedMediaCommandSendAsync),
                    ex);
                return new MediaKeyHelper.MediaCommandSendOutcome(
                    Sent: false,
                    MediaKeyHelper.MediaCommandRouteKind.Delegate,
                    FailureReason: $"delegate-{ex.GetType().Name}");
            }
        }

        private static MediaKeyHelper.MediaCommandSendOutcome CreateCanceledMediaSendOutcome()
        {
            return new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.Delegate,
                FailureReason: "canceled-before-send");
        }

        private void TrackMediaOperation(Task operation)
        {
            int operationId = Interlocked.Increment(ref _nextMediaOperationId);
            lock (_mediaOperationsLock)
            {
                _mediaOperations[operationId] = operation;
            }

            _ = operation.ContinueWith(
                _ =>
                {
                    lock (_mediaOperationsLock)
                    {
                        _mediaOperations.Remove(operationId);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void QueuePendingMediaOverlayCapture(
            PendingMediaOverlayCaptureKind kind,
            MediaOverlayCommand command,
            int requestVersion,
            string source,
            string? commandTargetSource = null)
        {
            lock (_mediaLifecycleLock)
            {
                if (!IsMediaFeedbackCurrent(requestVersion)) return;
                if (!overlay.IsOverlayEnabled())
                {
                    _mediaFeedbackBatch = null;
                    return;
                }
                _pendingMediaOverlayCapture = new(kind, command, requestVersion, source, OperationTrace.CurrentId, commandTargetSource,
                    kind == PendingMediaOverlayCaptureKind.CurrentTrack ? _mediaSendOrderTail : null);
                StartPendingMediaOverlayCaptureIfNeeded();
            }
        }

        private int BeginMediaFeedbackRequest(bool keepBatch = false)
        {
            int version = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
            if (!keepBatch) _mediaFeedbackBatch = null;
            _pendingMediaOverlayCapture = null;
            _activeMediaCapture?.Cancel(keepSend: true);
            return version;
        }

        private bool TryCompleteMediaFeedback(int requestVersion, out int acceptedSends)
        {
            lock (_mediaLifecycleLock)
            {
                acceptedSends = 0;
                if (!IsMediaFeedbackCurrent(requestVersion)) return false;
                if (_mediaFeedbackBatch is { } batch) acceptedSends = Volatile.Read(ref batch.AcceptedSends);
                _mediaFeedbackBatch = null;
                return true;
            }
        }

        private async ValueTask FinishMediaCaptureAsync(MediaCapture capture)
        {
            lock (_mediaLifecycleLock)
            {
                if (ReferenceEquals(_activeMediaCapture, capture))
                {
                    _activeMediaCapture = null;
                    StartPendingMediaOverlayCaptureIfNeeded();
                }
            }

            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { logger.Warning("AppAudioCommandCoordinator", "media-capture-cancellation-failed", nameof(FinishMediaCaptureAsync), ex); }
        }

        // Called with the lifecycle lock held so shutdown cannot miss a newly admitted capture.
        private void StartPendingMediaOverlayCaptureIfNeeded()
        {
            if (_activeMediaCapture != null || _pendingMediaOverlayCapture is not { } pending) return;
            _pendingMediaOverlayCapture = null;
            if (!IsMediaFeedbackCurrent(pending.RequestVersion)) return;
            if (!overlay.IsOverlayEnabled())
            {
                _mediaFeedbackBatch = null;
                return;
            }

            using var traceContext = OperationTrace.Attach(pending.TraceId);
            var capture = new MediaCapture();
            _activeMediaCapture = capture;
            Task captureTask = pending.Kind == PendingMediaOverlayCaptureKind.CurrentTrack
                ? ShowCurrentTrackOverlayAsync(pending.RequestVersion, capture, pending.PriorSends!)
                : ShowCurrentMediaStateOverlayAsync(pending.Command, pending.RequestVersion, pending.Source, pending.CommandTargetSource, capture);
            TrackMediaOperation(captureTask);
        }

        private async Task ShowCurrentMediaStateOverlayAsync(MediaOverlayCommand command, int requestVersion, string source, string? commandTargetSource, MediaCapture capture)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                MediaOverlaySessionSnapshot snapshot = await mediaOverlayCommands.GetCurrentMediaSnapshotAsync(commandTargetSource, capture.Token);
                if (capture.Token.IsCancellationRequested || !TryCompleteMediaFeedback(requestVersion, out int acceptedSends)) return;

                MediaOverlayResult mediaOverlay = BuildTrailingMediaOverlayResult(command, snapshot);
                ApplyMediaOverlayResult(mediaOverlay, requestVersion);
                RecordMediaCommandHistory(
                    command,
                    mediaOverlay,
                    source,
                    success: true,
                    skipped: mediaOverlay.Kind == MediaOverlayResultKind.Hidden,
                    diagCode: mediaOverlay.Kind == MediaOverlayResultKind.TrackMessage
                        ? "media-overlay-trailing-track"
                        : "media-overlay-trailing-no-metadata",
                    reason: mediaOverlay.Kind == MediaOverlayResultKind.Hidden ? "No media metadata was available for the trailing overlay." : null,
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: new Dictionary<string, string>
                    {
                        ["overlayCapture"] = "trailing",
                        ["acceptedSends"] = FormatInt(acceptedSends),
                        ["commandTarget"] = LogPrivacy.Id(commandTargetSource),
                    });
            }
            catch (OperationCanceledException) when (capture.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (!TryCompleteMediaFeedback(requestVersion, out int acceptedSends)) return;
                logger.Warning("AppAudioCommandCoordinator", "Failed to resolve trailing media state for overlay", nameof(ShowCurrentMediaStateOverlayAsync), ex);
                RecordMediaCommandHistory(command, MediaOverlayResult.Hidden, source, success: true, skipped: true,
                    diagCode: "media-overlay-trailing-no-metadata", reason: "Media command sent; trailing metadata could not be read.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: new Dictionary<string, string> { ["acceptedSends"] = FormatInt(acceptedSends) });
            }
            finally
            {
                await FinishMediaCaptureAsync(capture).ConfigureAwait(false);
            }
        }

        private void RecordMediaCommandHistory(MediaOverlayCommand command, MediaOverlayResult result, string source, bool success, bool skipped, string diagCode, string? reason, double elapsedMs, IReadOnlyDictionary<string, string>? details = null)
        {
            if (mediaHistoryRecorder == null)
            {
                return;
            }

            Dictionary<string, string> mergedDetails = new(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = command.ToString(),
                ["resultKind"] = result.Kind.ToString(),
                ["source"] = source,
            };

            if (details != null)
            {
                foreach (KeyValuePair<string, string> detail in details)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && detail.Value != null)
                    {
                        mergedDetails[detail.Key] = detail.Value;
                    }
                }
            }

            string action = command switch
            {
                MediaOverlayCommand.PlayPause => "media-play-pause",
                MediaOverlayCommand.NextTrack => "media-next-track",
                MediaOverlayCommand.PreviousTrack => "media-previous-track",
                _ => "media-command",
            };
            string summary = BuildMediaHistorySummary(command, result, success, skipped);

            mediaHistoryRecorder(new ExecutionHistoryEntry(
                OpId: $"media-{command.ToString().ToLowerInvariant()}:{Guid.NewGuid():N}",
                TimestampUtc: DateTimeOffset.UtcNow,
                Kind: ExecutionHistoryKind.Media,
                Source: source,
                Action: action,
                Success: success,
                Skipped: skipped,
                Summary: summary,
                Reason: reason,
                Target: command.ToString(),
                DiagCode: diagCode,
                ElapsedMs: elapsedMs,
                Details: mergedDetails));
        }

        private static Dictionary<string, string> BuildMediaCommandDetails(
            MediaOverlayCommandResult commandResult,
            MediaKeyHelper.MediaCommandSendOutcome? sendOutcome,
            IReadOnlyDictionary<string, string>? extraDetails = null)
        {
            Dictionary<string, string> details = new(StringComparer.OrdinalIgnoreCase)
            {
                ["commandDiagCode"] = commandResult.DiagCode,
            };

            if (sendOutcome is { } outcome)
            {
                AddMediaSendOutcomeDetails(details, outcome);
            }

            if (commandResult.TrackNavigationDiagnostics is { } trackDiagnostics)
            {
                details["finalPhase"] = trackDiagnostics.FinalPhase;
                if (trackDiagnostics.PhaseElapsedMs.HasValue)
                {
                    details["phaseElapsedMs"] = trackDiagnostics.PhaseElapsedMs.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                }
                details["outcome"] = trackDiagnostics.Outcome;
                details["confirmation"] = trackDiagnostics.Confirmation;
                details["finalChangeKind"] = trackDiagnostics.FinalChangeKind;
                details["finalFallbackClassification"] = trackDiagnostics.FinalFallbackClassification;
                details["sawSessionDrop"] = FormatBool(trackDiagnostics.SawSessionDrop);
                details["usedSessionDropRecovery"] = FormatBool(trackDiagnostics.UsedSessionDropRecovery);
                details["usedLateTrackLoadRecovery"] = FormatBool(trackDiagnostics.UsedLateTrackLoadRecovery);
                details["usedRecoveredAlternateSource"] = FormatBool(trackDiagnostics.UsedRecoveredAlternateSource);
                details["sameSourceConflictObserved"] = FormatBool(trackDiagnostics.SameSourceConflictObserved);
                details["sameSourceConflictActive"] = FormatBool(trackDiagnostics.SameSourceConflictActive);
                details["sameSourceDistinctCandidateCount"] = FormatInt(trackDiagnostics.SameSourceDistinctCandidateCount);
                details["sameSourceActiveRivalCount"] = FormatInt(trackDiagnostics.SameSourceActiveRivalCount);
                details["sameSourceReinforcedRivalCount"] = FormatInt(trackDiagnostics.SameSourceReinforcedRivalCount);
                details["sameSourceStaleRivalCount"] = FormatInt(trackDiagnostics.SameSourceStaleRivalCount);
            }

            if (commandResult.PlayPauseDiagnostics is { } playPauseDiagnostics)
            {
                details["finalPath"] = playPauseDiagnostics.FinalPath;
                details["outcome"] = playPauseDiagnostics.Outcome;
                details["usedEventAssist"] = FormatBool(playPauseDiagnostics.UsedEventAssist);
                details["usedChangedBySourceSnapshots"] = FormatBool(playPauseDiagnostics.UsedChangedBySourceSnapshots);
                details["usedImmediateCurrentEvidence"] = FormatBool(playPauseDiagnostics.UsedImmediateCurrentEvidence);
                details["reusedBaselineMetadata"] = FormatBool(playPauseDiagnostics.ReusedBaselineMetadata);
            }

            if (extraDetails != null)
            {
                foreach (KeyValuePair<string, string> detail in extraDetails)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && detail.Value != null)
                    {
                        details[detail.Key] = detail.Value;
                    }
                }
            }

            return details;
        }

        private static Dictionary<string, string> BuildMediaSendOutcomeDetails(
            MediaKeyHelper.MediaCommandSendOutcome sendOutcome,
            IReadOnlyDictionary<string, string>? extraDetails = null)
        {
            Dictionary<string, string> details = [with(StringComparer.OrdinalIgnoreCase)];
            AddMediaSendOutcomeDetails(details, sendOutcome);

            if (extraDetails != null)
            {
                foreach (KeyValuePair<string, string> detail in extraDetails)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && detail.Value != null)
                    {
                        details[detail.Key] = detail.Value;
                    }
                }
            }

            return details;
        }

        private static void AddMediaSendOutcomeDetails(
            Dictionary<string, string> details,
            MediaKeyHelper.MediaCommandSendOutcome outcome)
        {
            details["sendSent"] = FormatBool(outcome.Sent);
            details["sendRoute"] = outcome.Route.ToString();
            details["sendSuppressFallback"] = FormatBool(outcome.SuppressFallback);
            details["sendUsedInputFallback"] = FormatBool(outcome.UsedSendInputFallback);
            details["sendElapsedMs"] = outcome.ElapsedMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(outcome.CandidateSourceAppUserModelId))
            {
                details["sendCandidateSource"] = LogPrivacy.Id(outcome.CandidateSourceAppUserModelId);
            }

            if (!string.IsNullOrWhiteSpace(outcome.FailureReason))
            {
                details["sendFailureReason"] = outcome.FailureReason;
            }

            if (outcome.ErrorCode.HasValue)
            {
                details["sendErrorCode"] = outcome.ErrorCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string BuildMediaHistorySummary(MediaOverlayCommand command, MediaOverlayResult result, bool success, bool skipped)
        {
            if (result.Message == "Media command result unknown")
            {
                return $"{GetMediaCommandLabel(command)} media command result is unknown.";
            }
            if (!success)
            {
                return $"{GetMediaCommandLabel(command)} media command failed.";
            }

            if (skipped && result.Kind == MediaOverlayResultKind.Hidden)
            {
                return $"{GetMediaCommandLabel(command)} media command sent without an overlay.";
            }

            if (result.Kind == MediaOverlayResultKind.TrackMessage)
            {
                return $"{GetMediaCommandLabel(command)} media command sent; track metadata is available.";
            }

            if (result.Kind == MediaOverlayResultKind.Hidden)
            {
                return $"{GetMediaCommandLabel(command)} media command completed with no visible overlay.";
            }

            return $"{GetMediaCommandLabel(command)} media command completed.";
        }

        private static string GetMediaCommandLabel(MediaOverlayCommand command)
        {
            return command switch
            {
                MediaOverlayCommand.PlayPause => "Play/Pause",
                MediaOverlayCommand.NextTrack => "Next Track",
                MediaOverlayCommand.PreviousTrack => "Previous Track",
                _ => "Media",
            };
        }

        private static string? GetMediaOverlayHistoryReason(MediaOverlayCommand command, MediaOverlayResult result)
        {
            if (IsCommandSendFailure(command, result))
            {
                return "Media command send failed.";
            }

            return result.Kind switch
            {
                MediaOverlayResultKind.Hidden => "No visible media overlay was available.",
                MediaOverlayResultKind.PlainMessage when !string.IsNullOrWhiteSpace(result.Message) => result.Message,
                _ => null,
            };
        }

        private static bool IsCommandSendFailure(MediaOverlayCommand command, MediaOverlayResult result)
        {
            string? message = result.Message;
            if (result.Kind != MediaOverlayResultKind.PlainMessage || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return command switch
            {
                MediaOverlayCommand.PlayPause => string.Equals(message, "Play/pause failed", StringComparison.Ordinal),
                MediaOverlayCommand.NextTrack => string.Equals(message, "Next track failed", StringComparison.Ordinal),
                MediaOverlayCommand.PreviousTrack => string.Equals(message, "Previous track failed", StringComparison.Ordinal),
                _ => string.Equals(message, "Media command failed", StringComparison.Ordinal),
            };
        }

        private static string NormalizeMediaCommandSource(string? source)
        {
            if (string.Equals(source, "taskbar", StringComparison.OrdinalIgnoreCase)) return "taskbar";
            return string.Equals(source, HotkeySource, StringComparison.OrdinalIgnoreCase)
                ? HotkeySource
                : CliSource;
        }

        private static string FormatBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string FormatInt(int value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task ShowCurrentTrackOverlayAsync(int requestVersion, MediaCapture capture, Task priorSends)
        {
            try
            {
                await priorSends.WaitAsync(capture.Token).ConfigureAwait(false);
                MediaOverlaySessionSnapshot snapshot = await mediaOverlayCommands.GetCurrentMediaSnapshotAsync(cancellationToken: capture.Token);
                if (Volatile.Read(ref _mediaShutdownStarted) != 0
                    || requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    return;
                }

                ApplyMediaOverlayResult(BuildCurrentMediaOverlayResult(snapshot), requestVersion);
            }
            catch (OperationCanceledException) when (capture.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Warning("AppAudioCommandCoordinator", "Failed to capture current media state for overlay", nameof(ShowCurrentTrackOverlayAsync), ex);
                if (requestVersion == Volatile.Read(ref _latestMediaOverlayRequestVersion)
                    && Volatile.Read(ref _mediaShutdownStarted) == 0)
                {
                    overlay.Show("Media information unavailable", isCurrent: () => IsMediaFeedbackCurrent(requestVersion));
                }
            }
            finally
            {
                await FinishMediaCaptureAsync(capture).ConfigureAwait(false);
            }
        }

        private bool IsMediaFeedbackCurrent(int requestVersion) =>
            Volatile.Read(ref _mediaShutdownStarted) == 0 && requestVersion == Volatile.Read(ref _latestMediaOverlayRequestVersion);

        private void ApplyMediaOverlayResult(MediaOverlayResult mediaOverlay, int requestVersion)
        {
            if (mediaOverlay.Kind == MediaOverlayResultKind.Hidden)
            {
                return;
            }

            if (mediaOverlay.Kind == MediaOverlayResultKind.TrackMessage && !string.IsNullOrWhiteSpace(mediaOverlay.Title))
            {
                overlay.ShowMediaTrack(mediaOverlay.Header, mediaOverlay.Title, mediaOverlay.Artist, isCurrent: () => IsMediaFeedbackCurrent(requestVersion), icon: mediaOverlay.Icon);
                return;
            }

            if (!string.IsNullOrWhiteSpace(mediaOverlay.Message))
            {
                overlay.Show(mediaOverlay.Message, isCurrent: () => IsMediaFeedbackCurrent(requestVersion), icon: mediaOverlay.Icon);
            }
        }

        private int GetConfiguredVolumeStepPercent(bool playback)
        {
            Settings? settings = currentSettingsProvider();
            int configured = playback
                ? settings?.Hotkeys.Volume.MasterVolumeStepPercent ?? 5
                : settings?.Hotkeys.Volume.MicVolumeStepPercent ?? 5;

            return EndpointVolumeOperations.NormalizeVolumeStepPercent(configured);
        }

        public bool StepEndpointVolume(bool playback, bool increase, Func<bool> canApply)
        {
            AudioMixerMode mode = playback ? AudioMixerMode.Output : AudioMixerMode.Input;
            string reason = playback ? "hotkey-volume:master" : "hotkey-volume:recording";
            string overlayLabel = playback ? "Master volume" : "Microphone volume";
            int stepPercent = GetConfiguredVolumeStepPercent(playback);
            try
            {
                return ComThreadingHelper.RunOnCoreAudioThread(() => _volumeStepGate.Execute(mode, () =>
                {
                    if (!canApply()) return false;
                    int normalizedStepPercent = EndpointVolumeOperations.NormalizeVolumeStepPercent(stepPercent);
                    using var device = playback ? audio.GetDefaultPlaybackDevice() : audio.GetDefaultRecordingDevice();
                    if (device == null)
                    {
                        logger.Warning("AppAudioCommandCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} reason=no-default-device");
                        return false;
                    }

                    if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
                    {
                        logger.Warning("AppAudioCommandCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} device={LogPrivacy.Device(device.FriendlyName)} reason=endpoint-volume-unavailable");
                        return false;
                    }

                    var (appliedPercent, muted) = EndpointVolumeOperations.StepEndpointVolume(endpointVolume, stepPercent, increase);
                    TryNotifyEndpointVolumeApplied(mode, device, appliedPercent, muted);
                    overlay.Show(GetVolumeOverlayStateKind(increase), BuildVolumeOverlayMessage(overlayLabel, appliedPercent), icon: mode == AudioMixerMode.Input ? muted ? OverlayIcon.MicrophoneMuted : OverlayIcon.Microphone : muted ? OverlayIcon.SpeakerMuted : OverlayIcon.Speaker);
                    return true;
                }));
            }
            catch (Exception ex)
            {
                logger.Warning("AppAudioCommandCoordinator", $"Failed to adjust {overlayLabel.ToLowerInvariant()} via hotkey | direction={(increase ? "up" : "down")} stepPercent={EndpointVolumeOperations.NormalizeVolumeStepPercent(stepPercent)}", nameof(StepEndpointVolume), ex);
                return false;
            }
        }

        private void TryNotifyEndpointVolumeApplied(AudioMixerMode mode, MMDevice device, float volumePercent, bool isMuted)
        {
            if (endpointVolumeApplied == null)
            {
                return;
            }

            try
            {
                string endpointId = device.ID;
                if (!string.IsNullOrWhiteSpace(endpointId))
                {
                    endpointVolumeApplied(mode, endpointId, volumePercent, isMuted);
                }
            }
            catch (Exception ex)
            {
                logger.Warning("AppAudioCommandCoordinator", "Failed to publish applied endpoint volume state", nameof(TryNotifyEndpointVolumeApplied), ex);
            }
        }

        private void ShowListenToInputOverlay(InputListenStateChangeResult result)
        {
            if (!result.Applied)
            {
                if (result.Error == AppConstants.Audio.ErrorCodes.Listen.MonitorOutputUnavailable)
                    overlay.Show(OverlayDeviceKind.Error, "Monitor output unavailable", "Select an available monitor output or choose Default output.");
                return;
            }

            string deviceText = ComposeListenToInputOverlayDeviceText(result.Enabled,
                NormalizeListenToInputOverlayDeviceName(result.InputDeviceName), result.MonitorOutputDeviceName);
            overlay.Show(OverlayDeviceKind.Input, GetListenToInputOverlayHeader(result.Enabled, result.Verified), deviceText);
        }
    }
}
