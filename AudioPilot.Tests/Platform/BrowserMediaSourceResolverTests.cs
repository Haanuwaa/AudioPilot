namespace AudioPilot.Tests.Platform;

public sealed class BrowserMediaSourceResolverTests
{
    [Fact]
    public void IsBrowserSource_SharesDiscoveryAcrossSources_WithoutExtendingMissLifetime()
    {
        long now = 0;
        int reads = 0;
        string[] discovered = ["PortableBrowser", "PrivateBrowser"];
        var resolver = new BrowserMediaSourceResolver(() => { reads++; return discovered; }, () => now);

        Assert.True(resolver.IsBrowserSource("PortableBrowser"));
        Assert.True(resolver.IsBrowserSource("PrivateBrowser"));
        for (int i = 0; i < 300; i++) { Assert.False(resolver.IsBrowserSource($"UnknownApp{i}")); }
        now = 4_999;
        Assert.False(resolver.IsBrowserSource("NewBrowser"));
        Assert.Equal(1, reads);

        discovered = ["NewBrowser"];
        now = 5_000;
        Assert.True(resolver.IsBrowserSource("NewBrowser"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void IsBrowserSource_PreservesPartialDiscovery_WhenEnumerationFails()
    {
        static IEnumerable<string> Discover()
        {
            yield return " PortableBrowser ";
            throw new IOException("Browser exited during discovery");
        }

        var resolver = new BrowserMediaSourceResolver(Discover);
        Assert.True(resolver.IsBrowserSource("portablebrowser"));
        Assert.False(resolver.IsBrowserSource("UnknownPlayer"));
    }

    [Theory]
    [InlineData("Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4", true)]
    [InlineData(" Chrome.exe ", true)]
    [InlineData("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge", true)]
    [InlineData("Mozilla.Firefox.Profile1", true)]
    [InlineData("ResearchPlayer", false)]
    [InlineData("EdgeMusicPlayer", false)]
    [InlineData("MyChromePlayer.exe", false)]
    [InlineData("22EB8429C9C8096C", false)]
    [InlineData("22EB8429C9C8096C;PrivateBrowsingAUMID", false)]
    [InlineData("Spotify.exe", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsBrowserSource_MatchesFamiliesAtIdentityBoundaries(string? source, bool expected)
    {
        var resolver = new BrowserMediaSourceResolver(() => []);
        Assert.Equal(expected, resolver.IsBrowserSource(source));
    }

    [Theory]
    [InlineData("Firefox-22EB8429C9C8096C", "floorp.exe", "22EB8429C9C8096C")]
    [InlineData("Helium", "chrome.exe", "imput.Helium.Profile1")]
    [InlineData("FutureBrowser", "future.exe", "Unbranded.opaque-id")]
    public void IsBrowserSource_UsesRegistrationMetadataWithoutBrandSpecialCases(string client, string executable, string appId)
    {
        var resolver = new BrowserMediaSourceResolver(() => BrowserMediaSourceResolver.GetRegistrationSourceIds(
            client, $"\"C:\\Program Files\\Browser\\{executable}\" -- \"%1\"", [appId]));

        Assert.True(resolver.IsBrowserSource(appId));
        Assert.True(resolver.IsBrowserSource(executable));
        Assert.True(resolver.IsBrowserSource(System.IO.Path.GetFileNameWithoutExtension(executable)));
        Assert.False(resolver.IsBrowserSource("0123456789ABCDEF"));
    }

    [Fact]
    public void GeckoRegistration_IncludesPrivateIdentityWithoutAcceptingArbitrarySuffixes()
    {
        var resolver = new BrowserMediaSourceResolver(() => BrowserMediaSourceResolver.GetRegistrationSourceIds(
            "Firefox-22EB8429C9C8096C", null, []));

        Assert.True(resolver.IsBrowserSource("22eb8429c9c8096c;PrivateBrowsingAUMID"));
        Assert.False(resolver.IsBrowserSource("22EB8429C9C8096C;other"));
    }

    [Fact]
    public void IsBrowserSource_ExpiresMissesAndPositiveDecisions()
    {
        int reads = 0;
        long now = 0;
        string[] sources = [];
        var resolver = new BrowserMediaSourceResolver(() => { reads++; return sources; }, () => now);

        Assert.False(resolver.IsBrowserSource("PortableBrowser"));
        sources = ["PortableBrowser"];
        Assert.False(resolver.IsBrowserSource("PortableBrowser"));
        Assert.Equal(1, reads);
        now = 5_000;
        Assert.True(resolver.IsBrowserSource("PortableBrowser"));
        Assert.True(resolver.IsBrowserSource("portablebrowser"));
        Assert.Equal(2, reads);
        sources = [];
        now += 60_000;
        Assert.False(resolver.IsBrowserSource("PortableBrowser"));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void IsBrowserSource_RetriesDiscoveryFailure_AndBoundsCacheGrowth()
    {
        long now = 0;
        int reads = 0;
        var resolver = new BrowserMediaSourceResolver(() => ++reads == 1
            ? throw new System.IO.IOException("Unavailable registry view") : ["PortableBrowser"], () => now);

        Assert.False(resolver.IsBrowserSource("PortableBrowser"));
        now = 5_000;
        Assert.True(resolver.IsBrowserSource("PortableBrowser"));
        for (int i = 0; i < 300; i++) { Assert.False(resolver.IsBrowserSource($"UnknownApp{i}")); }
        Assert.InRange(resolver.CachedSourceCount, 1, 128);
    }

    [Theory]
    [InlineData("Firefox-22EB8429C9C8096C", "22EB8429C9C8096C")]
    [InlineData("Floorp-22eb8429c9c8096c", "22eb8429c9c8096c")]
    [InlineData("Firefox-123ABC", "123ABC")]
    [InlineData("Microsoft Edge", null)]
    [InlineData("Brave", null)]
    [InlineData("Firefox-", null)]
    [InlineData("-22EB8429C9C8096C", null)]
    [InlineData("Firefox-22EB8429C9C8096CZ", null)]
    [InlineData("Firefox-122EB8429C9C8096C", null)]
    public void GetInstallationId_ExtractsOnlyRegisteredBrowserHashSuffixes(string clientName, string? expected)
    {
        Assert.Equal(expected, BrowserMediaSourceResolver.GetInstallationId(clientName));
    }

    [Theory]
    [InlineData("\"C:\\Browser With Spaces\\app.exe\" -url \"%1\"", "C:\\Browser With Spaces\\app.exe")]
    [InlineData("C:\\Browser With Spaces\\app.exe -- \"%1\"", "C:\\Browser With Spaces\\app.exe")]
    [InlineData("C:\\folder.exe\\browser.exe -- \"%1\"", "C:\\folder.exe\\browser.exe")]
    [InlineData("\"unterminated.exe", null)]
    [InlineData("\"C:\\document.html\"", null)]
    [InlineData("https://example.test", null)]
    [InlineData(null, null)]
    public void GetCommandExecutable_ParsesPathsWithoutExecutingCommands(string? command, string? expected)
    {
        Assert.Equal(expected, BrowserMediaSourceResolver.GetCommandExecutable(command));
    }

    [Theory]
    [InlineData("rundll32.exe")]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("explorer.exe")]
    public void GetRegistrationSourceIds_DoesNotClassifySharedCommandHostsAsBrowsers(string executable)
    {
        var sources = BrowserMediaSourceResolver.GetRegistrationSourceIds(
            "RegisteredBrowser", $"\"C:\\Windows\\{executable}\" arguments", ["Browser.ExplicitId"]);

        Assert.Contains("Browser.ExplicitId", sources);
        Assert.DoesNotContain(executable, sources);
        Assert.DoesNotContain(System.IO.Path.GetFileNameWithoutExtension(executable), sources);
    }

    [Theory]
    [InlineData(@"C:\Tor Browser\Browser", "xul.dll")]
    [InlineData(@"C:\Helium\Application", "chrome.dll")]
    [InlineData(@"C:\Unknown Gecko Fork", "xul.dll")]
    [InlineData(@"C:\Unknown Chromium Fork", "chrome.dll")]
    [InlineData(@"C:\Browser", "msedge.dll")]
    public void HasBrowserEngine_RecognizesPortableAndVersionedEngineLayouts(string root, string engine)
    {
        Assert.True(AudioDeviceHelper.HasBrowserEngine(root,
            path => path == System.IO.Path.Combine(root, engine), _ => []));
        string version = System.IO.Path.Combine(root, "140.0.1.2");
        Assert.True(AudioDeviceHelper.HasBrowserEngine(root,
            path => path == System.IO.Path.Combine(version, engine), _ => [version]));
    }

    [Fact]
    public void HasBrowserEngine_RejectsElectronCefAndUnrelatedDirectories()
    {
        string root = @"C:\Desktop App";
        Assert.False(AudioDeviceHelper.HasBrowserEngine(root,
            path => path.EndsWith("chrome_elf.dll") || path.EndsWith("libcef.dll"), _ => []));
        Assert.False(AudioDeviceHelper.HasBrowserEngine(root,
            path => path.EndsWith("msedge.dll") || path.EndsWith("msedgewebview2.exe"), _ => []));
        Assert.False(AudioDeviceHelper.HasBrowserEngine(root,
            path => path == System.IO.Path.Combine(root, "Downloads", "chrome.dll"), _ => [System.IO.Path.Combine(root, "Downloads")]));
        Assert.False(AudioDeviceHelper.IsBrowserWindowClass("ApplicationFrameWindow"));
        Assert.True(AudioDeviceHelper.IsBrowserWindowClass("MozillaWindowClass"));
        Assert.True(AudioDeviceHelper.IsBrowserWindowClass("Chrome_WidgetWin_1"));
    }
}
