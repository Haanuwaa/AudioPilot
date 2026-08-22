namespace AudioPilot.Coordinators
{
    internal readonly record struct PostSaveMuteApplication(
        bool MuteMicrophone,
        bool MutePlayback);

    internal static class AppPostSaveCoordinator
    {
        public static PostSaveMuteApplication BuildMuteApplication(
            bool currentDeafen,
            bool currentMuteMic,
            bool currentMuteSound)
        {
            return new PostSaveMuteApplication(
                MuteMicrophone: currentMuteMic || currentDeafen,
                MutePlayback: currentMuteSound || currentDeafen);
        }

        public static async Task ApplyMuteStateAsync(
            Func<Task> applyMuteStateAsync,
            CancellationToken shutdownToken)
        {
            if (shutdownToken.IsCancellationRequested)
            {
                return;
            }

            await applyMuteStateAsync();
        }
    }
}
