using System.Diagnostics;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppAudioCommandCoordinator
    {
        private int _audioStatusCaptureInFlight;

        public void ShowAudioStatus()
        {
            if (!overlay.IsOverlayEnabled() || Volatile.Read(ref _mediaShutdownStarted) != 0 ||
                Interlocked.CompareExchange(ref _audioStatusCaptureInFlight, 1, 0) != 0)
            {
                return;
            }

            int requestVersion = 0;
            long started = Stopwatch.GetTimestamp();
            try
            {
                lock (_mediaLifecycleLock)
                {
                    if (Volatile.Read(ref _mediaShutdownStarted) != 0) return;
                    requestVersion = BeginMediaFeedbackRequest();
                }
                AudioStatusSnapshot snapshot = audioStatusReader?.Invoke() ?? audio.GetAudioStatus();
                if (IsMediaFeedbackCurrent(requestVersion))
                {
                    overlay.ShowAudioStatus(snapshot, () => IsMediaFeedbackCurrent(requestVersion));
                    logger.Debug("AppAudioCommandCoordinator", () => FormattableString.Invariant($"audio-status-overlay | output={snapshot.Output.Kind} input={snapshot.Input.Kind} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}"));
                }
            }
            catch (Exception ex)
            {
                logger.Warning("AppAudioCommandCoordinator", $"audio-status-overlay-failed | error={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
                if (IsMediaFeedbackCurrent(requestVersion))
                {
                    overlay.ShowAudioStatus(new(new(AudioEndpointStatusKind.Unavailable), new(AudioEndpointStatusKind.Unavailable)), () => IsMediaFeedbackCurrent(requestVersion));
                }
            }
            finally
            {
                Volatile.Write(ref _audioStatusCaptureInFlight, 0);
            }
        }
    }
}
