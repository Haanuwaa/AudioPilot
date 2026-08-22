using System.Diagnostics;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppCliOverlayCoordinator
    {
        private int _audioStatusCaptureInFlight;

        public void ShowAudioStatus()
        {
            if (!overlay.IsOverlayEnabled() || Volatile.Read(ref _mediaShutdownStarted) != 0 ||
                Interlocked.CompareExchange(ref _audioStatusCaptureInFlight, 1, 0) != 0)
            {
                return;
            }

            int requestVersion = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
            long started = Stopwatch.GetTimestamp();
            try
            {
                AudioStatusSnapshot snapshot = audioStatusReader?.Invoke() ?? audio.GetAudioStatus();
                if (IsMediaFeedbackCurrent(requestVersion))
                {
                    overlay.ShowAudioStatus(snapshot, () => IsMediaFeedbackCurrent(requestVersion));
                    logger.Debug("AppCliOverlayCoordinator", () => FormattableString.Invariant($"audio-status-overlay | output={snapshot.Output.Kind} input={snapshot.Input.Kind} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}"));
                }
            }
            catch (Exception ex)
            {
                logger.Warning("AppCliOverlayCoordinator", $"audio-status-overlay-failed | error={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
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
