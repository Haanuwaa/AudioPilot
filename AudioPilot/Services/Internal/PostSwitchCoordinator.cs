using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Internal
{
    internal static class PostSwitchCoordinator
    {
        /// <summary>Awaits required work without detaching it from the audio service's bounded shutdown queue.</summary>
        internal static async Task RunTrackedAsync(Func<CancellationToken, Task> operation, Func<Func<CancellationToken, Task>, bool> tryQueue, CancellationToken token)
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

        /// <summary>Restores a captured microphone level to the selected input without depending on playback availability.</summary>
        internal static async Task RestoreInputVolumeAsync(
            Task<SessionVolumeSnapshot> snapshotTask,
            string targetDeviceId,
            Action<string, float> applyVolume,
            Func<bool> shouldContinue,
            CancellationToken shutdownToken)
        {
            SessionVolumeSnapshot snapshot = await snapshotTask;
            if (!shouldContinue() || shutdownToken.IsCancellationRequested)
            {
                return;
            }

            if (snapshot.MicVolumePercent is float percent && float.IsFinite(percent))
            {
                applyVolume(targetDeviceId, Math.Clamp(percent, 0f, 100f));
            }
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
            bool muteMic,
            bool muteSound,
            bool deafen,
            bool preserveAudioLevels,
            bool restoreMasterVolume,
            bool restoreMicVolume,
            SessionVolumeSnapshot? snapshot,
            CancellationToken shutdownToken,
            Func<CancellationToken, Task>? runMuteApplyWorkAsync = null,
            Func<bool>? shouldContinue = null,
            string? recordingDeviceId = null,
            string? communicationsDeviceId = null,
            bool preserveEndpointMute = false)
        {
            if (shutdownToken.IsCancellationRequested || isDisposed() || shouldContinue?.Invoke() == false)
            {
                return;
            }

            Func<CancellationToken, Task> muteApplyWork = runMuteApplyWorkAsync ??
                (token =>
                {
                    ApplyMuteSettingsForPostSwitch(
                        logger,
                        volumeService,
                        opId,
                        targetDeviceId,
                        inputDetectionRole,
                        muteMic,
                        muteSound,
                        deafen,
                        shouldContinue,
                        recordingDeviceId,
                        communicationsDeviceId,
                        token);
                    return Task.CompletedTask;
                });

            if (!preserveEndpointMute || deafen)
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
                    applyMicVolume: restoreMicVolume);
            }
        }

        private static void ApplyMuteSettingsForPostSwitch(
            Logger logger,
            VolumeControlService volumeService,
            string opId,
            string targetDeviceId,
            NRole inputDetectionRole,
            bool muteMic,
            bool muteSound,
            bool deafen,
            Func<bool>? shouldContinue,
            string? recordingDeviceId,
            string? communicationsDeviceId,
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

                volumeService.ApplyMuteSettingsDirect(muteMic, muteSound, deafen, bgPlaybackDevice, bgRecordingDevice, localEnumerator, communicationsDeviceId, shouldContinue);
            }
            finally
            {
                bgPlaybackDevice?.Dispose();
                bgRecordingDevice?.Dispose();
            }
        }
    }
}
