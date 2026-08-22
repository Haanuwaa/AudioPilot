using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    public static partial class AudioDeviceHelper
    {
        public readonly record struct PackagedAppIdentity(
            string DisplayName,
            string AppUserModelId,
            string PackageFamilyName,
            string AppId,
            ImageSource? IconSource = null);
        public readonly record struct VisibleWindowMetadata(string Title, string ClassName);
        public readonly record struct VisibleWindowHandleMetadata(nint WindowHandle, int ProcessId, string Title, string ClassName);

        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public int BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private const int MAX_PARENT_DEPTH = 20;

        private static readonly NativePropertyKey PKEY_AppUserModel_ID = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

        [LibraryImport("shell32.dll")]
        private static partial int SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid riid,
            out IntPtr ppv);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        private static partial int GetWindowTextLength(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static readonly FrozenSet<string> KnownHelperCache =
            AppConstants.Audio.KnownHelperProcesses.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> IgnoredProcessCache =
            AppConstants.Audio.IgnoredProcesses.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenDictionary<int, string> HResultLookup =
            new Dictionary<int, string>
            {
                { unchecked((int)0x80070490), "Element not found (ERROR_NOT_FOUND)" },
                { unchecked((int)0x80070005), "Access denied (E_ACCESSDENIED)" },
                { unchecked((int)0x8007000E), "Out of memory (E_OUTOFMEMORY)" },
                { unchecked((int)0x800706BA), "RPC server unavailable (RPC_S_SERVER_UNAVAILABLE)" },
                { unchecked((int)0x800706BF), "RPC call failed (RPC_S_CALL_FAILED)" },
                { unchecked((int)0x800401F0), "Not initialized (CO_E_NOTINITIALIZED)" },
                { unchecked((int)0x800401F3), "Invalid class string (REGDB_E_CLASSNOTREG)" },
                { unchecked((int)0x800401F5), "Object not registered (CO_E_OBJNOTREG)" },
                { unchecked((int)0x800704A6), "Device not connected (ERROR_DEVICE_NOT_CONNECTED)" },
                { unchecked((int)0x8007045B), "System shutdown (ERROR_SHUTDOWN_IN_PROGRESS)" },
                { unchecked((int)0x80040201), "Invalid stream (AUDCLNT_E_INVALID_STREAM_FLAG)" },
                { unchecked((int)0x88890003), "Device in use (AUDCLNT_E_DEVICE_IN_USE)" },
                { unchecked((int)0x88890004), "Engine already initialized (AUDCLNT_E_ENGINE_ALREADY_INITIALIZED)" },
                { unchecked((int)0x88890006), "Engine not initialized (AUDCLNT_E_ENGINE_NOT_INITIALIZED)" },
                { unchecked((int)0x8889000A), "Incorrect buffer size (AUDCLNT_E_INCORRECT_BUFFER_SIZE)" },
                { unchecked((int)0x8889000F), "CPU usage exhausted (AUDCLNT_E_CPUUSAGE_EXCEEDED)" }
            }.ToFrozenDictionary();

        private readonly record struct CacheEntry(string? Value, long Expiry);
        private readonly record struct ProcessNameResolution(string? Name, ProcessNameResolutionSource Source);

        private enum ProcessNameResolutionSource
        {
            Window,
            FileDescription,
            SanitizedProcessName,
        }

        private static readonly long CacheExpiryTicks =
            (long)TimeSpan.FromMinutes(AppConstants.Timing.CacheEntryExpiryMinutes).TotalMilliseconds;
        private static readonly long FileDescriptionCacheExpiryTicks =
            (long)TimeSpan.FromSeconds(AppConstants.Timing.ProcessCacheTtlSeconds).TotalMilliseconds;

        private static readonly long WindowPidMapCacheDuration =
            (long)TimeSpan.FromSeconds(AppConstants.Timing.SessionCacheShortTtlSeconds).TotalMilliseconds;
        private static readonly long PackagedAppInventoryCacheDuration =
            (long)TimeSpan.FromMinutes(AppConstants.Timing.PackagedAppInventoryCacheMinutes).TotalMilliseconds;
        private const int MaxWindowPidMapCacheEntries = AppConstants.Limits.MaxPidProcessMapEntries;
        private const int MaxPackagedAppInventoryCacheEntries = AppConstants.Limits.MaxPackagedAppInventoryEntries;
        private const int MaxFileDescriptionCacheEntries = AppConstants.Limits.MaxFileDescriptionCacheEntries;
        private const int MaxAumidCacheEntries = AppConstants.Limits.MaxAumidCacheEntries;
        private const int CacheTrimIntervalMask = 0x3F;

        private static ConcurrentDictionary<int, CacheEntry> FileDescriptionCache = new();
        private static ConcurrentDictionary<string, CacheEntry> AumidCache = new();
        private static ConcurrentDictionary<IntPtr, uint>? WindowPidMapCache;
        private static IReadOnlyList<PackagedAppIdentity>? PackagedAppInventoryCache;
        private static readonly Lock WindowPidMapCacheLock = new();
        private static readonly Lock PackagedAppInventoryLock = new();
        private static long WindowPidMapCacheExpiry;
        private static long PackagedAppInventoryCacheExpiry;
        private static int CacheTrimCounter;

        internal static IReadOnlyDictionary<IntPtr, uint>? WindowPidMapCacheForTests => WindowPidMapCache;
        internal static long WindowPidMapCacheExpiryForTests => Volatile.Read(ref WindowPidMapCacheExpiry);
        internal static IReadOnlyList<PackagedAppIdentity>? PackagedAppInventoryCacheForTests => PackagedAppInventoryCache;
        internal static long PackagedAppInventoryCacheExpiryForTests => Volatile.Read(ref PackagedAppInventoryCacheExpiry);

        internal static void SeedFileDescriptionCacheForTests(int pid, string? value, long expiry)
        {
            FileDescriptionCache[pid] = new CacheEntry(value, expiry);
        }

        internal static bool TryGetFileDescriptionCacheEntryForTests(int pid, out string? value, out long expiry)
        {
            if (FileDescriptionCache.TryGetValue(pid, out CacheEntry entry))
            {
                value = entry.Value;
                expiry = entry.Expiry;
                return true;
            }

            value = null;
            expiry = 0;
            return false;
        }

        internal static void SeedAumidCacheForTests(string aumid, string? value, long expiry)
        {
            AumidCache[aumid] = new CacheEntry(value, expiry);
        }

        internal static bool TryGetAumidCacheEntryForTests(string aumid, out string? value, out long expiry)
        {
            if (AumidCache.TryGetValue(aumid, out CacheEntry entry))
            {
                value = entry.Value;
                expiry = entry.Expiry;
                return true;
            }

            value = null;
            expiry = 0;
            return false;
        }

        internal static void SeedWindowPidMapCacheForTests(IReadOnlyDictionary<IntPtr, uint> cache, long expiry)
        {
            WindowPidMapCache = new ConcurrentDictionary<IntPtr, uint>(cache);
            Volatile.Write(ref WindowPidMapCacheExpiry, expiry);
        }

        internal static void SeedPackagedAppInventoryCacheForTests(IReadOnlyList<PackagedAppIdentity> cache, long expiry)
        {
            PackagedAppInventoryCache = cache;
            Volatile.Write(ref PackagedAppInventoryCacheExpiry, expiry);
        }

        private static void Trace(string message)
        {
            var logger = Logger.Instance;
            if (!logger.IsEnabled(LogLevel.Trace))
                return;

            logger.Trace("AudioDeviceHelper", message);
        }

        private static void Trace(Func<string> messageFactory)
        {
            var logger = Logger.Instance;
            if (!logger.IsEnabled(LogLevel.Trace))
                return;

            logger.Trace("AudioDeviceHelper", messageFactory());
        }

        private static void StoreCacheEntry<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, TKey key, string? value, long now)
            where TKey : notnull
        {
            StoreCacheEntry(cache, key, value, now, CacheExpiryTicks);
        }

        private static void StoreCacheEntry<TKey>(
            ConcurrentDictionary<TKey, CacheEntry> cache,
            TKey key,
            string? value,
            long now,
            long cacheDuration)
            where TKey : notnull
        {
            cache[key] = new CacheEntry(value, now + Math.Max(0, cacheDuration));
            TrimCachesIfNeeded(now);
        }

        private static bool TryGetCachedValue<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, TKey key, long now, out string? value)
            where TKey : notnull
        {
            if (cache.TryGetValue(key, out CacheEntry cached) && cached.Expiry > now)
            {
                value = cached.Value;
                return true;
            }

            cache.TryRemove(key, out _);
            value = null;
            return false;
        }

        private static string? StoreCachedLookupResult<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, TKey key, string? value, long now)
            where TKey : notnull
        {
            return StoreCachedLookupResult(cache, key, value, now, CacheExpiryTicks);
        }

        private static string? StoreCachedLookupResult<TKey>(
            ConcurrentDictionary<TKey, CacheEntry> cache,
            TKey key,
            string? value,
            long now,
            long cacheDuration)
            where TKey : notnull
        {
            StoreCacheEntry(cache, key, value, now, cacheDuration);
            return value;
        }

        private static bool TryGetExpiringSnapshot<T>(T? cachedValue, long expiry, long now, [NotNullWhen(true)] out T? value)
            where T : class
        {
            if (cachedValue != null && expiry > now)
            {
                value = cachedValue;
                return true;
            }

            value = null;
            return false;
        }

        private static T PublishExpiringSnapshot<T>(ref T? cache, ref long expiry, T value, long duration, long now)
            where T : class
        {
            Interlocked.Exchange(ref cache, value);
            Interlocked.Exchange(ref expiry, now + duration);
            return value;
        }

        public static void ClearCaches()
        {
            Interlocked.Exchange(ref FileDescriptionCache, new ConcurrentDictionary<int, CacheEntry>());
            Interlocked.Exchange(ref AumidCache, new ConcurrentDictionary<string, CacheEntry>());
            ClearWindowPidMapCache();
            ClearPackagedAppInventoryCache();
            Logger.Instance.Info("AudioDeviceHelper", "All caches cleared");
        }

        internal static void ClearPackagedAppInventoryCache()
        {
            lock (PackagedAppInventoryLock)
            {
                Interlocked.Exchange(ref PackagedAppInventoryCache, null);
                Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
            }
        }

        private static void ClearWindowPidMapCache()
        {
            lock (WindowPidMapCacheLock)
            {
                Interlocked.Exchange(ref WindowPidMapCache, null);
                Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
            }
        }

        internal static void TrimCachesForHiddenMode()
        {
            long now = Environment.TickCount64;
            TrimCache(FileDescriptionCache, MaxFileDescriptionCacheEntries, now);
            TrimCache(AumidCache, MaxAumidCacheEntries, now);
            ClearWindowPidMapCache();
            ClearPackagedAppInventoryCache();
        }

        private static void TrimCachesIfNeeded(long now)
        {
            if ((Interlocked.Increment(ref CacheTrimCounter) & CacheTrimIntervalMask) != 0)
                return;

            TrimCache(FileDescriptionCache, MaxFileDescriptionCacheEntries, now);
            TrimCache(AumidCache, MaxAumidCacheEntries, now);
        }

        private static void TrimCache<TKey>(ConcurrentDictionary<TKey, CacheEntry> cache, int maxEntries, long now)
            where TKey : notnull
        {
            if (cache.Count <= maxEntries)
                return;

            foreach (var kvp in cache)
            {
                if (kvp.Value.Expiry <= now)
                    cache.TryRemove(kvp.Key, out _);
            }

            int countAfterExpiryTrim = cache.Count;
            if (countAfterExpiryTrim <= maxEntries)
                return;

            int overflow = countAfterExpiryTrim - maxEntries;
            var oldestByExpiry = new List<(TKey Key, long Expiry)>(countAfterExpiryTrim);
            foreach (var kvp in cache)
            {
                oldestByExpiry.Add((kvp.Key, kvp.Value.Expiry));
            }

            oldestByExpiry.Sort(static (left, right) => left.Expiry.CompareTo(right.Expiry));
            int removals = Math.Min(overflow, oldestByExpiry.Count);
            for (int i = 0; i < removals; i++)
            {
                cache.TryRemove(oldestByExpiry[i].Key, out _);
            }
        }

        public static void LogComException(ILogger logger, string methodName, COMException ex)
        {
            logger.Error(
                "AudioDeviceService",
                () => $"COM Exception - HRESULT: 0x{ex.ErrorCode:X8}, Type: {ex.GetType().Name}",
                methodName,
                ex);

            if (HResultLookup.TryGetValue(ex.ErrorCode, out var message))
                logger.Trace("AudioDeviceService", () => $"  -> {message}", methodName);
            else
                logger.Trace(
                    "AudioDeviceService",
                    "  -> Unknown HRESULT - You may need to look up this code",
                    methodName);
        }

        public static void LogException(ILogger logger, string methodName, Exception ex)
        {
            logger.Error(
                "AudioDeviceService",
                () => $"Exception - Type: {ex.GetType().Name}",
                methodName,
                ex);

            if (ex.InnerException != null)
            {
                logger.Trace(
                    "AudioDeviceService",
                    () => $"  -> Inner Exception: {ex.InnerException.GetType().Name}",
                    methodName);
            }
        }

    }
}
