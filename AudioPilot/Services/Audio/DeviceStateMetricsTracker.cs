using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class DeviceStateMetricsTracker
    {
        private readonly Lock _lock = new();
        private DateTime _metricsWindowStart = DateTime.UtcNow;
        private int _metricsCount;
        private int _stormWarnings;
        private DateTime _summaryWindowStart = DateTime.UtcNow;
        private int _summaryCount;

        public void TrackAndLog(Logger logger)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _metricsWindowStart;
                _summaryCount++;

                if (elapsed > TimeSpan.FromSeconds(AppConstants.Timing.DeviceStateMetricsWindowSeconds))
                {
                    if (_metricsCount > 0 && logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.Debug("AudioDeviceService", () => $"Device state notifications: {_metricsCount} in {elapsed.TotalSeconds:F1}s");
                    }

                    _metricsWindowStart = now;
                    _metricsCount = 0;
                    elapsed = TimeSpan.Zero;
                }

                _metricsCount++;

                if (_metricsCount >= AppConstants.Timing.DeviceStateStormThreshold && elapsed <= TimeSpan.FromSeconds(AppConstants.Timing.DeviceStateMetricsWindowSeconds) && logger.IsEnabled(LogLevel.Warning))
                {
                    logger.Warning("AudioDeviceService", () => $"Hotplug notification storm detected ({_metricsCount} events in {Math.Max(elapsed.TotalSeconds, 0.1):F1}s)");
                    _stormWarnings++;
                    _metricsWindowStart = now;
                    _metricsCount = 0;
                }

                var summaryElapsed = now - _summaryWindowStart;
                if (summaryElapsed >= TimeSpan.FromSeconds(AppConstants.Timing.DeviceStateSummaryWindowSeconds) && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.Diagnostics.HotplugDiagnostics} | events={_summaryCount} storms={_stormWarnings} windowSeconds={summaryElapsed.TotalSeconds:F0}");
                    _summaryWindowStart = now;
                    _summaryCount = 0;
                    _stormWarnings = 0;
                }
            }
        }
    }
}
