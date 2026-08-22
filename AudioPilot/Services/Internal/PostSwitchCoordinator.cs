using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Internal
{
    internal static class PostSwitchCoordinator
    {
        /// <summary>
        /// Runs post-output-switch cleanup, including mute propagation and optional session volume restore for the
        /// newly selected playback device.
        /// </summary>
        /// <remarks>
        /// Mute propagation now runs on its own COM-initialized worker instead of the shared CoreAudio executor so
        /// best-effort post-switch work cannot block switch-critical role changes. The method also rechecks disposal
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
            Func<bool>? shouldContinue = null)
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
                        token);
                    return Task.CompletedTask;
                });

            await muteApplyWork(shutdownToken);

            if (preserveAudioLevels && snapshot != null)
            {
                if (shutdownToken.IsCancellationRequested || isDisposed() || shouldContinue?.Invoke() == false)
                {
                    return;
                }

                await volumeService.ApplySessionVolumesSimpleAsync(
                    snapshot,
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
                    bgRecordingDevice = localEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, inputDetectionRole);
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

                volumeService.ApplyMuteSettingsDirect(muteMic, muteSound, deafen, bgPlaybackDevice, bgRecordingDevice, localEnumerator);
            }
            finally
            {
                bgPlaybackDevice?.Dispose();
                bgRecordingDevice?.Dispose();
            }
        }
    }
}
