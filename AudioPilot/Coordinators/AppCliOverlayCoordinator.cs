using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace AudioPilot.Coordinators
{
    internal sealed class AppCliOverlayCoordinator(
        AudioDeviceService audio,
        OverlayService overlay,
        MediaOverlayCommandService mediaOverlayCommands,
        Logger logger,
        Func<Settings?> currentSettingsProvider,
        Func<bool>? mediaPlayPauseCommand = null,
        Func<bool>? mediaNextTrackCommand = null,
        Func<bool>? mediaPreviousTrackCommand = null,
        Func<Task<bool>>? mediaPlayPauseCommandAsync = null,
        Func<Task<bool>>? mediaNextTrackCommandAsync = null,
        Func<Task<bool>>? mediaPreviousTrackCommandAsync = null,
        Action<ExecutionHistoryEntry>? mediaHistoryRecorder = null,
        Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPlayPauseCommandDetailedAsync = null,
        Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaNextTrackCommandDetailedAsync = null,
        Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPreviousTrackCommandDetailedAsync = null,
        Action<AudioMixerMode, string, float, bool>? endpointVolumeApplied = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPlayPauseCommandCancellableAsync = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaNextTrackCommandCancellableAsync = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPreviousTrackCommandCancellableAsync = null,
        int mediaShutdownDrainTimeoutMs = AppConstants.MediaOverlay.ShutdownDrainTimeoutMs)
    {
        private int _mediaOverlayCaptureInFlight;
        private int _latestMediaOverlayRequestVersion;
        private readonly Lock _pendingMediaOverlayLock = new();
        private readonly Lock _mediaSendOrderLock = new();
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
        private readonly Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> _mediaPlayPauseCommand = ResolveMediaCommand(mediaPlayPauseCommandCancellableAsync, mediaPlayPauseCommandDetailedAsync, mediaPlayPauseCommandAsync, mediaPlayPauseCommand, MediaKeyHelper.TryPressPlayPauseDetailedAsync);
        private readonly Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> _mediaNextTrackCommand = ResolveMediaCommand(mediaNextTrackCommandCancellableAsync, mediaNextTrackCommandDetailedAsync, mediaNextTrackCommandAsync, mediaNextTrackCommand, MediaKeyHelper.TryPressNextTrackDetailedAsync);
        private readonly Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> _mediaPreviousTrackCommand = ResolveMediaCommand(mediaPreviousTrackCommandCancellableAsync, mediaPreviousTrackCommandDetailedAsync, mediaPreviousTrackCommandAsync, mediaPreviousTrackCommand, MediaKeyHelper.TryPressPreviousTrackDetailedAsync);
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
            string Source);

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
                _ready.TrySetResult(true);
                return Completion;
            }

            public void CancelBeforeSend()
            {
                _ready.TrySetResult(false);
            }

            public Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
            {
                return _ready.Task.WaitAsync(cancellationToken);
            }
        }

        internal bool IsMediaOverlayCaptureInFlightForTests => Volatile.Read(ref _mediaOverlayCaptureInFlight) != 0;

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
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                int requestVersion = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
                if (Interlocked.CompareExchange(ref _mediaOverlayCaptureInFlight, 1, 0) != 0)
                {
                    QueuePendingMediaOverlayCapture(
                        PendingMediaOverlayCaptureKind.CurrentTrack,
                        default,
                        requestVersion,
                        CliSource);
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AppCliOverlayCoordinator", () => "media-overlay-capture-deferred | command=show-current-track reason=capture-in-flight");
                    }

                    return;
                }

                TrackMediaOperation(ShowCurrentTrackOverlayAsync(requestVersion));
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
                    lock (_pendingMediaOverlayLock)
                    {
                        _pendingMediaOverlayCapture = null;
                    }
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
            Task completedTask = await Task.WhenAny(
                drainTask,
                Task.Delay(_mediaShutdownDrainTimeoutMs)).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, drainTask))
            {
                logger.Warning(
                    "AppCliOverlayCoordinator",
                    () => $"media-overlay-shutdown-drain-timeout | timeoutMs={_mediaShutdownDrainTimeoutMs} pending={operations.Count(operation => !operation.IsCompleted)}",
                    nameof(ShutdownAsync));
                _ = drainTask.ContinueWith(
                    task => logger.Warning(
                        "AppCliOverlayCoordinator",
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
                    "AppCliOverlayCoordinator",
                    () => $"media-overlay-shutdown-drain-failed | error={ex.GetType().Name}",
                    nameof(ShutdownAsync));
            }
        }

        public async Task<MediaOverlaySessionSnapshot> GetCurrentMediaSnapshotAsync(CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _mediaShutdownCts.Token);
            return await mediaOverlayCommands.GetCurrentMediaSnapshotAsync(linkedCancellation.Token).ConfigureAwait(false);
        }

        public static MediaOverlayResult BuildCurrentMediaOverlayResult(MediaOverlaySessionSnapshot snapshot)
        {
            if (MediaOverlayEngine.IsSessionMissing(snapshot) || !MediaOverlayEngine.HasTrackData(snapshot))
            {
                return MediaOverlayResult.Plain("No current track");
            }

            string header = snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                ? "Current track paused"
                : "Current track";
            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown title" : snapshot.Title;

            return MediaOverlayResult.Track(header, title, snapshot.Artist);
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
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Playback paused",
                MediaOverlayCommand.PlayPause when snapshot.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playback resumed",
                MediaOverlayCommand.PlayPause => "Play/pause command sent",
                _ => "Media command sent",
            };

            return MediaOverlayResult.Track(header, title, snapshot.Artist);
        }

        public bool ToggleMuteMic(Func<bool> currentValueProvider, Action<bool> applyMuteMic)
        {
            return ToggleState(currentValueProvider, applyMuteMic, "Microphone muted", "Microphone unmuted");
        }

        public bool SetMuteMic(bool enabled, Action<bool> applyMuteMic)
        {
            return SetState(enabled, applyMuteMic, "Microphone muted", "Microphone unmuted");
        }

        public bool ToggleMuteSound(Func<bool> currentValueProvider, Action<bool> applyMuteSound)
        {
            return ToggleState(currentValueProvider, applyMuteSound, "Sound muted", "Sound unmuted");
        }

        public bool SetMuteSound(bool enabled, Action<bool> applyMuteSound)
        {
            return SetState(enabled, applyMuteSound, "Sound muted", "Sound unmuted");
        }

        public bool ToggleDeafen(Func<bool> currentValueProvider, Action<bool> applyDeafen)
        {
            return ToggleState(currentValueProvider, applyDeafen, "Deafened", "Undeafened");
        }

        public bool SetDeafen(bool enabled, Action<bool> applyDeafen)
        {
            return SetState(enabled, applyDeafen, "Deafened", "Undeafened");
        }

        public bool StepMasterVolume(bool increase)
        {
            return StepEndpointVolume(
                getDevice: () => audio.GetDefaultPlaybackDevice(),
                mode: AudioMixerMode.Output,
                stepPercent: GetConfiguredVolumeStepPercent(playback: true),
                increase,
                reason: "hotkey-volume:master",
                overlayLabel: "Master volume");
        }

        public bool StepMicVolume(bool increase)
        {
            return StepEndpointVolume(
                getDevice: () => audio.GetDefaultRecordingDevice(),
                mode: AudioMixerMode.Input,
                stepPercent: GetConfiguredVolumeStepPercent(playback: false),
                increase,
                reason: "hotkey-volume:recording",
                overlayLabel: "Microphone volume");
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
            InputListenStateChangeResult result = audio.ToggleCurrentInputListenState(preferredMonitorOutputDeviceId, preferredMonitorOutputDeviceName);
            if (!result.Applied)
            {
                logger.Warning("AppCliOverlayCoordinator", () => $"{AppConstants.Audio.LogEvents.Listen.ToggleFailed} | error={result.Error ?? "unknown"}");
                return false;
            }

            ShowListenToInputOverlay(result.Enabled, result.Verified);
            return true;
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
            InputListenStateChangeResult result = audio.SetCurrentInputListenState(preferredMonitorOutputDeviceId, preferredMonitorOutputDeviceName, enabled);
            if (!result.Applied)
            {
                logger.Warning("AppCliOverlayCoordinator", () => $"{AppConstants.Audio.LogEvents.Listen.SetFailed} | target={enabled} error={result.Error ?? "unknown"}");
                return false;
            }

            ShowListenToInputOverlay(enabled, result.Verified);
            return true;
        }

        internal static string GetListenToInputOverlayHeader(bool enabled, bool verified = true)
        {
            string state = enabled ? "Input listen enabled" : "Input listen disabled";
            return verified ? state : $"{state} (verification pending)";
        }

        internal static string NormalizeListenToInputOverlayDeviceName(string? friendlyName)
        {
            return string.IsNullOrWhiteSpace(friendlyName)
                ? "Current input device"
                : friendlyName;
        }

        internal static string ComposeListenToInputOverlayDeviceText(bool enabled, string inputDeviceName, string? monitorTargetOutputDeviceName)
        {
            if (!enabled)
            {
                return inputDeviceName;
            }

            string outputTarget = string.IsNullOrWhiteSpace(monitorTargetOutputDeviceName)
                ? "Default output"
                : monitorTargetOutputDeviceName;

            return $"{inputDeviceName}\nTo: {outputTarget}";
        }

        internal static float ComputeSteppedVolumePercent(float currentPercent, int stepPercent, bool increase)
        {
            float normalizedCurrent = Math.Clamp(currentPercent, 0f, 100f);
            int normalizedStep = NormalizeVolumeStepPercent(stepPercent);
            float delta = increase ? normalizedStep : -normalizedStep;
            return Math.Clamp(normalizedCurrent + delta, 0f, 100f);
        }

        internal static string BuildVolumeOverlayMessage(string label, float resultingPercent)
        {
            int roundedPercent = (int)Math.Round(Math.Clamp(resultingPercent, 0f, 100f), MidpointRounding.AwayFromZero);
            return $"{label} {roundedPercent}%";
        }

        internal static bool TryGetEndpointVolumeState(Logger logger, MMDevice? device, string reason, out float currentPercent, out bool muted)
        {
            currentPercent = 0f;
            muted = false;

            if (device == null)
            {
                return false;
            }

            if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
            {
                return false;
            }

            currentPercent = Math.Clamp(endpointVolume.MasterVolumeLevelScalar * 100f, 0f, 100f);
            muted = endpointVolume.Mute;
            return true;
        }

        internal static bool TryApplyEndpointVolume(Logger logger, MMDevice? device, float targetPercent, string reason, bool muteAtZero, bool unmuteAboveZero, out float appliedPercent)
        {
            appliedPercent = 0f;
            if (device == null)
            {
                return false;
            }

            if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
            {
                return false;
            }

            bool applied = TryApplyEndpointVolume(endpointVolume, targetPercent, muteAtZero, unmuteAboveZero, out appliedPercent);

            return applied;
        }

        internal static bool TryApplyEndpointVolume(AudioEndpointVolume endpointVolume, float targetPercent, bool muteAtZero, bool unmuteAboveZero, out float appliedPercent)
        {
            appliedPercent = Math.Clamp(targetPercent, 0f, 100f);
            endpointVolume.MasterVolumeLevelScalar = appliedPercent / 100f;

            if (muteAtZero && appliedPercent <= 0f)
            {
                endpointVolume.Mute = true;
            }
            else if (unmuteAboveZero && appliedPercent > 0f)
            {
                endpointVolume.Mute = false;
            }

            return true;
        }

        internal static OverlayActionStateKind GetVolumeOverlayStateKind(bool increase)
        {
            return increase ? OverlayActionStateKind.Enabled : OverlayActionStateKind.Disabled;
        }

        internal static int NormalizeVolumeStepPercent(int stepPercent)
        {
            return stepPercent < 1 ? 5 : Math.Clamp(stepPercent, 1, 100);
        }

        private void StartMediaCommand(MediaOverlayCommand command, Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> sendCommandAsync, string source)
        {
            lock (_mediaLifecycleLock)
            {
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                int requestVersion = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
                MediaCommandSendReservation sendReservation = ReserveMediaCommandSend(command, sendCommandAsync);
                if (Interlocked.CompareExchange(ref _mediaOverlayCaptureInFlight, 1, 0) != 0)
                {
                    TrackMediaOperation(SendMediaCommandWithoutCaptureAsync(command, sendReservation, requestVersion, source));
                    return;
                }

                TrackMediaOperation(ShowMediaOverlayAsync(command, sendReservation, requestVersion, source));
            }
        }

        private async Task SendMediaCommandWithoutCaptureAsync(MediaOverlayCommand command, MediaCommandSendReservation sendReservation, int requestVersion, string source)
        {
            long started = Stopwatch.GetTimestamp();
            MediaKeyHelper.MediaCommandSendOutcome sendOutcome = await sendReservation.SendAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _mediaShutdownStarted) != 0)
            {
                return;
            }

            if (!sendOutcome.Sent)
            {
                MediaOverlayResult failureResult = MediaOverlayEngine.BuildCommandSendFailureResult(command);
                bool isLatestRequest = requestVersion == Volatile.Read(ref _latestMediaOverlayRequestVersion);
                if (isLatestRequest && Volatile.Read(ref _mediaShutdownStarted) == 0)
                {
                    ApplyMediaOverlayResult(failureResult);
                }

                RecordMediaCommandHistory(
                    command,
                    failureResult,
                    source,
                    success: false,
                    skipped: !isLatestRequest,
                    diagCode: isLatestRequest ? "media-command-send-failed" : "media-command-send-failed-stale-suppressed",
                    reason: isLatestRequest ? "Media command send failed." : "A newer media command superseded this failure overlay.",
                    elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: BuildMediaSendOutcomeDetails(sendOutcome, new Dictionary<string, string>
                    {
                        ["overlayCapture"] = "in-flight",
                        ["fallback"] = "send-only",
                    }));
                return;
            }

            QueuePendingMediaOverlayCapture(
                PendingMediaOverlayCaptureKind.CommandState,
                command,
                requestVersion,
                source);
            StartPendingMediaOverlayCaptureIfNeeded();

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace("AppCliOverlayCoordinator", () => $"media-overlay-capture-deferred | command={command} reason=capture-in-flight");
            }
        }

        private async Task ShowMediaOverlayAsync(MediaOverlayCommand command, MediaCommandSendReservation sendReservation, int requestVersion, string source)
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
                    () => sendOutcome?.CandidateSourceAppUserModelId,
                    _mediaShutdownCts.Token);
                MediaOverlayResult mediaOverlay = commandResult.Overlay;
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    if (!HasPendingMediaOverlayCaptureForLatestVersion())
                    {
                        RecordMediaCommandHistory(
                            command,
                            mediaOverlay,
                            source,
                            success: true,
                            skipped: true,
                            diagCode: "media-overlay-stale-suppressed",
                            reason: "A newer media command superseded this overlay result.",
                            elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            details: BuildMediaCommandDetails(commandResult, sendOutcome, new Dictionary<string, string>
                            {
                                ["overlayCapture"] = "superseded",
                            }));
                    }

                    return;
                }

                ApplyMediaOverlayResult(mediaOverlay);
                RecordMediaCommandHistory(
                    command,
                    mediaOverlay,
                    source,
                    success: !IsCommandSendFailure(command, mediaOverlay),
                    skipped: mediaOverlay.Kind == MediaOverlayResultKind.Hidden,
                    diagCode: commandResult.DiagCode,
                    reason: GetMediaOverlayHistoryReason(command, mediaOverlay),
                    elapsedMs: commandResult.ElapsedMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    details: BuildMediaCommandDetails(commandResult, sendOutcome));
            }
            catch (OperationCanceledException) when (_mediaShutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", "Failed to resolve media session state for overlay", nameof(ShowMediaOverlayAsync), ex);
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

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
                    overlay.Show("Next track unknown");
                }
                else if (command == MediaOverlayCommand.PreviousTrack)
                {
                    overlay.Show("Previous track unknown");
                }
            }
            finally
            {
                sendReservation.CancelBeforeSend();
                Interlocked.Exchange(ref _mediaOverlayCaptureInFlight, 0);
                StartPendingMediaOverlayCaptureIfNeeded();
            }
        }

        private MediaCommandSendReservation ReserveMediaCommandSend(
            MediaOverlayCommand command,
            Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> sendCommandAsync)
        {
            var reservation = new MediaCommandSendReservation();
            lock (_mediaSendOrderLock)
            {
                Task predecessor = _mediaSendOrderTail;
                Task<MediaKeyHelper.MediaCommandSendOutcome> completion = ProcessReservedMediaCommandSendAsync(
                    predecessor,
                    reservation,
                    command,
                    sendCommandAsync,
                    _mediaShutdownCts.Token);
                reservation.Attach(completion);
                _mediaSendOrderTail = completion.ContinueWith(
                    static _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return reservation;
        }

        private async Task<MediaKeyHelper.MediaCommandSendOutcome> ProcessReservedMediaCommandSendAsync(
            Task predecessor,
            MediaCommandSendReservation reservation,
            MediaOverlayCommand command,
            Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> sendCommandAsync,
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
                MediaKeyHelper.MediaCommandSendOutcome outcome = await sendCommandAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!outcome.Sent)
                {
                    logger.Warning(
                        "MediaOverlayHelper",
                        () => $"media-command-send-failed | command={command} route={outcome.Route} failure={outcome.FailureReason ?? "unknown"}");
                }

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
            string source)
        {
            if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion)
                || Volatile.Read(ref _mediaShutdownStarted) != 0)
            {
                return;
            }

            lock (_pendingMediaOverlayLock)
            {
                if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion)
                    || Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                if (_pendingMediaOverlayCapture is { } existing && existing.RequestVersion > requestVersion)
                {
                    return;
                }

                _pendingMediaOverlayCapture = new PendingMediaOverlayCapture(kind, command, requestVersion, source);
            }
        }

        private bool HasPendingMediaOverlayCaptureForLatestVersion()
        {
            lock (_pendingMediaOverlayLock)
            {
                return _pendingMediaOverlayCapture is { } pending
                    && pending.RequestVersion == Volatile.Read(ref _latestMediaOverlayRequestVersion);
            }
        }

        private void StartPendingMediaOverlayCaptureIfNeeded()
        {
            if (Volatile.Read(ref _mediaShutdownStarted) != 0)
            {
                return;
            }

            PendingMediaOverlayCapture? pending;
            lock (_pendingMediaOverlayLock)
            {
                pending = _pendingMediaOverlayCapture;
                _pendingMediaOverlayCapture = null;
            }

            if (pending is not { } capture)
            {
                return;
            }

            if (capture.RequestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _mediaOverlayCaptureInFlight, 1, 0) != 0)
            {
                QueuePendingMediaOverlayCapture(capture.Kind, capture.Command, capture.RequestVersion, capture.Source);
                return;
            }

            Task captureTask = capture.Kind == PendingMediaOverlayCaptureKind.CurrentTrack
                ? ShowCurrentTrackOverlayAsync(capture.RequestVersion)
                : ShowCurrentMediaStateOverlayAsync(capture.Command, capture.RequestVersion, capture.Source);
            TrackMediaOperation(captureTask);
        }

        private async Task ShowCurrentMediaStateOverlayAsync(MediaOverlayCommand command, int requestVersion, string source)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                MediaOverlaySessionSnapshot snapshot = await mediaOverlayCommands.GetCurrentMediaSnapshotAsync(_mediaShutdownCts.Token);
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return;
                }

                if (requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    RecordMediaCommandHistory(
                        command,
                        MediaOverlayResult.Hidden,
                        source,
                        success: true,
                        skipped: true,
                        diagCode: "media-overlay-trailing-stale-suppressed",
                        reason: "A newer media command superseded this trailing overlay result.",
                        elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        details: new Dictionary<string, string>
                        {
                            ["overlayCapture"] = "trailing-superseded",
                        });
                    return;
                }

                MediaOverlayResult mediaOverlay = BuildTrailingMediaOverlayResult(command, snapshot);
                ApplyMediaOverlayResult(mediaOverlay);
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
                    });
            }
            catch (OperationCanceledException) when (_mediaShutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", "Failed to resolve trailing media state for overlay", nameof(ShowCurrentMediaStateOverlayAsync), ex);
            }
            finally
            {
                Interlocked.Exchange(ref _mediaOverlayCaptureInFlight, 0);
                StartPendingMediaOverlayCaptureIfNeeded();
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
                details["outcome"] = trackDiagnostics.Outcome;
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
            Dictionary<string, string> details = new(StringComparer.OrdinalIgnoreCase);
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
                return $"{GetMediaCommandLabel(command)} media command resolved to updated track metadata.";
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

        private async Task ShowCurrentTrackOverlayAsync(int requestVersion)
        {
            try
            {
                MediaOverlaySessionSnapshot snapshot = await mediaOverlayCommands.GetCurrentMediaSnapshotAsync(_mediaShutdownCts.Token);
                if (Volatile.Read(ref _mediaShutdownStarted) != 0
                    || requestVersion != Volatile.Read(ref _latestMediaOverlayRequestVersion))
                {
                    return;
                }

                ApplyMediaOverlayResult(BuildCurrentMediaOverlayResult(snapshot));
            }
            catch (OperationCanceledException) when (_mediaShutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", "Failed to capture current media state for overlay", nameof(ShowCurrentTrackOverlayAsync), ex);
                if (requestVersion == Volatile.Read(ref _latestMediaOverlayRequestVersion)
                    && Volatile.Read(ref _mediaShutdownStarted) == 0)
                {
                    overlay.Show("No current track");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _mediaOverlayCaptureInFlight, 0);
                StartPendingMediaOverlayCaptureIfNeeded();
            }
        }

        private void ApplyMediaOverlayResult(MediaOverlayResult mediaOverlay)
        {
            if (mediaOverlay.Kind == MediaOverlayResultKind.Hidden)
            {
                return;
            }

            if (mediaOverlay.Kind == MediaOverlayResultKind.TrackMessage && !string.IsNullOrWhiteSpace(mediaOverlay.Title))
            {
                overlay.ShowMediaTrack(mediaOverlay.Header, mediaOverlay.Title, mediaOverlay.Artist);
                return;
            }

            if (!string.IsNullOrWhiteSpace(mediaOverlay.Message))
            {
                overlay.Show(mediaOverlay.Message);
            }
        }

        private int GetConfiguredVolumeStepPercent(bool playback)
        {
            Settings? settings = currentSettingsProvider();
            int configured = playback
                ? settings?.Hotkeys.Volume.MasterVolumeStepPercent ?? 5
                : settings?.Hotkeys.Volume.MicVolumeStepPercent ?? 5;

            return NormalizeVolumeStepPercent(configured);
        }

        private static Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> ResolveMediaCommand(
            Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? cancellableDetailedAsyncCommand,
            Func<Task<MediaKeyHelper.MediaCommandSendOutcome>>? detailedAsyncCommand,
            Func<Task<bool>>? asyncCommand,
            Func<bool>? syncCommand,
            Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>> defaultCommand)
        {
            if (cancellableDetailedAsyncCommand != null)
            {
                return cancellableDetailedAsyncCommand;
            }

            if (detailedAsyncCommand != null)
            {
                return async cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MediaKeyHelper.MediaCommandSendOutcome outcome = await detailedAsyncCommand().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return outcome;
                };
            }

            if (asyncCommand != null)
            {
                return async cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool sent = await asyncCommand().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(sent);
                };
            }

            return syncCommand == null
                ? defaultCommand
                : cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(syncCommand()));
                };
        }

        private bool ToggleState(Func<bool> currentValueProvider, Action<bool> applyState, string enabledMessage, string disabledMessage)
        {
            return SetState(!currentValueProvider(), applyState, enabledMessage, disabledMessage);
        }

        private bool SetState(bool enabled, Action<bool> applyState, string enabledMessage, string disabledMessage)
        {
            applyState(enabled);
            overlay.Show(enabled ? OverlayActionStateKind.Disabled : OverlayActionStateKind.Enabled, enabled ? enabledMessage : disabledMessage);
            return true;
        }

        private bool StepEndpointVolume(
            Func<MMDevice?> getDevice,
            AudioMixerMode mode,
            int stepPercent,
            bool increase,
            string reason,
            string overlayLabel)
        {
            try
            {
                return _volumeStepGate.Execute(mode, () =>
                {
                    int normalizedStepPercent = NormalizeVolumeStepPercent(stepPercent);
                    using var device = getDevice();
                    if (device == null)
                    {
                        logger.Warning("AppCliOverlayCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} reason=no-default-device");
                        return false;
                    }

                    if (!TryGetEndpointVolumeState(logger, device, reason, out float currentPercent, out bool currentMuted))
                    {
                        logger.Warning("AppCliOverlayCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} device={LogPrivacy.Device(device.FriendlyName)} reason=endpoint-volume-unavailable");
                        return false;
                    }

                    float updatedPercent = ComputeSteppedVolumePercent(currentPercent, stepPercent, increase);
                    if (!TryApplyEndpointVolume(logger, device, updatedPercent, reason, muteAtZero: false, unmuteAboveZero: increase, out float appliedPercent))
                    {
                        logger.Warning("AppCliOverlayCoordinator", () => $"volume-step-failed | target={overlayLabel} direction={(increase ? "up" : "down")} stepPercent={normalizedStepPercent} device={LogPrivacy.Device(device.FriendlyName)} currentPercent={currentPercent:F1} targetPercent={updatedPercent:F1} reason=endpoint-volume-apply-failed");
                        return false;
                    }

                    bool appliedMuted = !(increase && appliedPercent > 0f) && currentMuted;
                    TryNotifyEndpointVolumeApplied(mode, device, appliedPercent, appliedMuted);
                    overlay.Show(GetVolumeOverlayStateKind(increase), BuildVolumeOverlayMessage(overlayLabel, appliedPercent));
                    return true;
                });
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", $"Failed to adjust {overlayLabel.ToLowerInvariant()} via hotkey | direction={(increase ? "up" : "down")} stepPercent={NormalizeVolumeStepPercent(stepPercent)}", nameof(StepEndpointVolume), ex);
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
                logger.Warning("AppCliOverlayCoordinator", "Failed to publish applied endpoint volume state", nameof(TryNotifyEndpointVolumeApplied), ex);
            }
        }

        private void ShowListenToInputOverlay(bool enabled, bool verified)
        {
            string inputDeviceName = "Current input device";
            string? monitorTargetOutputDeviceName = null;
            try
            {
                using var inputDevice = audio.GetDefaultRecordingDevice();
                inputDeviceName = NormalizeListenToInputOverlayDeviceName(inputDevice?.FriendlyName);

                audio.TryGetCurrentInputListenTargetOutputDeviceName(out monitorTargetOutputDeviceName, out _);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(AudioPilot.Logging.LogLevel.Trace))
                {
                    logger.Trace("AppCliOverlayCoordinator", () => $"Listen overlay input name fallback used: {ex.GetType().Name}");
                }
            }

            string deviceText = ComposeListenToInputOverlayDeviceText(enabled, inputDeviceName, monitorTargetOutputDeviceName);
            overlay.Show(OverlayDeviceKind.Input, GetListenToInputOverlayHeader(enabled, verified), deviceText);
        }
    }
}
