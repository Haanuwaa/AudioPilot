using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

[Collection("AudioDeviceHelperCacheIsolation")]
public sealed class AudioDeviceHelperTests
{
    [Fact]
    public void NormalizePackagedAppIcon_CropsExcessTransparentPadding()
    {
        BitmapSource source = CreateTestIcon(
            width: 64,
            height: 64,
            visibleBounds: new Int32Rect(24, 20, 16, 24));

        BitmapSource normalized = AudioDeviceHelper.NormalizePackagedAppIconForTests(source);

        Assert.InRange(normalized.PixelWidth, 26, 36);
        Assert.InRange(normalized.PixelHeight, 29, 40);
        Assert.True(normalized.IsFrozen);
    }

    [Fact]
    public void NormalizePackagedAppIcon_PreservesFullBleedArtwork()
    {
        BitmapSource source = CreateTestIcon(
            width: 64,
            height: 64,
            visibleBounds: new Int32Rect(0, 0, 64, 64));

        BitmapSource normalized = AudioDeviceHelper.NormalizePackagedAppIconForTests(source);

        Assert.Same(source, normalized);
    }

    [Fact]
    public void NormalizePackagedAppIcon_PreservesFullyTransparentArtwork()
    {
        BitmapSource source = CreateTestIcon(
            width: 64,
            height: 64,
            visibleBounds: null);

        BitmapSource normalized = AudioDeviceHelper.NormalizePackagedAppIconForTests(source);

        Assert.Same(source, normalized);
    }

    [Fact]
    public void GetVisibleWindowTitlesByProcessIds_ReturnsEmpty_WhenProcessIdsNull()
    {
        IReadOnlyDictionary<int, IReadOnlyList<string>> result = AudioDeviceHelper.GetVisibleWindowTitlesByProcessIds(null);

        Assert.Empty(result);
    }

    private static BitmapSource CreateTestIcon(int width, int height, Int32Rect? visibleBounds)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        if (visibleBounds is Int32Rect bounds)
        {
            for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                for (int x = bounds.X; x < bounds.X + bounds.Width; x++)
                {
                    int offset = (y * stride) + (x * 4);
                    pixels[offset] = 0x40;
                    pixels[offset + 1] = 0x80;
                    pixels[offset + 2] = 0xC0;
                    pixels[offset + 3] = 0xFF;
                }
            }
        }

        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [Fact]
    public void GetVisibleWindowTitlesByProcessIds_ReturnsEmpty_WhenProcessIdsEmpty()
    {
        IReadOnlyDictionary<int, IReadOnlyList<string>> result = AudioDeviceHelper.GetVisibleWindowTitlesByProcessIds([]);

        Assert.Empty(result);
    }

    [Fact]
    public void GetVisibleWindowMetadataByProcessIds_ReturnsEmpty_WhenProcessIdsNull()
    {
        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> result = AudioDeviceHelper.GetVisibleWindowMetadataByProcessIds(null);

        Assert.Empty(result);
    }

    [Fact]
    public void GetVisibleWindowMetadataByProcessIds_ReturnsEmpty_WhenProcessIdsEmpty()
    {
        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> result = AudioDeviceHelper.GetVisibleWindowMetadataByProcessIds([]);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetAppUserModelDisplayName_ReturnsNull_WhenIdentifierMissing(string? appUserModelId)
    {
        string? result = AudioDeviceHelper.GetAppUserModelDisplayName(appUserModelId);

        Assert.Null(result);
    }

    [Fact]
    public void BuildWindowPidMap_CachesResultUntilClearCaches()
    {
        AudioDeviceHelper.ClearCaches();

        IReadOnlyDictionary<IntPtr, uint> first = AudioDeviceHelper.BuildWindowPidMap();
        IReadOnlyDictionary<IntPtr, uint> second = AudioDeviceHelper.BuildWindowPidMap();

        bool wasCached = AudioDeviceHelper.WindowPidMapCacheForTests != null;

        if (wasCached)
        {
            Assert.Same(first, second);
            Assert.Same(first, AudioDeviceHelper.WindowPidMapCacheForTests);
        }
        else
        {
            Assert.True(first.Count > AppConstants.Limits.MaxPidProcessMapEntries);
            Assert.NotSame(first, second);
        }

        AudioDeviceHelper.ClearCaches();

        Assert.Null(AudioDeviceHelper.WindowPidMapCacheForTests);

        IReadOnlyDictionary<IntPtr, uint> afterClear = AudioDeviceHelper.BuildWindowPidMap();

        Assert.NotSame(first, afterClear);
    }

    [Fact]
    public void BuildWindowPidMap_ReturnsSeededCache_WhenUnexpired()
    {
        var seeded = new Dictionary<IntPtr, uint>
        {
            [new IntPtr(101)] = 202,
        };
        AudioDeviceHelper.SeedWindowPidMapCacheForTests(seeded, Environment.TickCount64 + 5000);

        IReadOnlyDictionary<IntPtr, uint> result = AudioDeviceHelper.BuildWindowPidMap();

        Assert.Same(AudioDeviceHelper.WindowPidMapCacheForTests, result);
        Assert.Equal(202u, result[new IntPtr(101)]);
    }

    [Fact]
    public void BuildWindowPidMap_IgnoresExpiredSeededCache()
    {
        var expired = new Dictionary<IntPtr, uint>
        {
            [new IntPtr(303)] = 404,
        };
        AudioDeviceHelper.SeedWindowPidMapCacheForTests(expired, Environment.TickCount64 - 1);

        IReadOnlyDictionary<IntPtr, uint> result = AudioDeviceHelper.BuildWindowPidMap();

        Assert.NotSame(AudioDeviceHelper.WindowPidMapCacheForTests, expired);
        Assert.NotSame(expired, result);
    }

    [Fact]
    public void BuildWindowPidMap_IgnoresOversizedSeededCache()
    {
        AudioDeviceHelper.ClearCaches();
        var oversized = new Dictionary<IntPtr, uint>();
        for (int index = 0; index < AppConstants.Limits.MaxPidProcessMapEntries + 8; index++)
        {
            oversized[new IntPtr(index + 1)] = (uint)(index + 100);
        }

        AudioDeviceHelper.SeedWindowPidMapCacheForTests(oversized, Environment.TickCount64 + 5000);

        IReadOnlyDictionary<IntPtr, uint> result = AudioDeviceHelper.BuildWindowPidMap();

        Assert.NotSame(AudioDeviceHelper.WindowPidMapCacheForTests, oversized);
        Assert.NotSame(oversized, result);
        Assert.True(
            AudioDeviceHelper.WindowPidMapCacheForTests == null
            || AudioDeviceHelper.WindowPidMapCacheForTests.Count <= AppConstants.Limits.MaxPidProcessMapEntries);
    }

    [Fact]
    public void ClearCaches_ResetsWindowAndPackagedAppCacheState()
    {
        AudioDeviceHelper.SeedWindowPidMapCacheForTests(new Dictionary<IntPtr, uint>
        {
            [new IntPtr(123)] = 456,
        }, expiry: Environment.TickCount64 + 1000);
        AudioDeviceHelper.SeedPackagedAppInventoryCacheForTests(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Test App", "Test.Family!App", "Test.Family", "App")
        ],
        expiry: Environment.TickCount64 + 1000);

        AudioDeviceHelper.ClearCaches();

        Assert.Null(AudioDeviceHelper.WindowPidMapCacheForTests);
        Assert.Equal(0, AudioDeviceHelper.WindowPidMapCacheExpiryForTests);
        Assert.Null(AudioDeviceHelper.PackagedAppInventoryCacheForTests);
        Assert.Equal(0, AudioDeviceHelper.PackagedAppInventoryCacheExpiryForTests);
    }

    [Fact]
    public void ClearCaches_ResetsAumidAndFileDescriptionCacheState()
    {
        string aumid = "Test.Family!App";
        int pid = -5151;
        AudioDeviceHelper.SeedAumidCacheForTests(aumid, "Cached App", Environment.TickCount64 + 1000);
        AudioDeviceHelper.SeedFileDescriptionCacheForTests(pid, "Cached Description", Environment.TickCount64 + 1000);

        AudioDeviceHelper.ClearCaches();

        Assert.False(AudioDeviceHelper.TryGetAumidCacheEntryForTests(aumid, out _, out _));
        Assert.False(AudioDeviceHelper.TryGetFileDescriptionCacheEntryForTests(pid, out _, out _));
    }

    [Fact]
    public void GetInstalledPackagedApps_ReturnsSeededCache_WhenUnexpired()
    {
        AudioDeviceHelper.ClearCaches();
        IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> cachedApps =
        [
            new AudioDeviceHelper.PackagedAppIdentity("Cached App", "Cached.Family!App", "Cached.Family", "App")
        ];
        AudioDeviceHelper.SeedPackagedAppInventoryCacheForTests(cachedApps, Environment.TickCount64 + 5000);

        IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> result = AudioDeviceHelper.GetInstalledPackagedApps();

        Assert.Same(cachedApps, result);
    }

    [Fact]
    public void GetInstalledPackagedApps_IgnoresExpiredSeededCache()
    {
        AudioDeviceHelper.ClearCaches();
        IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> expiredApps =
        [
            new AudioDeviceHelper.PackagedAppIdentity("Expired App", "Expired.Family!App", "Expired.Family", "App")
        ];
        AudioDeviceHelper.SeedPackagedAppInventoryCacheForTests(expiredApps, Environment.TickCount64 - 1);

        IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> result = AudioDeviceHelper.GetInstalledPackagedApps();

        Assert.NotSame(expiredApps, result);
    }

    [Fact]
    public void GetInstalledPackagedApps_IgnoresOversizedSeededCache()
    {
        AudioDeviceHelper.ClearCaches();
        var oversizedApps = new List<AudioDeviceHelper.PackagedAppIdentity>();
        for (int index = 0; index < AppConstants.Limits.MaxPackagedAppInventoryEntries + 8; index++)
        {
            oversizedApps.Add(new AudioDeviceHelper.PackagedAppIdentity(
                $"App {index}",
                $"Family.{index}!App",
                $"Family.{index}",
                "App"));
        }

        AudioDeviceHelper.SeedPackagedAppInventoryCacheForTests(oversizedApps, Environment.TickCount64 + 5000);

        IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> result = AudioDeviceHelper.GetInstalledPackagedApps();

        Assert.NotSame(oversizedApps, result);
        Assert.True(
            AudioDeviceHelper.PackagedAppInventoryCacheForTests == null
            || AudioDeviceHelper.PackagedAppInventoryCacheForTests.Count <= AppConstants.Limits.MaxPackagedAppInventoryEntries);
    }

    [Fact]
    public void ClearPackagedAppInventoryCache_ResetsOnlyPackagedAppInventoryState()
    {
        AudioDeviceHelper.ClearCaches();
        IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> cachedApps =
        [
            new AudioDeviceHelper.PackagedAppIdentity("Cached App", "Cached.Family!App", "Cached.Family", "App")
        ];
        AudioDeviceHelper.SeedPackagedAppInventoryCacheForTests(cachedApps, Environment.TickCount64 + 5000);

        AudioDeviceHelper.ClearPackagedAppInventoryCache();

        Assert.Null(AudioDeviceHelper.PackagedAppInventoryCacheForTests);
        Assert.Equal(0, AudioDeviceHelper.PackagedAppInventoryCacheExpiryForTests);
    }

    [Fact]
    public void TrimCachesForHiddenMode_ClearsWindowAndPackagedAppSnapshots()
    {
        AudioDeviceHelper.ClearCaches();
        AudioDeviceHelper.SeedWindowPidMapCacheForTests(new Dictionary<IntPtr, uint>
        {
            [new IntPtr(123)] = 456,
        }, expiry: Environment.TickCount64 + 1000);
        AudioDeviceHelper.SeedPackagedAppInventoryCacheForTests(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Test App", "Test.Family!App", "Test.Family", "App")
        ],
        expiry: Environment.TickCount64 + 1000);

        AudioDeviceHelper.TrimCachesForHiddenMode();

        Assert.Null(AudioDeviceHelper.WindowPidMapCacheForTests);
        Assert.Equal(0, AudioDeviceHelper.WindowPidMapCacheExpiryForTests);
        Assert.Null(AudioDeviceHelper.PackagedAppInventoryCacheForTests);
        Assert.Equal(0, AudioDeviceHelper.PackagedAppInventoryCacheExpiryForTests);
    }

    [Fact]
    public void GetAppUserModelDisplayName_ReturnsSeededCachedValue_WhenUnexpired()
    {
        AudioDeviceHelper.ClearCaches();
        string aumid = "Missing.Test.Family!App";
        string cachedName = $"cached-aumid-{Guid.NewGuid():N}";
        long expiry = Environment.TickCount64 + 5000;
        AudioDeviceHelper.SeedAumidCacheForTests(aumid, cachedName, expiry);

        string? result = AudioDeviceHelper.GetAppUserModelDisplayName(aumid);

        Assert.Equal(cachedName, result);
        Assert.True(AudioDeviceHelper.TryGetAumidCacheEntryForTests(aumid, out string? cachedValue, out long cachedExpiry));
        Assert.Equal(cachedName, cachedValue);
        Assert.Equal(expiry, cachedExpiry);
    }

    [Fact]
    public void GetAppUserModelDisplayName_ReplacesExpiredSeededCache_WithNullEntry()
    {
        AudioDeviceHelper.ClearCaches();
        string aumid = "Definitely.Missing.Family!App";
        AudioDeviceHelper.SeedAumidCacheForTests(aumid, "expired-value", Environment.TickCount64 - 1);
        long beforeLookup = Environment.TickCount64;

        string? result = AudioDeviceHelper.GetAppUserModelDisplayName(aumid);

        Assert.Null(result);
        Assert.True(AudioDeviceHelper.TryGetAumidCacheEntryForTests(aumid, out string? cachedValue, out long cachedExpiry));
        Assert.Null(cachedValue);
        Assert.True(cachedExpiry > beforeLookup);
    }

    [Fact]
    public void GetFileDescription_ReturnsSeededCachedValue_WhenUnexpired()
    {
        AudioDeviceHelper.ClearCaches();
        int pid = -4242;
        string cachedDescription = $"cached-description-{Guid.NewGuid():N}";
        long expiry = Environment.TickCount64 + 5000;
        AudioDeviceHelper.SeedFileDescriptionCacheForTests(pid, cachedDescription, expiry);

        string? result = AudioDeviceHelper.GetFileDescription(pid);

        Assert.Equal(cachedDescription, result);
        Assert.True(AudioDeviceHelper.TryGetFileDescriptionCacheEntryForTests(pid, out string? cachedValue, out long cachedExpiry));
        Assert.Equal(cachedDescription, cachedValue);
        Assert.Equal(expiry, cachedExpiry);
    }

    [Fact]
    public void GetFileDescription_ReplacesExpiredSeededCache_WithNullEntry()
    {
        AudioDeviceHelper.ClearCaches();
        int pid = -4343;
        AudioDeviceHelper.SeedFileDescriptionCacheForTests(pid, "expired-description", Environment.TickCount64 - 1);
        long beforeLookup = Environment.TickCount64;

        string? result = AudioDeviceHelper.GetFileDescription(pid);

        Assert.Null(result);
        Assert.True(AudioDeviceHelper.TryGetFileDescriptionCacheEntryForTests(pid, out string? cachedValue, out long cachedExpiry));
        Assert.Null(cachedValue);
        Assert.True(cachedExpiry > beforeLookup);
        Assert.True(cachedExpiry <= Environment.TickCount64 + TimeSpan.FromSeconds(AppConstants.Timing.ProcessCacheTtlSeconds).TotalMilliseconds);
    }

    [Theory]
    [InlineData("HwndWrapper[AudioPilot;;123]", true)]
    [InlineData("DesktopWindowXamlSourceSomething", true)]
    [InlineData("Normal Window Title", false)]
    [InlineData("Some.Window_ABCDEF1234567890", true)]
    [InlineData("This-Is-A-Very-Long-Internal-Style-Window-Token", true)]
    [InlineData("NVIDIA Broadcast", false)]
    [InlineData("AMD Noise Suppression", false)]
    [InlineData("Intel Unison", false)]
    public void IsInternalWindowTitle_ClassifiesKnownHeuristics(string title, bool expected)
    {
        bool result = AudioDeviceHelper.IsInternalWindowTitle(title);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Contoso.App_123abc", "Contoso App")]
    [InlineData("Contoso.App", "Contoso.App")]
    public void CleanPackageNameForTests_NormalizesFamilyName(string packageFamilyName, string expected)
    {
        string result = AudioDeviceHelper.CleanPackageNameForTests(packageFamilyName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Contoso.App_123abc!Shell", "Contoso.App_123abc", "Shell")]
    [InlineData("Contoso.App_123abc", "Contoso.App_123abc", "")]
    [InlineData("StandaloneId", "Contoso.App_123abc", "StandaloneId")]
    public void ExtractPackagedAppIdForTests_ParsesExpectedSuffix(string appUserModelId, string packageFamilyName, string expected)
    {
        string result = AudioDeviceHelper.ExtractPackagedAppIdForTests(appUserModelId, packageFamilyName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Contoso.App_123abc", "Shell", true, "Contoso App (Shell)")]
    [InlineData("Contoso.App_123abc", "App", true, "Contoso App")]
    [InlineData("Contoso.App_123abc", "", false, "Contoso App")]
    [InlineData("", "Shell", true, "Shell")]
    public void BuildPackagedAppDisplayNameForTests_FormatsDisplayName(string packageFamilyName, string appId, bool includeAppId, string expected)
    {
        string result = AudioDeviceHelper.BuildPackagedAppDisplayNameForTests(packageFamilyName, appId, includeAppId);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Game-Win64-Shipping.exe", "Game")]
    [InlineData("Spotify.App", "Spotify")]
    [InlineData("discord_x64", "discord")]
    [InlineData("msedge", "Microsoft Edge")]
    [InlineData("plainprocess", "plainprocess")]
    public void SanitizeProcessName_ReturnsExpectedFriendlyName(string processName, string expected)
    {
        string result = AudioDeviceHelper.SanitizeProcessName(processName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("spotify.exe", null, "Spotify")]
    [InlineData("discord_x64", "Discord Voice", "Discord Voice")]
    [InlineData("   ", null, "Unknown")]
    public void GetSessionDisplayNameFromCache_UsesCachedValueThenSanitizedFallback(string processName, string? cachedDisplayName, string expected)
    {
        string result = AudioDeviceHelper.GetSessionDisplayNameFromCache(processName, cachedDisplayName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("audiodg", true)]
    [InlineData("runtimebroker", true)]
    [InlineData("AudioPilot", true)]
    [InlineData("audiopilot.exe", false)]
    [InlineData("runtimebroker.exe", false)]
    [InlineData(" explorer ", false)]
    [InlineData("spotify", false)]
    public void IsIgnoredProcessName_UsesExactBasenameMatching(string processName, bool expected)
    {
        bool result = AudioDeviceHelper.IsIgnoredProcessName(processName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("msedgewebview2", true)]
    [InlineData("MSEDGEWEBVIEW2", true)]
    [InlineData("cefsharp.browsersubprocess", true)]
    [InlineData("renderer", true)]
    [InlineData("msedgewebview2.exe", false)]
    [InlineData("renderer-helper", false)]
    [InlineData("helperworker", false)]
    [InlineData("spotify", false)]
    public void IsKnownHelperProcessName_UsesExactBasenameMatching(string processName, bool expected)
    {
        bool result = AudioDeviceHelper.IsKnownHelperProcessName(processName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Music", "audiodg", true)]
    [InlineData("@%SystemRoot%\\System32\\AudioSrv", "spotify", true)]
    [InlineData("   ", "spotify", true)]
    [InlineData("Spotify", "spotify", false)]
    [InlineData("AudioPilot", "audiopilot", true)]
    [InlineData("AudioPilot", "audiopilot.exe", false)]
    [InlineData("CLI", "audiopilot.clihost", true)]
    [InlineData("Music", "runtimebroker.exe", false)]
    [InlineData("Music", "runtimebroker", true)]
    public void ShouldIgnoreSessionFromCache_ReturnsExpectedDecision(string displayName, string processName, bool expected)
    {
        bool result = AudioDeviceHelper.ShouldIgnoreSessionFromCache(displayName, processName);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldIgnoreSession_WhenSystemSoundsTraceIsLogged_RedactsDisplayName()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AudioDeviceHelperTests), "audio-device-helper.log", LogLevel.Trace);
        const string displayName = "@%SystemRoot%\\System32\\AudioSrv\\PrivateLabel";

        bool ignored = AudioDeviceHelper.ShouldIgnoreSession(loggerScope.Logger, displayName, process: null);

        Assert.True(ignored);

        string logText = TestLogFileAssert.WaitForLogText(
            loggerScope.LogPath,
            2000,
            "Skipping system sounds:",
            "session[len=");

        Assert.DoesNotContain(displayName, logText, StringComparison.Ordinal);
    }
}
