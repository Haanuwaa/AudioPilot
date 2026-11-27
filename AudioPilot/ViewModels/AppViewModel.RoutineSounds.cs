using System.Diagnostics;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private object? _activeRoutineCue;

    private void QueueRoutineCue(string routineId, bool isManual, Func<bool> isCurrent)
    {
        if (!CanPlay() || Volatile.Read(ref _activeRoutineCue) != null) return;
        var request = new object();
        if (Interlocked.CompareExchange(ref _activeRoutineCue, request, null) != null) return;

        long queuedAt = Stopwatch.GetTimestamp();
        if (!TryRunBackgroundWork(async token =>
        {
            try
            {
                await RoutineSoundPlayer.PlayAsync(
                    () => CanPlay() && !token.IsCancellationRequested && Stopwatch.GetElapsedTime(queuedAt) < TimeSpan.FromSeconds(1),
                    token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.Debug("RoutineSound", () => $"routine-sound-unavailable | error={ex.GetType().Name}");
            }
            finally { Finish(); }
        }, nameof(QueueRoutineCue), onDiscarded: Finish)) Finish();

        bool CanPlay()
        {
            if (!_dialogs.SoundsEnabled || _isCleaningUp || IsRuntimeShutdownRequested || IsUpdateAdmissionBlocked ||
                Deafen || MuteSound || AudioTesting.IsAudioTestActive || IsMicrophoneHoldControlling?.Invoke() == true || !isCurrent())
                return false;
            if (isManual) return true;
            lock (_settingsLock)
                return _cachedSettings?.Routines.Items.Any(routine => routine.Enabled && routine.PlaySoundAfterApplying &&
                    string.Equals(routine.Id, routineId, StringComparison.OrdinalIgnoreCase)) == true;
        }

        void Finish() => Interlocked.CompareExchange(ref _activeRoutineCue, null, request);
    }
}
