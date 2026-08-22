using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            if (TryGetWindowPidMapSnapshot(now, out ConcurrentDictionary<IntPtr, uint>? cached))
                return cached;

            lock (WindowPidMapCacheLock)
            {
                now = Environment.TickCount64;
                if (TryGetWindowPidMapSnapshot(now, out cached))
                {
                    return cached;
                }

                var map = new ConcurrentDictionary<IntPtr, uint>();
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

        private static bool TryGetWindowPidMapSnapshot(long now, [NotNullWhen(true)] out ConcurrentDictionary<IntPtr, uint>? cached)
        {
            if (!TryGetExpiringSnapshot(WindowPidMapCache, WindowPidMapCacheExpiry, now, out cached))
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

        private static ConcurrentDictionary<IntPtr, uint> PublishWindowPidMapSnapshot(ConcurrentDictionary<IntPtr, uint> map, long now)
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

        private static string? TryGetWindowTitle(IntPtr windowHandle)
        {
            int titleLength = GetWindowTextLength(windowHandle);
            if (titleLength <= 0)
            {
                return null;
            }

            StringBuilder titleBuilder = new(titleLength + 1);
            int copiedLength = GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
            if (copiedLength <= 0)
            {
                return null;
            }

            string title = titleBuilder.ToString().Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }

        private static string? TryGetWindowClassName(IntPtr windowHandle)
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

        public static string? GetProcessRegisteredName(uint pid, int depth = 0)
        {
            return GetProcessRegisteredName(pid, depth, BuildWindowPidMap());
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

        public static Task<string?> GetProcessRegisteredNameAsync(uint pid, int depth = 0)
        {
            return ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => GetProcessRegisteredName(pid, depth));
        }

        public static Task<string?> GetProcessNameFromWindowAsync(int pid)
        {
            return ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => GetProcessNameFromWindow(pid, BuildWindowPidMap()));
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

                    int iconCount = apps.Count(static app => app.IconSource != null);
                    Trace(() => $"Packaged app inventory refreshed source={inventorySource} count={apps.Count} iconCount={iconCount} fallbackIconCount={apps.Count - iconCount}");

                    IReadOnlyList<PackagedAppIdentity> cachedResult = [.. apps];
                    return PublishPackagedAppInventorySnapshot(cachedResult, now);
                }
                catch (Exception ex)
                {
                    Trace(() => $"GetInstalledPackagedApps failed: {ex.GetType().Name}");
                    return [];
                }
            }
        }

        private static bool TryGetPackagedAppInventorySnapshot(long now, [NotNullWhen(true)] out IReadOnlyList<PackagedAppIdentity>? cachedApps)
        {
            if (!TryGetExpiringSnapshot(PackagedAppInventoryCache, PackagedAppInventoryCacheExpiry, now, out cachedApps))
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
            try
            {
                var packageManager = new PackageManager();
                var apps = new List<PackagedAppIdentity>();
                var seenAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int iconLoadFailureCount = 0;
                string? firstIconLoadFailureType = null;

                foreach (Package package in packageManager.FindPackagesForUser(string.Empty))
                {
                    string packageFamilyName = package.Id.FamilyName;
                    if (string.IsNullOrWhiteSpace(packageFamilyName))
                    {
                        continue;
                    }

                    IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry>? appEntries;
                    try
                    {
                        appEntries = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Trace(() => $"GetAppListEntriesAsync failed for {packageFamilyName}: {ex.GetType().Name}");
                        continue;
                    }

                    if (appEntries == null || appEntries.Count == 0)
                    {
                        continue;
                    }

                    bool includeAppId = appEntries.Count > 1;
                    foreach (Windows.ApplicationModel.Core.AppListEntry appEntry in appEntries)
                    {
                        BitmapSource? iconSource = TryLoadPackagedAppIcon(appEntry.DisplayInfo, out string? iconLoadFailureType);
                        if (iconLoadFailureType != null)
                        {
                            iconLoadFailureCount++;
                            firstIconLoadFailureType ??= iconLoadFailureType;
                        }

                        TryAddPackagedAppIdentity(
                            apps,
                            seenAumids,
                            packageFamilyName,
                            appEntry.AppUserModelId,
                            appEntry.DisplayInfo?.DisplayName,
                            includeAppId,
                            iconSource);
                    }
                }

                if (iconLoadFailureCount > 0)
                {
                    Trace(() => $"Packaged app icon loading completed with failures failureCount={iconLoadFailureCount} firstError={firstIconLoadFailureType}");
                }

                return OrderPackagedApps(apps);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetInstalledPackagedAppsFromPackageManager failed: {ex.GetType().Name}");
                return [];
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
            ImageSource? iconSource = null)
        {
            string normalizedAppUserModelId = appUserModelId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(packageFamilyName) ||
                string.IsNullOrWhiteSpace(normalizedAppUserModelId) ||
                !seenAumids.Add(normalizedAppUserModelId))
            {
                return false;
            }

            apps.Add(CreatePackagedAppIdentity(packageFamilyName, normalizedAppUserModelId, preferredDisplayName, includeAppId, iconSource));
            return true;
        }

        private static PackagedAppIdentity CreatePackagedAppIdentity(
            string packageFamilyName,
            string appUserModelId,
            string? preferredDisplayName,
            bool includeAppId,
            ImageSource? iconSource = null)
        {
            string appId = ExtractPackagedAppId(appUserModelId, packageFamilyName);
            string displayName = ResolvePackagedAppDisplayName(packageFamilyName, appId, preferredDisplayName, includeAppId);
            return new PackagedAppIdentity(displayName, appUserModelId, packageFamilyName, appId, iconSource);
        }

        private static BitmapSource? TryLoadPackagedAppIcon(AppDisplayInfo? displayInfo, out string? failureType)
        {
            failureType = null;
            if (displayInfo == null)
            {
                return null;
            }

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

                using var reader = new Windows.Storage.Streams.DataReader(logoStream.GetInputStreamAt(0));
                int byteCount = (int)logoStream.Size;
                _ = reader.LoadAsync((uint)byteCount).AsTask().GetAwaiter().GetResult();
                var iconBytes = new byte[byteCount];
                reader.ReadBytes(iconBytes);
                using var managedStream = new MemoryStream(iconBytes, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64;
                bitmap.StreamSource = managedStream;
                bitmap.EndInit();
                bitmap.Freeze();
                return NormalizePackagedAppIcon(bitmap);
            }
            catch (Exception ex)
            {
                failureType = ex.GetType().Name;
                return null;
            }
        }

        internal static BitmapSource NormalizePackagedAppIconForTests(BitmapSource source)
            => NormalizePackagedAppIcon(source);

        private static BitmapSource NormalizePackagedAppIcon(BitmapSource source)
        {
            const byte visibleAlphaThreshold = 12;
            const double visibleContentPaddingRatio = 0.10;
            const double maximumScale = 2.5;

            if (source.PixelWidth <= 1 || source.PixelHeight <= 1)
            {
                return source;
            }

            BitmapSource pixelSource = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int stride = checked(pixelSource.PixelWidth * 4);
            var pixels = new byte[checked(stride * pixelSource.PixelHeight)];
            pixelSource.CopyPixels(pixels, stride, 0);

            int minX = pixelSource.PixelWidth;
            int minY = pixelSource.PixelHeight;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < pixelSource.PixelHeight; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < pixelSource.PixelWidth; x++)
                {
                    if (pixels[rowOffset + (x * 4) + 3] <= visibleAlphaThreshold)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return source;
            }

            int contentWidth = maxX - minX + 1;
            int contentHeight = maxY - minY + 1;
            int minimumCropWidth = Math.Min(pixelSource.PixelWidth, (int)Math.Ceiling(pixelSource.PixelWidth / maximumScale));
            int minimumCropHeight = Math.Min(pixelSource.PixelHeight, (int)Math.Ceiling(pixelSource.PixelHeight / maximumScale));
            int desiredCropWidth = Math.Max(
                minimumCropWidth,
                contentWidth + (int)Math.Ceiling(contentWidth * visibleContentPaddingRatio * 2));
            int desiredCropHeight = Math.Max(
                minimumCropHeight,
                contentHeight + (int)Math.Ceiling(contentHeight * visibleContentPaddingRatio * 2));

            int cropWidth = Math.Min(pixelSource.PixelWidth, desiredCropWidth);
            int cropHeight = Math.Min(pixelSource.PixelHeight, desiredCropHeight);
            int cropX = Math.Clamp(minX - ((cropWidth - contentWidth) / 2), 0, pixelSource.PixelWidth - cropWidth);
            int cropY = Math.Clamp(minY - ((cropHeight - contentHeight) / 2), 0, pixelSource.PixelHeight - cropHeight);
            if (cropX == 0 && cropY == 0 && cropWidth == pixelSource.PixelWidth && cropHeight == pixelSource.PixelHeight)
            {
                return source;
            }

            var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(cropX, cropY, cropWidth, cropHeight));
            cropped.Freeze();
            return cropped;
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

            if (TryGetCachedValue(AumidCache, aumid, now, out string? cachedName))
                return cachedName;

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\ActivatableClasses\Package");

                if (key == null)
                {
                    return StoreCachedLookupResult(AumidCache, aumid, null, now);
                }

                string? packageFamilyName = FindPackageFamilyNameForAumid(key, aumid);
                if (!string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    string cleaned = CleanPackageName(packageFamilyName);
                    Trace("Resolved AUMID to package display name");
                    return StoreCachedLookupResult(AumidCache, aumid, cleaned, now);
                }

                return StoreCachedLookupResult(AumidCache, aumid, null, now);
            }
            catch (Exception ex)
            {
                Trace(() => $"ResolveAumidToName failed: {ex.GetType().Name}");
                return StoreCachedLookupResult(AumidCache, aumid, null, now);
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

        public static Task<string?> GetFileDescriptionAsync(int pid)
        {
            return Task.Run(() => GetFileDescription(pid));
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
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
                return !string.IsNullOrEmpty(processName);
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

            int length = GetWindowTextLength(hWnd);
            if (length > 0)
            {
                StringBuilder title = new(length + 1);
                int copiedLength = GetWindowText(hWnd, title, title.Capacity);
                if (copiedLength <= 0)
                {
                    return null;
                }

                string titleText = title.ToString();
                if (!string.IsNullOrEmpty(titleText) && !IsInternalWindowTitle(titleText))
                {
                    Trace(() => $"Resolved PID {pid} via window title heuristic (length={titleText.Length})");
                    return titleText;
                }
            }

            return null;
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
        private static string? GetWindowAppUserModelId(IntPtr hWnd)
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
            var now = Environment.TickCount64;

            if (TryGetCachedValue(FileDescriptionCache, pid, now, out string? cachedDescription))
                return cachedDescription;

            try
            {
                using var process = Process.GetProcessById(pid);
                var mainModule = process.MainModule;
                if (mainModule?.FileName != null)
                {
                    var info = FileVersionInfo.GetVersionInfo(mainModule.FileName);
                    var description = info.FileDescription;

                    if (!string.IsNullOrEmpty(description))
                    {
                        return StoreCachedLookupResult(FileDescriptionCache, pid, description, now, FileDescriptionCacheExpiryTicks);
                    }
                }

                return StoreCachedLookupResult(FileDescriptionCache, pid, null, now, FileDescriptionCacheExpiryTicks);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                Trace(() => $"GetFileDescription failed for PID {pid}: Access is denied (Expected for elevated/system processes)");
                return StoreCachedLookupResult(FileDescriptionCache, pid, null, now, FileDescriptionCacheExpiryTicks);
            }
            catch (Exception ex)
            {
                Trace(() => $"GetFileDescription failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
                return StoreCachedLookupResult(FileDescriptionCache, pid, null, now, FileDescriptionCacheExpiryTicks);
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
                    if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result[..^suffix.Length];
                        changed = true;
                        break;
                    }
                }
            }

            if (result.Length > 3 &&
                (result.EndsWith("64", StringComparison.Ordinal) ||
                 result.EndsWith("32", StringComparison.Ordinal)))
            {
                result = result[..^2];
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
                using var process = Process.GetProcessById(pid);
                PROCESS_BASIC_INFORMATION pbi = new();
                int status = NtQueryInformationProcess(
                    process.Handle,
                    0,
                    ref pbi,
                    Marshal.SizeOf(pbi),
                    out _);

                if (status == 0)
                    return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
            catch (Exception ex)
            {
                Trace(() => $"GetParentPid failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
            }

            return 0;
        }

        public static Process? GetParentProcessSafe(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                PROCESS_BASIC_INFORMATION pbi = new();
                int status = NtQueryInformationProcess(
                    process.Handle,
                    0,
                    ref pbi,
                    Marshal.SizeOf(pbi),
                    out _);

                if (status == 0)
                {
                    int parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
                    if (parentPid > 0)
                    {
                        var parentProcess = Process.GetProcessById(parentPid);
                        Trace(() => $"Got parent process for PID {pid}: parent PID = {parentPid}");
                        return parentProcess;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Trace(() => $"GetParentProcessSafe failed for PID {LogPrivacy.Id(pid.ToString())}: {ex.GetType().Name}");
                return null;
            }
        }

        public static string GetFriendlyProcessName(Process? process)
        {
            if (process == null)
                return string.Empty;

            string processName = process.ProcessName;

            if (processName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var parentProcess = GetParentProcessSafe(process.Id);
                    if (parentProcess != null)
                    {
                        Trace(() => $"Resolved msedgewebview2 to parent={LogPrivacy.Process(parentProcess.ProcessName)}");
                        return parentProcess.ProcessName;
                    }
                }
                catch (Exception ex)
                {
                    Trace(() => $"Failed to get parent process for {LogPrivacy.Process(processName)}: {ex.GetType().Name}");
                }
            }

            return processName;
        }
    }
}
