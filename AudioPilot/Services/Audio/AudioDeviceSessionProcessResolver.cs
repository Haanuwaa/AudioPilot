using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal class AudioDeviceSessionProcessResolver(
        Logger logger,
        Func<uint, (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)?> getCachedProcessInfo,
        Func<long, bool> isCacheEntryExpired)
    {
        private readonly Logger _logger = logger;
        private readonly Func<uint, (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)?> _getCachedProcessInfo = getCachedProcessInfo;
        private readonly Func<long, bool> _isCacheEntryExpired = isCacheEntryExpired;

        public virtual bool TryResolveProcessName(uint pid, out string processName)
        {
            processName = string.Empty;
            if (pid == 0)
            {
                return false;
            }

            var cachedInfo = _getCachedProcessInfo(pid);
            if (cachedInfo.HasValue && !_isCacheEntryExpired(cachedInfo.Value.TimestampTicks))
            {
                processName = cachedInfo.Value.ProcessName;
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
                return true;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"Skipping new session: failed to resolve process name ({ex.GetType().Name})");
                }

                return false;
            }
        }
    }
}
