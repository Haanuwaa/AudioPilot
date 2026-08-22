using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Internal
{
    internal static class PostSwitchCoordinator
    {
        /// <summary>Awaits tracked restoration and releases its optional guard on completion, rejection, or cancellation.</summary>
        internal static async Task RunTrackedAsync(Func<CancellationToken, Task> operation, Func<Func<CancellationToken, Task>, bool> tryQueue, CancellationToken token, IDisposable? ownedResource = null)
        {
            try
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using CancellationTokenRegistration registration = token.Register(() => completion.TrySetCanceled(token));
                token.ThrowIfCancellationRequested();
                string? traceId = OperationTrace.CurrentId;
                if (!tryQueue(async shutdownToken =>
                {
                    using var traceContext = OperationTrace.Attach(traceId);
                    using var trace = OperationTrace.Start("post-switch-work", id: traceId);
                    try
                    {
                        await operation(shutdownToken);
                        trace.Complete("completed");
                        completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        trace.Complete(ex is OperationCanceledException ? "cancelled" : "failed");
                        completion.TrySetException(ex);
                    }
                }))
                {
                    throw new InvalidOperationException("Post-switch audio work could not be queued.");
                }
                await completion.Task;
            }
            finally { ownedResource?.Dispose(); }
        }

        internal static bool? ResolveSwitchMuteState(bool keepMuteState, bool? sourceMuted, bool forceMute)
            => forceMute ? true : keepMuteState ? sourceMuted : null;

        /// <summary>Restores microphone level and mute independently to the selected input without depending on playback availability.</summary>
        internal static async Task RestoreInputStateAsync(
            Task<SessionVolumeSnapshot>? snapshotTask,
            string targetDeviceId,
            Action<string, float?, bool?> applyState,
            Func<bool> shouldContinue,
            CancellationToken shutdownToken,
            bool? muted = null,
            Func<bool>? canRestoreVolume = null,
            Func<bool>? canRestoreMute = null)
        {
            SessionVolumeSnapshot? snapshot;
            try { snapshot = snapshotTask != null ? await snapshotTask : null; }
            catch
            {
                if (muted.HasValue && shouldContinue() && !shutdownToken.IsCancellationRequested && canRestoreMute?.Invoke() != false)
                    applyState(targetDeviceId, null, muted);
                throw;
            }
            if (!shouldContinue() || shutdownToken.IsCancellationRequested)
            {
                return;
            }

            float? volume = snapshot?.MicVolumePercent is float percent && float.IsFinite(percent) ? Math.Clamp(percent, 0f, 100f) : null;
            if (volume.HasValue && canRestoreVolume?.Invoke() == false) volume = null;
            if (muted.HasValue && canRestoreMute?.Invoke() == false) muted = null;
            if (volume.HasValue || muted.HasValue) applyState(targetDeviceId, volume, muted);
        }

        /// <summary>
        /// Runs post-output-switch cleanup, including mute propagation and optional session volume restore for the
        /// newly selected playback device.
        /// </summary>
        /// <remarks>
        /// Mute propagation runs on a COM-initialized worker instead of the shared CoreAudio executor. The method rechecks disposal
        /// and shutdown before both the device-mute pass and the later session-volume restore so teardown does not
        /// race against delayed post-switch cleanup.
        /// </remarks>
        public static async Task ExecuteAsync(
            Func<bool> isDisposed,
            Logger logger,
            VolumeControlService volumeService,
            string opId,
            string targetDeviceId,
            NRole inputDetectionRole,
            bool? muteMic,
            bool? muteSound,
            bool deafen,
            bool preserveAudioLevels,
            bool restoreMasterVolume,
            bool restoreMicVolume,
            SessionVolumeSnapshot? snapshot,
            CancellationToken shutdownToken,
            Func<bool?, bool?, bool, CancellationToken, Task>? runMuteApplyWorkAsync = null,
            Func<bool>? shouldContinue = null,
            string? recordingDeviceId = null,
            string? communicationsDeviceId = null,
            bool preserveEndpointMute = false,
            Func<bool, bool?, bool?>? resolveMuteOverride = null,
            Func<bool>? canRestoreMasterVolume = null,
            Func<bool>? canRestorePlaybackMute = null,
            Func<bool>? isPlaybackMuteIntentCurrent = null)
        {
            if (shutdownToken.IsCancellationRequested || isDisposed() || shouldContinue?.Invoke() == false)
            {
                return;
            }

            Task muteApplyWork(CancellationToken token)
            {
                bool? recordingMute = deafen ? true : muteMic;
                bool? playbackMute = deafen ? true : muteSound;
                if (runMuteApplyWorkAsync != null && canRestorePlaybackMute?.Invoke() == false) playbackMute = null;
                if (resolveMuteOverride != null)
                {
                    recordingMute = resolveMuteOverride(false, recordingMute);
                    playbackMute = resolveMuteOverride(true, playbackMute);
                }
                if (isPlaybackMuteIntentCurrent?.Invoke() == false) playbackMute = null;
                if (runMuteApplyWorkAsync != null) return runMuteApplyWorkAsync(recordingMute, playbackMute, deafen, token);
                ApplyMuteSettingsForPostSwitch(
                    logger,
                    volumeService,
                    opId,
                    targetDeviceId,
                    inputDetectionRole,
                    recordingMute,
                    playbackMute,
                    deafen,
                    shouldContinue,
                    recordingDeviceId,
                    communicationsDeviceId,
                    resolveMuteOverride,
                    canRestorePlaybackMute,
                    isPlaybackMuteIntentCurrent,
                    token);
                return Task.CompletedTask;
            }

            if ((!preserveEndpointMute && (muteMic.HasValue || muteSound.HasValue)) || deafen)
                await muteApplyWork(shutdownToken);

            if (preserveAudioLevels && snapshot != null)
            {
                if (shutdownToken.IsCancellationRequested || isDisposed() || shouldContinue?.Invoke() == false)
                {
                    return;
                }

                await volumeService.ApplySessionVolumesSimpleAsync(
                    snapshot,
                    targetDeviceId,
                    recordingDeviceId,
                    () => !shutdownToken.IsCancellationRequested && !isDisposed() && shouldContinue?.Invoke() != false,
                    applyMasterVolume: restoreMasterVolume,
                    applyMicVolume: restoreMicVolume,
                    canRestoreMasterVolume: canRestoreMasterVolume);
            }
        }

        private static void ApplyMuteSettingsForPostSwitch(
            Logger logger,
            VolumeControlService volumeService,
            string opId,
            string targetDeviceId,
            NRole inputDetectionRole,
            bool? muteMic,
            bool? muteSound,
            bool deafen,
            Func<bool>? shouldContinue,
            string? recordingDeviceId,
            string? communicationsDeviceId,
            Func<bool, bool?, bool?>? resolveMuteOverride,
            Func<bool>? canRestorePlaybackMute,
            Func<bool>? isPlaybackMuteIntentCurrent,
            CancellationToken shutdownToken)
        {
            shutdownToken.ThrowIfCancellationRequested();
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(PostSwitchCoordinator));

            MMDevice? bgPlaybackDevice = null;
            MMDevice? bgRecordingDevice = null;

            using var localEnumerator = new MMDeviceEnumerator();

            try
            {
                bgPlaybackDevice = localEnumerator.GetDevice(targetDeviceId);
                try
                {
                    if (!string.IsNullOrEmpty(recordingDeviceId))
                    {
                        using MMDevice currentRecording = localEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, inputDetectionRole);
                        if (string.Equals(currentRecording.ID, recordingDeviceId, StringComparison.OrdinalIgnoreCase))
                            bgRecordingDevice = localEnumerator.GetDevice(recordingDeviceId);
                    }
                }
                catch (Exception captureEx)
                {
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.Trace("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.OutputSwitch.PostSkipRecordingEndpoint} | opId={opId} role={inputDetectionRole} reason={captureEx.GetType().Name}");
                    }
                }

                if (shouldContinue?.Invoke() == false)
                {
                    return;
                }

                volumeService.ApplyMuteSettingsDirect(muteMic, muteSound, deafen, bgPlaybackDevice, bgRecordingDevice, localEnumerator, communicationsDeviceId, shouldContinue, resolveMuteOverride, canRestorePlaybackMute, isPlaybackMuteIntentCurrent);
            }
            finally
            {
                bgPlaybackDevice?.Dispose();
                bgRecordingDevice?.Dispose();
            }
        }
    }
}
