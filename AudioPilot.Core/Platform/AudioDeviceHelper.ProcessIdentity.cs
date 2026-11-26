using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AudioPilot.Platform
{
    public static partial class AudioDeviceHelper
    {
        public static IReadOnlyDictionary<IntPtr, uint> BuildWindowPidMap()
        {
            var now = Environment.TickCount64;
            if (TryGetWindowPidMapSnapshot(now, out Dictionary<IntPtr, uint>? cached))
                return cached;

            lock (WindowPidMapCacheLock)
            {
                now = Environment.TickCount64;
                if (TryGetWindowPidMapSnapshot(now, out cached))
                {
                    return cached;
                }

                var map = new Dictionary<IntPtr, uint>();
                EnumWindows((hWnd, _) =>
                {
                    uint threadId = GetWindowThreadProcessId(hWnd, out uint pid);
                    if (threadId == 0 || pid == 0)
                    {
                        return true;
                    }

                    map[hWnd] = pid;
                    return true;
                }, IntPtr.Zero);

                return PublishWindowPidMapSnapshot(map, now);
            }
        }

        private static bool TryGetWindowPidMapSnapshot(long now, [NotNullWhen(true)] out Dictionary<IntPtr, uint>? cached)
        {
            if (!TryGetExpiringSnapshot(Volatile.Read(ref WindowPidMapCache), Volatile.Read(ref WindowPidMapCacheExpiry), now, out cached))
            {
                cached = null;
                return false;
            }

            if (cached.Count <= MaxWindowPidMapCacheEntries)
            {
                return true;
            }

            int oversizedCount = cached.Count;
            Interlocked.Exchange(ref WindowPidMapCache, null);
            Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
            Trace($"Discarded oversized window PID cache snapshot count={oversizedCount} max={MaxWindowPidMapCacheEntries}");
            cached = null;
            return false;
        }

        private static Dictionary<IntPtr, uint> PublishWindowPidMapSnapshot(Dictionary<IntPtr, uint> map, long now)
        {
            if (map.Count > MaxWindowPidMapCacheEntries)
            {
                Interlocked.Exchange(ref WindowPidMapCache, null);
                Interlocked.Exchange(ref WindowPidMapCacheExpiry, 0);
                Trace(() => $"Skipped caching oversized window PID snapshot count={map.Count} max={MaxWindowPidMapCacheEntries}");
                return map;
            }

            return PublishExpiringSnapshot(ref WindowPidMapCache, ref WindowPidMapCacheExpiry, map, WindowPidMapCacheDuration, now);
        }

        public static IReadOnlyDictionary<int, IReadOnlyList<string>> GetVisibleWindowTitlesByProcessIds(IEnumerable<int>? processIds)
        {
            IReadOnlyDictionary<int, IReadOnlyList<VisibleWindowMetadata>> windowsByProcessId = GetVisibleWindowMetadataByProcessIds(processIds);

            return windowsByProcessId.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)[.. pair.Value.Select(static window => window.Title)],
                EqualityComparer<int>.Default);
        }

        public static IReadOnlyDictionary<int, IReadOnlyList<VisibleWindowMetadata>> GetVisibleWindowMetadataByProcessIds(IEnumerable<int>? processIds)
        {
            if (processIds == null)
            {
                return new Dictionary<int, IReadOnlyList<VisibleWindowMetadata>>();
            }

            HashSet<uint> targetProcessIds = [.. processIds
                .Where(static processId => processId > 0)
                .Select(static processId => (uint)processId)];

            if (targetProcessIds.Count == 0)
            {
                return new Dictionary<int, IReadOnlyList<VisibleWindowMetadata>>();
            }

            IReadOnlyDictionary<IntPtr, uint> windowPidMap = BuildWindowPidMap();
            Dictionary<int, List<VisibleWindowMetadata>> windowsByProcessId = [];

            foreach ((IntPtr windowHandle, uint windowProcessId) in windowPidMap)
            {
                if (!targetProcessIds.Contains(windowProcessId) || !IsWindowVisible(windowHandle))
                {
                    continue;
                }

                string? title = TryGetWindowTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title) || IsInternalWindowTitle(title))
                {
                    continue;
                }

                string className = TryGetWindowClassName(windowHandle) ?? string.Empty;

                int processId = unchecked((int)windowProcessId);
                if (!windowsByProcessId.TryGetValue(processId, out List<VisibleWindowMetadata>? processWindows))
                {
                    processWindows = [];
                    windowsByProcessId[processId] = processWindows;
                }

                bool duplicateExists = processWindows.Any(existing =>
                    string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ClassName, className, StringComparison.OrdinalIgnoreCase));
                if (!duplicateExists)
                {
                    processWindows.Add(new VisibleWindowMetadata(title, className));
                }
            }

            return windowsByProcessId.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<VisibleWindowMetadata>)pair.Value,
                EqualityComparer<int>.Default);
        }

        public static IReadOnlyList<VisibleWindowHandleMetadata> GetVisibleWindowHandleMetadataByProcessIds(IEnumerable<int>? processIds)
        {
            if (processIds == null)
            {
                return [];
            }

            HashSet<uint> targetProcessIds = [..
                processIds
                    .Where(static processId => processId > 0)
                    .Select(static processId => (uint)processId)];

            if (targetProcessIds.Count == 0)
            {
                return [];
            }

            IReadOnlyDictionary<IntPtr, uint> windowPidMap = BuildWindowPidMap();
            var windows = new List<VisibleWindowHandleMetadata>();

            foreach ((IntPtr windowHandle, uint windowProcessId) in windowPidMap)
            {
                if (!targetProcessIds.Contains(windowProcessId) || !IsWindowVisible(windowHandle))
                {
                    continue;
                }

                string? title = TryGetWindowTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title) || IsInternalWindowTitle(title))
                {
                    continue;
                }

                string className = TryGetWindowClassName(windowHandle) ?? string.Empty;
                int processId = unchecked((int)windowProcessId);
                bool duplicateExists = windows.Any(existing =>
                    existing.WindowHandle == windowHandle ||
                    (existing.ProcessId == processId &&
                     string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(existing.ClassName, className, StringComparison.OrdinalIgnoreCase)));
                if (!duplicateExists)
                {
                    windows.Add(new VisibleWindowHandleMetadata(windowHandle, processId, title, className));
                }
            }

            return windows;
        }

        private static unsafe string? TryGetWindowTitle(IntPtr windowHandle)
        {
            Span<char> shortBuffer = stackalloc char[256];
            int copiedLength;
            fixed (char* ptr = shortBuffer)
            {
                copiedLength = GetWindowText(windowHandle, ptr, shortBuffer.Length);
            }

            if (copiedLength <= 0)
            {
                return null;
            }

            if (copiedLength < shortBuffer.Length - 1)
            {
                ReadOnlySpan<char> trimmed = shortBuffer[..copiedLength].Trim();
                return trimmed.IsEmpty ? null : new string(trimmed);
            }

            int capacity = Math.Clamp(GetWindowTextLength(windowHandle) + 1, 512, 32768);
            char[] rented = System.Buffers.ArrayPool<char>.Shared.Rent(capacity);
            try
            {
                fixed (char* ptr = rented)
                {
                    copiedLength = GetWindowText(windowHandle, ptr, capacity);
                }

                if (copiedLength <= 0)
                {
                    return null;
                }

                ReadOnlySpan<char> trimmed = rented.AsSpan(0, copiedLength).Trim();
                return trimmed.IsEmpty ? null : new string(trimmed);
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        internal static string? TryGetWindowClassName(IntPtr windowHandle)
        {
            char[] classBuffer = new char[256];
            int copiedLength = GetClassName(windowHandle, classBuffer, classBuffer.Length);
            if (copiedLength <= 0)
            {
                return null;
            }

            string className = new string(classBuffer, 0, copiedLength).Trim();
            return string.IsNullOrWhiteSpace(className) ? null : className;
        }

        public static string? GetProcessRegisteredName(
            uint pid,
            int depth,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            if (depth >= MAX_PARENT_DEPTH)
                return null;

            if (!TryGetProcessName((int)pid, out string? processName))
            {
                return null;
            }

            if (IsKnownHelperProcessName(processName))
            {
                var parentName = TraverseParentChain((int)pid, depth + 1, windowPidMap);
                if (!string.IsNullOrEmpty(parentName))
                {
                    Trace(() => $"Resolved helper process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(pid.ToString())} to parent={LogPrivacy.Process(parentName)}");
                    return parentName;
                }
            }

            ProcessNameResolution resolution = ResolveProcessName((int)pid, processName, windowPidMap);
            if (resolution.Source == ProcessNameResolutionSource.Window)
            {
                Trace(() => $"Resolved process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(pid.ToString())} via window={LogPrivacy.Process(resolution.Name)}");
            }
            else if (resolution.Source == ProcessNameResolutionSource.FileDescription)
            {
                Trace(() => $"Resolved process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(pid.ToString())} via file-description={LogPrivacy.Process(resolution.Name)}");
            }

            return resolution.Name;
        }

        public static string? GetProcessAppUserModelId(int pid)
        {
            return GetProcessAppUserModelId(pid, BuildWindowPidMap());
        }

        public static string? GetAppUserModelDisplayName(string? appUserModelId)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return null;
            }

            return ResolveAumidToName(appUserModelId);
        }

        public static IReadOnlyList<PackagedAppIdentity> GetInstalledPackagedApps()
        {
            long now = Environment.TickCount64;
            if (TryGetPackagedAppInventorySnapshot(now, out IReadOnlyList<PackagedAppIdentity>? cachedApps))
            {
                return cachedApps;
            }

            lock (PackagedAppInventoryLock)
            {
                now = Environment.TickCount64;
                if (TryGetPackagedAppInventorySnapshot(now, out cachedApps))
                {
                    return cachedApps;
                }

                try
                {
                    string inventorySource = "package-manager";
                    List<PackagedAppIdentity> apps = GetInstalledPackagedAppsFromPackageManager();
                    if (apps.Count == 0)
                    {
                        inventorySource = "registry-fallback";
                        apps = GetInstalledPackagedAppsFromRegistry();
                    }

                    int logoCount = apps.Count(static app => app.LoadIconBytes != null);
                    Trace(() => $"Packaged app inventory refreshed source={inventorySource} count={apps.Count} logoSourceCount={logoCount}");

                    IReadOnlyList<PackagedAppIdentity> cachedResult = [.. apps];
                    return PublishPackagedAppInventorySnapshot(cachedResult, now);
                }
                catch (Exception ex)
                {
                    Trace(() => $"GetInstalledPackagedApps failed: {ex.GetType().Name}");
                    throw;
                }
            }
        }

        private static bool TryGetPackagedAppInventorySnapshot(long now, [NotNullWhen(true)] out IReadOnlyList<PackagedAppIdentity>? cachedApps)
        {
            if (!TryGetExpiringSnapshot(Volatile.Read(ref PackagedAppInventoryCache), Volatile.Read(ref PackagedAppInventoryCacheExpiry), now, out cachedApps))
            {
                cachedApps = null;
                return false;
            }

            if (cachedApps.Count <= MaxPackagedAppInventoryCacheEntries)
            {
                return true;
            }

            int oversizedCount = cachedApps.Count;
            Interlocked.Exchange(ref PackagedAppInventoryCache, null);
            Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
            Trace($"Discarded oversized packaged app inventory cache count={oversizedCount} max={MaxPackagedAppInventoryCacheEntries}");
            cachedApps = null;
            return false;
        }

        private static IReadOnlyList<PackagedAppIdentity> PublishPackagedAppInventorySnapshot(IReadOnlyList<PackagedAppIdentity> cachedResult, long now)
        {
            if (cachedResult.Count > MaxPackagedAppInventoryCacheEntries)
            {
                Interlocked.Exchange(ref PackagedAppInventoryCache, null);
                Interlocked.Exchange(ref PackagedAppInventoryCacheExpiry, 0);
                Trace(() => $"Skipped caching oversized packaged app inventory snapshot count={cachedResult.Count} max={MaxPackagedAppInventoryCacheEntries}");
                return cachedResult;
            }

            return PublishExpiringSnapshot(ref PackagedAppInventoryCache, ref PackagedAppInventoryCacheExpiry, cachedResult, PackagedAppInventoryCacheDuration, now);
        }

        private static List<PackagedAppIdentity> GetInstalledPackagedAppsFromPackageManager()
        {
            var apps = new List<PackagedAppIdentity>();
            try
            {
                var packageManager = new PackageManager();
                var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Package package in packageManager.FindPackagesForUser(string.Empty))
                {
                    string packageFamilyName = string.Empty;
                    IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry>? appEntries;
                    try
                    {
                        packageFamilyName = package.Id.FamilyName;
                        if (string.IsNullOrWhiteSpace(packageFamilyName)) continue;
                        appEntries = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Trace(() => $"GetAppListEntriesAsync failed | package={LogPrivacy.Process(packageFamilyName)} error={ex.GetType().Name}");
                        continue;
                    }

                    if (appEntries == null || appEntries.Count == 0)
                    {
                        continue;
                    }

                    bool includeAppId = appEntries.Count > 1;
                    foreach (Windows.ApplicationModel.Core.AppListEntry appEntry in appEntries)
                    {
                        string appUserModelId;
                        try { appUserModelId = appEntry.AppUserModelId; }
                        catch (Exception ex)
                        {
                            Trace(() => $"Packaged app entry unavailable | package={LogPrivacy.Process(packageFamilyName)} error={ex.GetType().Name}");
                            continue;
                        }
                        AppDisplayInfo? displayInfo = null;
                        string? displayName = null;
                        try { displayInfo = appEntry.DisplayInfo; displayName = displayInfo?.DisplayName; }
                        catch (Exception ex)
                        {
                            Trace(() => $"Packaged app display metadata unavailable | package={LogPrivacy.Process(packageFamilyName)} error={ex.GetType().Name}");
                        }
                        TryAddPackagedAppIdentity(
                            apps,
                            seenAumids,
                            packageFamilyName,
                            appUserModelId,
                            displayName,
                            includeAppId,
                            displayInfo);
                    }
                }

                return OrderPackagedApps(apps);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetInstalledPackagedAppsFromPackageManager failed: {ex.GetType().Name}");
                return OrderPackagedApps(apps);
            }
        }

        private static List<PackagedAppIdentity> GetInstalledPackagedAppsFromRegistry()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\ActivatableClasses\Package");

            if (key == null)
            {
                return [];
            }

            var rawEntries = new List<(string PackageFamilyName, string AppId, string AppUserModelId)>();
            var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string packageFamilyName in key.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    continue;
                }

                using var packageKey = key.OpenSubKey(packageFamilyName);
                if (packageKey == null)
                {
                    continue;
                }

                foreach (string appId in packageKey.GetSubKeyNames())
                {
                    if (string.IsNullOrWhiteSpace(appId))
                    {
                        continue;
                    }

                    string appUserModelId = $"{packageFamilyName}!{appId}";
                    if (seenAumids.Add(appUserModelId))
                    {
                        rawEntries.Add((packageFamilyName, appId, appUserModelId));
                    }
                }
            }

            HashSet<string> multiEntryPackages = rawEntries
                .GroupBy(static entry => entry.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return OrderPackagedApps(rawEntries.Select(entry =>
                CreatePackagedAppIdentity(
                    entry.PackageFamilyName,
                    entry.AppUserModelId,
                    preferredDisplayName: null,
                    multiEntryPackages.Contains(entry.PackageFamilyName))));
        }

        private static bool TryAddPackagedAppIdentity(
            List<PackagedAppIdentity> apps,
            HashSet<string> seenAumids,
            string packageFamilyName,
            string? appUserModelId,
            string? preferredDisplayName,
            bool includeAppId,
            AppDisplayInfo? displayInfo = null)
        {
            string normalizedAppUserModelId = appUserModelId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(packageFamilyName) ||
                string.IsNullOrWhiteSpace(normalizedAppUserModelId) ||
                !seenAumids.Add(normalizedAppUserModelId))
            {
                return false;
            }

            var app = CreatePackagedAppIdentity(packageFamilyName, normalizedAppUserModelId, preferredDisplayName, includeAppId);
            if (displayInfo != null) app = app with { LoadIconBytes = () => TryReadPackagedAppIconBytes(displayInfo) };
            apps.Add(app);
            return true;
        }

        private static PackagedAppIdentity CreatePackagedAppIdentity(
            string packageFamilyName,
            string appUserModelId,
            string? preferredDisplayName,
            bool includeAppId)
        {
            string appId = ExtractPackagedAppId(appUserModelId, packageFamilyName);
            string displayName = ResolvePackagedAppDisplayName(packageFamilyName, appId, preferredDisplayName, includeAppId);
            return new PackagedAppIdentity(displayName, appUserModelId, packageFamilyName, appId);
        }

        private static byte[]? TryReadPackagedAppIconBytes(AppDisplayInfo displayInfo)
        {
            try
            {
                Windows.Storage.Streams.RandomAccessStreamReference logoReference =
                    displayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                using Windows.Storage.Streams.IRandomAccessStreamWithContentType logoStream =
                    logoReference.OpenReadAsync().AsTask().GetAwaiter().GetResult();
                if (logoStream.Size == 0 || logoStream.Size > 2 * 1024 * 1024 || logoStream.Size > uint.MaxValue)
                {
                    return null;
                }

                using var input = logoStream.GetInputStreamAt(0);
                using var reader = new Windows.Storage.Streams.DataReader(input);
                int byteCount = (int)logoStream.Size;
                if (reader.LoadAsync((uint)byteCount).AsTask().GetAwaiter().GetResult() != (uint)byteCount) return null;
                var iconBytes = new byte[byteCount];
                reader.ReadBytes(iconBytes);
                return iconBytes;
            }
            catch (Exception ex)
            {
                Trace(() => $"Packaged app icon unavailable | error={ex.GetType().Name}");
                return null;
            }
        }

        private static string ResolvePackagedAppDisplayName(
            string packageFamilyName,
            string appId,
            string? preferredDisplayName,
            bool includeAppId)
        {
            string displayName = preferredDisplayName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return BuildPackagedAppDisplayName(packageFamilyName, appId, includeAppId);
            }

            if (includeAppId &&
                !string.IsNullOrWhiteSpace(appId) &&
                !string.Equals(appId, "App", StringComparison.OrdinalIgnoreCase) &&
                !displayName.Contains(appId, StringComparison.OrdinalIgnoreCase))
            {
                return $"{displayName} ({appId})";
            }

            return displayName;
        }

        private static List<PackagedAppIdentity> OrderPackagedApps(IEnumerable<PackagedAppIdentity> apps)
        {
            return
            [
                .. apps
                    .OrderBy(static entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static entry => entry.AppUserModelId, StringComparer.OrdinalIgnoreCase)
            ];
        }

        private static string ExtractPackagedAppId(string appUserModelId, string packageFamilyName)
        {
            if (string.IsNullOrWhiteSpace(appUserModelId))
            {
                return string.Empty;
            }

            int separatorIndex = appUserModelId.IndexOf('!');
            if (separatorIndex >= 0 && separatorIndex < appUserModelId.Length - 1)
            {
                return appUserModelId[(separatorIndex + 1)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(packageFamilyName) &&
                appUserModelId.StartsWith(packageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return appUserModelId.Trim();
        }

        internal static string ExtractPackagedAppIdForTests(string appUserModelId, string packageFamilyName)
        {
            return ExtractPackagedAppId(appUserModelId, packageFamilyName);
        }


        private static string? FindPackageFamilyNameForAumid(Microsoft.Win32.RegistryKey rootKey, string aumid)
        {
            int exclamationIndex = aumid.IndexOf('!');
            if (exclamationIndex > 0)
            {
                string packageFamilyName = aumid[..exclamationIndex];
                using var directMatch = rootKey.OpenSubKey(packageFamilyName);
                if (directMatch != null)
                {
                    return packageFamilyName;
                }
            }

            foreach (string subKeyName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subKeyName);
                if (subKey == null)
                    continue;

                foreach (string appId in subKey.GetSubKeyNames())
                {
                    if (string.Equals($"{subKeyName}!{appId}", aumid, StringComparison.OrdinalIgnoreCase))
                    {
                        return subKeyName;
                    }
                }
            }

            return null;
        }

        private static string? ResolveAumidToName(string aumid)
        {
            var now = Environment.TickCount64;
            var cache = Volatile.Read(ref AumidCache);

            if (TryGetCachedValue(cache, aumid, now, out string? cachedName))
                return cachedName;

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\ActivatableClasses\Package");

                if (key == null)
                {
                    return StoreCachedLookupResult(cache, aumid, null, now);
                }

                string? packageFamilyName = FindPackageFamilyNameForAumid(key, aumid);
                if (!string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    string cleaned = CleanPackageName(packageFamilyName);
                    Trace("Resolved AUMID to package display name");
                    return StoreCachedLookupResult(cache, aumid, cleaned, now);
                }

                return StoreCachedLookupResult(cache, aumid, null, now);
            }
            catch (Exception ex)
            {
                Trace(() => $"ResolveAumidToName failed: {ex.GetType().Name}");
                return StoreCachedLookupResult(cache, aumid, null, now);
            }
        }


        private static string CleanPackageName(string packageFamilyName)
        {
            int underscoreIndex = packageFamilyName.IndexOf('_');
            if (underscoreIndex > 0)
                return packageFamilyName[..underscoreIndex].Replace('.', ' ');

            return packageFamilyName;
        }

        internal static string CleanPackageNameForTests(string packageFamilyName)
        {
            return CleanPackageName(packageFamilyName);
        }

        private static string BuildPackagedAppDisplayName(string packageFamilyName, string appId, bool includeAppId)
        {
            string packageDisplayName = CleanPackageName(packageFamilyName).Trim();
            if (!includeAppId)
            {
                return string.IsNullOrWhiteSpace(packageDisplayName) ? appId : packageDisplayName;
            }

            string cleanedAppId = appId.Replace('.', ' ').Trim();
            if (string.IsNullOrWhiteSpace(cleanedAppId) || string.Equals(cleanedAppId, "App", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(packageDisplayName) ? packageFamilyName : packageDisplayName;
            }

            if (string.IsNullOrWhiteSpace(packageDisplayName))
            {
                return cleanedAppId;
            }

            return $"{packageDisplayName} ({cleanedAppId})";
        }

        internal static string BuildPackagedAppDisplayNameForTests(string packageFamilyName, string appId, bool includeAppId)
        {
            return BuildPackagedAppDisplayName(packageFamilyName, appId, includeAppId);
        }

        private static string? TraverseParentChain(
            int pid,
            int currentDepth,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            if (currentDepth >= MAX_PARENT_DEPTH)
                return null;

            var parentPid = GetParentPid(pid);
            if (parentPid == 0 || parentPid == pid)
                return null;

            if (!TryGetProcessName(parentPid, out string? parentName))
            {
                return null;
            }

            if (IsKnownHelperProcessName(parentName))
                return TraverseParentChain(parentPid, currentDepth + 1, windowPidMap);

            Trace(() => $"Parent chain traversal found parent={LogPrivacy.Process(parentName)} pid={LogPrivacy.Id(parentPid.ToString())}");

            return ResolveProcessName(parentPid, parentName, windowPidMap).Name;
        }

        private static bool TryGetProcessName(int pid, [NotNullWhen(true)] out string? processName)
        {
            processName = null;
            try
            {
                if (!Process.TryGetProcessById(pid, out var process)) return false;
                using (process)
                {
                    processName = process.ProcessName;
                    return !string.IsNullOrEmpty(processName);
                }
            }
            catch (ArgumentException)
            {
                processName = null;
                return false;
            }
            catch (InvalidOperationException)
            {
                processName = null;
                return false;
            }
        }

        private static ProcessNameResolution ResolveProcessName(
            int pid,
            string processName,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            string? windowName = GetProcessNameFromWindow(pid, windowPidMap);
            if (!string.IsNullOrEmpty(windowName))
            {
                return new ProcessNameResolution(windowName, ProcessNameResolutionSource.Window);
            }

            string? fileDescription = GetFileDescription(pid);
            if (!string.IsNullOrEmpty(fileDescription))
            {
                return new ProcessNameResolution(fileDescription, ProcessNameResolutionSource.FileDescription);
            }

            return new ProcessNameResolution(SanitizeProcessName(processName), ProcessNameResolutionSource.SanitizedProcessName);
        }

        private static IntPtr FindPreferredMainWindow(int pid, IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            IntPtr windowHandle = FindMainWindow(pid, windowPidMap, requireVisible: true);
            return windowHandle != IntPtr.Zero
                ? windowHandle
                : FindMainWindow(pid, windowPidMap, requireVisible: false);
        }

        private static string? GetProcessNameFromWindow(
            int pid,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            IntPtr hWnd = FindPreferredMainWindow(pid, windowPidMap);

            if (hWnd == IntPtr.Zero)
                return null;

            string? aumid = GetWindowAppUserModelId(hWnd);
            if (!string.IsNullOrEmpty(aumid))
            {
                string? friendlyName = ResolveAumidToName(aumid);
                if (!string.IsNullOrEmpty(friendlyName))
                {
                    Trace(() => $"Resolved PID {pid} via AUMID mapping");
                    return friendlyName;
                }
            }

            string? titleText = TryGetWindowTitleRaw(hWnd);
            if (!string.IsNullOrEmpty(titleText) && !IsInternalWindowTitle(titleText))
            {
                Trace(() => $"Resolved PID {pid} via window title heuristic (length={titleText.Length})");
                return titleText;
            }

            return null;
        }

        private static unsafe string? TryGetWindowTitleRaw(IntPtr windowHandle)
        {
            Span<char> shortBuffer = stackalloc char[256];
            int copiedLength;
            fixed (char* ptr = shortBuffer)
            {
                copiedLength = GetWindowText(windowHandle, ptr, shortBuffer.Length);
            }

            if (copiedLength <= 0)
            {
                return null;
            }

            if (copiedLength < shortBuffer.Length - 1)
            {
                return new string(shortBuffer[..copiedLength]);
            }

            int capacity = Math.Clamp(GetWindowTextLength(windowHandle) + 1, 512, 32768);
            char[] rented = System.Buffers.ArrayPool<char>.Shared.Rent(capacity);
            try
            {
                fixed (char* ptr = rented)
                {
                    copiedLength = GetWindowText(windowHandle, ptr, capacity);
                }

                return copiedLength > 0 ? new string(rented, 0, copiedLength) : null;
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        private static string? GetProcessAppUserModelId(
            int pid,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap)
        {
            IntPtr hWnd = FindPreferredMainWindow(pid, windowPidMap);

            return hWnd == IntPtr.Zero ? null : GetWindowAppUserModelId(hWnd);
        }

        internal static bool IsInternalWindowTitle(string title)
        {
            foreach (var prefix in AppConstants.Audio.InternalWindowPrefixes)
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (title.Length > AppConstants.Audio.WindowTitleHeuristics.MinHexLength &&
                (title.Contains('.') || title.Contains('_')) &&
                char.IsDigit(title[^1]))
            {
                int hexCount = 0;
                for (int i = title.Length - 1; i >= 0 && (char.IsDigit(title[i]) ||
                    (title[i] >= 'a' && title[i] <= 'f') ||
                    (title[i] >= 'A' && title[i] <= 'F') ||
                    title[i] == '.'); i--)
                {
                    hexCount++;
                }
                if (hexCount > AppConstants.Audio.WindowTitleHeuristics.MaxHexCount)
                    return true;
            }

            if (title.Length > AppConstants.Audio.WindowTitleHeuristics.MinDashLength && title.Contains('-'))
            {
                int dashCount = 0;
                foreach (char c in title)
                    if (c == '-') dashCount++;
                if (dashCount >= AppConstants.Audio.WindowTitleHeuristics.MinDashCount)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the AppUserModelID from a top-level window using the shared native property-store projection.
        /// </summary>
        internal static string? GetWindowAppUserModelId(IntPtr hWnd)
        {
            try
            {
                Guid iid = typeof(IPropertyStoreNativeInterop).GUID;
                if (SHGetPropertyStoreForWindow(hWnd, ref iid, out IntPtr storePtr) != 0 || storePtr == IntPtr.Zero)
                {
                    return null;
                }

                if (!NativeAudioInteropHelper.ComActivator.TryWrapTyped(storePtr, out IActivatedNativeComObject<IPropertyStoreNativeInterop>? storeHandle, out _))
                {
                    return null;
                }

                using (storeHandle!)
                {
                    NativePropertyKey key = PKEY_AppUserModel_ID;
                    int hResult = storeHandle.Interface.GetValue(ref key, out NativePropVariant variant);
                    if (hResult != 0)
                    {
                        return null;
                    }

                    using (variant)
                    {
                        if (!variant.TryGetString(out string? appUserModelId))
                        {
                            return null;
                        }

                        return appUserModelId;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace(() => $"GetWindowAppUserModelId failed: {ex.GetType().Name}");
                return null;
            }
        }

        public static string? GetFileDescription(int pid)
        {
            long now = Environment.TickCount64;
            var cache = Volatile.Read(ref FileDescriptionCache);
            long? started = pid > 0 ? ProcessEnumerationHelper.TryGetStartTimeUtcTicks((uint)pid) : null;
            if (!started.HasValue)
            {
                if (cache.TryGetValue(pid, out CacheEntry previous)) cache.TryRemove(new KeyValuePair<int, CacheEntry>(pid, previous));
                return null;
            }
            if (TryGetCachedValue(cache, pid, now, out string? cachedDescription, started)) return cachedDescription;
            try
            {
                using var process = Process.GetProcessById(pid);

                string? path = process.MainModule?.FileName;
                string? description = path == null ? null : FileVersionInfo.GetVersionInfo(path).FileDescription;
                return StoreCachedLookupResult(cache, pid, string.IsNullOrEmpty(description) ? null : description,
                    now, FileDescriptionCacheExpiryTicks, started);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetFileDescription failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
                return StoreCachedLookupResult(cache, pid, null, now, FileDescriptionCacheExpiryTicks, started);
            }
        }


        private static IntPtr FindMainWindow(
            int pid,
            IReadOnlyDictionary<IntPtr, uint> windowPidMap,
            bool requireVisible)
        {
            IntPtr bestWindow = IntPtr.Zero;
            int bestTitleLength = 0;

            foreach (var kvp in windowPidMap)
            {
                if (kvp.Value != pid)
                    continue;

                if (requireVisible && !IsWindowVisible(kvp.Key))
                    continue;

                int titleLength = GetWindowTextLength(kvp.Key);
                if (titleLength > bestTitleLength)
                {
                    bestTitleLength = titleLength;
                    bestWindow = kvp.Key;
                }
                else if (bestWindow == IntPtr.Zero)
                {
                    bestWindow = kvp.Key;
                }
            }

            return bestWindow;
        }


        public static string CapitalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            return string.Create(name.Length, name, (span, source) =>
            {
                source.CopyTo(span);
                span[0] = char.ToUpperInvariant(span[0]);
            });
        }

        /// <summary>Removes explicit executable/build suffixes from basenames while preserving product numbers and edition labels.</summary>
        public static string SanitizeProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return processName;

            if (processName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Edge";

            ReadOnlySpan<char> result = processName.AsSpan();

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var suffix in AppConstants.Audio.SuffixesToRemove)
                {
                    if (result.Length > suffix.Length && result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result[..^suffix.Length];
                        changed = true;
                        break;
                    }
                }
            }

            if (result.Length == processName.Length)
                return processName;

            return result.ToString();
        }

        public static Process? FindRealAppProcess(Process? webViewProcess)
        {
            if (webViewProcess == null)
                return null;

            var visitedPids = new HashSet<int>(MAX_PARENT_DEPTH);
            Process? currentProcess = webViewProcess;
            bool isCurrentOwned = false;

            try
            {
                while (currentProcess != null && visitedPids.Count < MAX_PARENT_DEPTH)
                {
                    visitedPids.Add(currentProcess.Id);
                    string processName = currentProcess.ProcessName;

                    Trace(() => $"Checking process={LogPrivacy.Process(processName)} pid={LogPrivacy.Id(currentProcess.Id.ToString())}");

                    if (!IsKnownHelperProcessName(processName))
                    {
                        var result = currentProcess;
                        currentProcess = null;
                        return result;
                    }

                    var parentPid = GetParentPid(currentProcess.Id);
                    if (parentPid == 0 || visitedPids.Contains(parentPid))
                        break;

                    if (isCurrentOwned)
                        currentProcess.Dispose();

                    try
                    {
                        currentProcess = Process.GetProcessById(parentPid);
                        isCurrentOwned = true;
                    }
                    catch (ArgumentException)
                    {
                        currentProcess = null;
                        isCurrentOwned = false;
                    }
                    catch (InvalidOperationException)
                    {
                        currentProcess = null;
                        isCurrentOwned = false;
                    }
                }

                return null;
            }
            finally
            {
                if (currentProcess != null && isCurrentOwned)
                    currentProcess.Dispose();
            }
        }

        public static int GetParentPid(int pid)
        {
            try
            {
                return ProcessEnumerationHelper.TryGetParentProcessId(pid);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetParentPid failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
            }

            return 0;
        }
    }
}
