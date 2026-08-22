using AudioPilot.Logging;

namespace AudioPilot.Services.Routines;

internal interface IRoutineAudioChange : IDisposable
{
    void Restore();
}

internal readonly record struct RoutineMuteApplicationResult(bool Success, IRoutineAudioChange? Change = null, string? FailureDetail = null);

/// <summary>Owns endpoint volume, mute, and default-role changes shared by all active triggers of one routine.</summary>
internal sealed class RoutineAudioRestoration(IEnumerable<IRoutineAudioChange> changes, Logger logger) : IDisposable
{
    private IRoutineAudioChange[]? _changes = [.. changes];

    internal void Complete(bool restore)
    {
        IRoutineAudioChange[]? owned = Interlocked.Exchange(ref _changes, null);
        if (owned == null) return;
        for (int index = owned.Length - 1; index >= 0; index--)
        {
            IRoutineAudioChange change = owned[index];
            try { if (restore) change.Restore(); }
            catch (Exception ex) { logger.Warning("RoutineAudio", "routine-audio-restore-failed", nameof(Complete), ex); }
            finally
            {
                try { change.Dispose(); }
                catch (Exception ex) { logger.Warning("RoutineAudio", "routine-audio-release-failed", nameof(Complete), ex); }
            }
        }
    }

    public void Dispose() => Complete(false);
}
