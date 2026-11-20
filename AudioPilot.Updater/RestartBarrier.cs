namespace AudioPilot.Updater;

internal enum RestartReadiness { Ready, MicrophoneOwned, SaveOrDrainFailed }

/// <summary>The caller must suspend new audio/hold work before checking ownership and keep it suspended through process exit.</summary>
internal static class RestartBarrier
{
    public static async Task<RestartReadiness> PrepareAsync(Func<bool> microphoneOwned,
        Func<CancellationToken, Task<bool>> flushAndDrain, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (microphoneOwned()) return RestartReadiness.MicrophoneOwned;
        if (!await flushAndDrain(token).ConfigureAwait(false)) return RestartReadiness.SaveOrDrainFailed;
        token.ThrowIfCancellationRequested();
        return microphoneOwned() ? RestartReadiness.MicrophoneOwned : RestartReadiness.Ready;
    }
}
