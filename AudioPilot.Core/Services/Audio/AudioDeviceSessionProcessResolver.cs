using System.Collections.Concurrent;
using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal class AudioDeviceSessionProcessResolver(
        Logger logger,
        Func<uint, (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)?> getCachedProcessInfo,
        Func<long, bool> isCacheEntryExpired)
    {
        private const long ExecutableNameCacheTtlMs = 2000;
        private const int MaxExecutableNameCacheEntries = 256;
        private readonly Logger _logger = logger;
        private readonly Func<uint, (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)?> _getCachedProcessInfo = getCachedProcessInfo;
        private readonly Func<long, bool> _isCacheEntryExpired = isCacheEntryExpired;
        private readonly ConcurrentDictionary<uint, (string ProcessName, long ExpiryTick)> _executableNameCache = new();

        public virtual bool TryResolveProcessName(uint pid, out string processName)
        {
            processName = string.Empty;
            if (pid == 0 || pid > int.MaxValue)
            {
                return false;
            }

            long now = Environment.TickCount64;
            if (_executableNameCache.TryGetValue(pid, out var cachedExecutable) && now < cachedExecutable.ExpiryTick)
            {
                processName = cachedExecutable.ProcessName;
                return true;
            }

            string? imagePath = ProcessEnumerationHelper.TryGetImagePath((int)pid);
            if (!string.IsNullOrEmpty(imagePath))
            {
                string resolved = Path.GetFileNameWithoutExtension(imagePath);
                if (!string.IsNullOrEmpty(resolved))
                {
                    StoreExecutableName(pid, resolved, now);
                    processName = resolved;
                    return true;
                }
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
                if (!string.IsNullOrEmpty(processName))
                {
                    StoreExecutableName(pid, processName, now);
                }
                return true;
            }
            catch (Exception ex)
            {
                var cachedInfo = _getCachedProcessInfo(pid);
                if (cachedInfo.HasValue && !_isCacheEntryExpired(cachedInfo.Value.TimestampTicks))
                {
                    processName = cachedInfo.Value.ProcessName;
                    return true;
                }
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioDeviceService", () => $"Skipping new session: failed to resolve process name ({ex.GetType().Name})");
                }

                return false;
            }
        }

        private void StoreExecutableName(uint pid, string processName, long now)
        {
            if (_executableNameCache.Count >= MaxExecutableNameCacheEntries)
            {
                foreach (var kvp in _executableNameCache)
                {
                    if (now >= kvp.Value.ExpiryTick)
                    {
                        _executableNameCache.TryRemove(kvp);
                    }
                }

                if (_executableNameCache.Count >= MaxExecutableNameCacheEntries)
                {
                    _executableNameCache.Clear();
                }
            }

            _executableNameCache[pid] = (processName, now + ExecutableNameCacheTtlMs);
        }
    }
}
