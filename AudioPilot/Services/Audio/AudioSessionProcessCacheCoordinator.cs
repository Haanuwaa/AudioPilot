using System.Collections.Concurrent;
using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioSessionProcessCacheCoordinator(
        Logger logger,
        TimeSpan cacheEntryTtl,
        Func<long>? timestampProvider = null) : IDisposable
    {
        internal readonly record struct CacheEntry(
            string ProcessName,
            string? DisplayName,
            string? MainWindowTitle,
            long TimestampTicks)
        {
            public bool IsExpired(long nowTimestampTicks, long ttlTicks)
            {
                long elapsedTicks = nowTimestampTicks - TimestampTicks;
                return elapsedTicks < 0 || elapsedTicks > ttlTicks;
            }

            public static CacheEntry Create(string processName, string? displayName, string? mainWindowTitle) =>
                new(processName, displayName, mainWindowTitle, GetMonotonicTimestampTicks());
        }

        internal readonly record struct SessionProcessMetadata(
            string ProcessName,
            string DisplayName,
            string? MainWindowTitle);

        private readonly Logger _logger = logger;
        private readonly long _cacheEntryTtlTicks = Math.Max(0, cacheEntryTtl.Ticks);
        private readonly Func<long> _timestampProvider = timestampProvider ?? GetMonotonicTimestampTicks;
        private readonly ConcurrentDictionary<uint, CacheEntry> _processCache = new();
        private readonly ConcurrentBag<List<uint>> _pidCleanupListPool = [];
        private readonly SemaphoreSlim _cleanupLock = new(1, 1);
        private readonly Lock _cleanupStartLock = new();
        private const int MaxProcessCacheEntries = AppConstants.Limits.MaxProcessCacheEntries;
        private const int MaxPooledCleanupListCapacity = AppConstants.Limits.MaxPidProcessMapEntries;
        private CancellationTokenSource? _cleanupCts;
        private Task? _cleanupTask;
        private bool _cleanupPaused;
        private bool _disposed;

        internal Task? CleanupTaskForTests => _cleanupTask;
        internal int ProcessCacheCount => _processCache.Count;
        internal bool IsCleanupLoopStarted => _cleanupCts != null;

        internal (string ProcessName, string? DisplayName, string? MainWindowTitle, long TimestampTicks)? GetCachedProcessInfo(uint pid)
        {
            if (_processCache.TryGetValue(pid, out var entry))
            {
                return (entry.ProcessName, entry.DisplayName, entry.MainWindowTitle, entry.TimestampTicks);
            }

            return null;
        }

        internal bool IsCacheEntryExpired(long timestampTicks) =>
            new CacheEntry(string.Empty, null, null, timestampTicks)
                .IsExpired(_timestampProvider(), _cacheEntryTtlTicks);

        internal Task StartCleanupTaskAsync(Func<bool> isDisposed)
        {
            lock (_cleanupStartLock)
            {
                if (_disposed || isDisposed() || _cleanupPaused || _cleanupCts != null)
                {
                    return Task.CompletedTask;
                }

                var cleanupCts = new CancellationTokenSource();
                _cleanupCts = cleanupCts;
                CancellationToken cleanupToken = cleanupCts.Token;
                _cleanupTask = Task.Run(() => CleanupLoopAsync(cleanupToken), cleanupToken);
            }

            return Task.CompletedTask;
        }

        internal void AddProcessCacheEntryForTests(uint pid, string processName, string? displayName, string? mainWindowTitle, long timestampTicks)
        {
            _processCache[pid] = new CacheEntry(processName, displayName, mainWindowTitle, timestampTicks);
        }

        internal void TrimProcessCacheForTests()
        {
            TrimProcessCacheIfNeeded();
        }

        internal bool TryGetOrAddEntry(
            uint processId,
            Func<CacheEntry?> cacheEntryFactory,
            out CacheEntry entry)
        {
            if (_processCache.TryGetValue(processId, out var cachedEntry) &&
                !cachedEntry.IsExpired(_timestampProvider(), _cacheEntryTtlTicks))
            {
                entry = cachedEntry;
                return true;
            }

            CacheEntry? createdEntry = cacheEntryFactory();
            if (createdEntry == null)
            {
                entry = default;
                return false;
            }

            entry = createdEntry.Value with { TimestampTicks = _timestampProvider() };
            _processCache[processId] = entry;
            TrimProcessCacheIfNeeded();
            return true;
        }

        internal bool TryGetSessionProcessMetadata(
            uint processId,
            AudioSessionControl session,
            AudioSessionService.DeferredWindowPidMap windowPidMap,
            out SessionProcessMetadata metadata)
        {
            metadata = default;

            if (!TryGetOrAddEntry(
                processId,
                () =>
                {
                    (string processName, string? displayName, string? mainWindowTitle) = ResolveProcessInfoSync(processId, session, windowPidMap);
                    return processName == null
                        ? null
                        : CacheEntry.Create(processName, displayName, mainWindowTitle);
                },
                out var cacheEntry))
            {
                return false;
            }

            return TryProjectSessionProcessMetadata(cacheEntry, out metadata);
        }

        private (string processName, string? displayName, string? mainWindowTitle) ResolveProcessInfoSync(
            uint processId,
            AudioSessionControl session,
            AudioSessionService.DeferredWindowPidMap windowPidMap)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;
                string? displayName = null;
                try
                {
                    displayName = session.DisplayName;
                }
                catch (Exception ex)
                {
                    _logger.Trace("AudioSessionService", () => $"Failed to get session display name for process {LogPrivacy.Id(processId.ToString())}: {ex.GetType().Name}");
                }
                string? mainWindowTitle = null;

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return (AudioDeviceHelper.SanitizeProcessName(processName), displayName, mainWindowTitle);
                }

                try
                {
                    mainWindowTitle = process.MainWindowTitle;
                }
                catch (Exception ex)
                {
                    _logger.Trace("AudioSessionService", () => $"Failed to get MainWindowTitle for process {LogPrivacy.Id(processId.ToString())}: {ex.GetType().Name}");
                }

                if (!string.IsNullOrWhiteSpace(mainWindowTitle) &&
                    !AudioDeviceHelper.IsInternalWindowTitle(mainWindowTitle))
                {
                    displayName = mainWindowTitle;
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    return (AudioDeviceHelper.SanitizeProcessName(processName), displayName, mainWindowTitle);
                }

                int parentPid = AudioDeviceHelper.GetParentPid((int)processId);
                bool isActualChild = parentPid != 0 && parentPid != (int)processId;
                bool isKnownHelper = AppConstants.Audio.KnownHelperProcesses
                    .Contains(processName);
                bool isChildProcess = isActualChild && isKnownHelper;

                if (isChildProcess)
                {
                    string? registeredName = AudioDeviceHelper.GetProcessRegisteredName((uint)parentPid, 0, windowPidMap.GetOrCreate());
                    if (!string.IsNullOrEmpty(registeredName))
                    {
                        processName = registeredName;
                    }
                    else
                    {
                        using var realAppProcess = AudioDeviceHelper.FindRealAppProcess(process);
                        processName = realAppProcess != null
                            ? AudioDeviceHelper.SanitizeProcessName(realAppProcess.ProcessName)
                            : AudioDeviceHelper.SanitizeProcessName(processName);
                        displayName = AudioDeviceHelper.CapitalizeName(processName);
                    }
                }
                else
                {
                    string? fileDesc = AudioDeviceHelper.GetFileDescription((int)processId);
                    processName = !string.IsNullOrEmpty(fileDesc)
                        ? fileDesc
                        : AudioDeviceHelper.SanitizeProcessName(processName);
                    if (string.IsNullOrEmpty(fileDesc)) displayName = AudioDeviceHelper.CapitalizeName(processName);
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = processName;
                }

                return (processName, displayName, mainWindowTitle);
            }
            catch (ArgumentException)
            {
                return (null!, null, null);
            }
            catch (InvalidOperationException)
            {
                return (null!, null, null);
            }
            finally
            {
                process?.Dispose();
            }
        }

        internal static bool TryProjectSessionProcessMetadata(
            string processName,
            string? displayName,
            string? mainWindowTitle,
            out SessionProcessMetadata metadata)
        {
            return TryProjectSessionProcessMetadata(CacheEntry.Create(processName, displayName, mainWindowTitle), out metadata);
        }

        internal static bool TryProjectSessionProcessMetadata(CacheEntry cacheEntry, out SessionProcessMetadata metadata)
        {
            string finalDisplayName = AudioDeviceHelper.GetSessionDisplayNameFromCache(
                cacheEntry.ProcessName,
                cacheEntry.DisplayName);

            if (AudioDeviceHelper.ShouldIgnoreSessionFromCache(finalDisplayName, cacheEntry.ProcessName))
            {
                metadata = default;
                return false;
            }

            metadata = new SessionProcessMetadata(
                cacheEntry.ProcessName,
                finalDisplayName,
                cacheEntry.MainWindowTitle);
            return true;
        }

        internal static bool ShouldSkipSelfSession(uint processId, int currentProcessId)
        {
            return processId != 0 && processId == (uint)currentProcessId;
        }

        internal void Clear()
        {
            _processCache.Clear();
            while (_pidCleanupListPool.TryTake(out _))
            {
            }
        }

        internal async Task PauseCleanupTaskAsync()
        {
            CancellationTokenSource? cleanupCts;
            Task? cleanupTask;

            lock (_cleanupStartLock)
            {
                _cleanupPaused = true;
                if (_disposed || _cleanupCts == null)
                {
                    Clear();
                    return;
                }

                cleanupCts = _cleanupCts;
                cleanupTask = _cleanupTask;
            }

            try
            {
                cleanupCts.Cancel();
                if (cleanupTask != null)
                {
                    await cleanupTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioSessionService", () => $"Cleanup task pause failed: {ex.GetType().Name}");
                }
            }
            bool ownsCleanupLoop;
            bool shouldRestart;
            lock (_cleanupStartLock)
            {
                ownsCleanupLoop = !_disposed && ReferenceEquals(_cleanupCts, cleanupCts);
                shouldRestart = ownsCleanupLoop && !_cleanupPaused;
                if (ownsCleanupLoop)
                {
                    _cleanupCts = null;
                    _cleanupTask = null;
                }
            }

            if (!ownsCleanupLoop)
            {
                return;
            }

            cleanupCts.Dispose();
            Clear();

            if (shouldRestart)
            {
                await StartCleanupTaskAsync(static () => false).ConfigureAwait(false);
            }
        }

        internal Task ResumeCleanupTaskAsync(Func<bool> isDisposed)
        {
            lock (_cleanupStartLock)
            {
                if (_disposed || isDisposed())
                {
                    return Task.CompletedTask;
                }

                _cleanupPaused = false;
            }

            return StartCleanupTaskAsync(isDisposed);
        }

        private async Task CleanupLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(AppConstants.Timing.CacheCleanupIntervalMs, cancellationToken);
                    CleanupExpiredEntries();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("AudioSessionService", "Cleanup loop error", nameof(CleanupLoopAsync), ex);
                }
            }
        }

        private void CleanupExpiredEntries()
        {
            if (!_cleanupLock.Wait(0))
            {
                return;
            }

            List<uint>? processCacheExpired = null;
            try
            {
                processCacheExpired = RentPidCleanupList();

                foreach (var kvp in _processCache)
                {
                    if (kvp.Value.IsExpired(_timestampProvider(), _cacheEntryTtlTicks))
                    {
                        processCacheExpired.Add(kvp.Key);
                    }
                }

                foreach (var pid in processCacheExpired)
                {
                    _processCache.TryRemove(pid, out _);
                }

                TrimProcessCacheIfNeeded();

                int cleanedCount = processCacheExpired.Count;
                if (cleanedCount > 0 && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioSessionService", () => $"Cleaned {cleanedCount} expired cache entries");
                }
            }
            finally
            {
                ReturnPidCleanupList(processCacheExpired);
                _cleanupLock.Release();
            }
        }

        private List<uint> RentPidCleanupList()
        {
            if (_pidCleanupListPool.TryTake(out var list))
            {
                list.Clear();
                return list;
            }

            return [];
        }

        private void ReturnPidCleanupList(List<uint>? list)
        {
            if (list == null)
            {
                return;
            }

            int capacity = list.Capacity;
            list.Clear();
            if (capacity > MaxPooledCleanupListCapacity)
            {
                return;
            }

            _pidCleanupListPool.Add(list);
        }

        private void TrimProcessCacheIfNeeded()
        {
            int count = _processCache.Count;
            if (count <= MaxProcessCacheEntries)
            {
                return;
            }

            var orderedEntries = _processCache.ToArray();
            Array.Sort(orderedEntries, static (left, right) => left.Value.TimestampTicks.CompareTo(right.Value.TimestampTicks));

            int entriesToRemove = count - MaxProcessCacheEntries;
            int removed = 0;
            for (int index = 0; index < orderedEntries.Length && removed < entriesToRemove; index++)
            {
                if (_processCache.TryRemove(orderedEntries[index].Key, out _))
                {
                    removed++;
                }
            }

            if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AudioSessionService", () => $"Trimmed process cache entries: removed={removed}, remaining={_processCache.Count}");
            }
        }

        private static long GetMonotonicTimestampTicks() =>
            Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;

        public void Dispose()
        {
            CancellationTokenSource? cleanupCts;
            Task? cleanupTask;

            lock (_cleanupStartLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                cleanupCts = _cleanupCts;
                cleanupTask = _cleanupTask;
                _cleanupCts = null;
                _cleanupTask = null;
            }

            try
            {
                cleanupCts?.Cancel();
                cleanupTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("AudioSessionService", () => $"Cleanup task drain failed during disposal: {ex.GetType().Name}");
                }
            }
            finally
            {
                cleanupCts?.Dispose();
            }

            _cleanupLock.Dispose();
            Clear();
        }
    }
}
