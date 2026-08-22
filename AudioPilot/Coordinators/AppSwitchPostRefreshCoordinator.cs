using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct SwitchPostRefreshInput(
        string OperationId,
        bool IsWindowVisible,
        bool IsCleaningUp);

    internal readonly record struct MuteFlagUpdateResult(
        bool NewDeafen,
        bool NewMuteSound,
        bool NewMuteMic,
        bool AnyChanged);

    internal static class AppSwitchPostRefreshCoordinator
    {
        /// <summary>
        /// Runs the output-switch post-refresh sequence, skipping mixer work when the window is hidden but still
        /// refreshing device cache and mute state while the app remains active.
        /// </summary>
        public static async Task ExecuteOutputPostSwitchRefreshAsync(
            SwitchPostRefreshInput input,
            Action refreshDeviceCache,
            Func<Task> updateMuteFlagsAsync,
            Func<Task> refreshMixerAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || input.IsCleaningUp)
            {
                return;
            }

            refreshDeviceCache();
            await updateMuteFlagsAsync();

            if (input.IsWindowVisible)
            {
                await refreshMixerAsync();
                return;
            }

            logger.Debug("AppViewModel", () => $"{AppConstants.Audio.LogEvents.ViewModel.App.OutputSwitchPost} | opId={input.OperationId} action=skip-mixer-refresh reason=window-hidden");
        }

        /// <summary>
        /// Recomputes the deafen, mute-sound, and mute-mic flags from the underlying playback and microphone mute
        /// state after a switch refresh.
        /// </summary>
        public static MuteFlagUpdateResult ResolveMuteFlagUpdate(
            bool isPlaybackMuted,
            bool isMicMuted,
            bool currentDeafen,
            bool currentMuteSound,
            bool currentMuteMic)
        {
            bool newDeafen = isPlaybackMuted && isMicMuted;
            bool newMuteSound = !newDeafen && isPlaybackMuted;
            bool newMuteMic = !newDeafen && isMicMuted;
            bool anyChanged =
                currentDeafen != newDeafen ||
                currentMuteSound != newMuteSound ||
                currentMuteMic != newMuteMic;

            return new MuteFlagUpdateResult(newDeafen, newMuteSound, newMuteMic, anyChanged);
        }
    }
}
