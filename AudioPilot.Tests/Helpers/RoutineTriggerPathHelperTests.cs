using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class RoutineTriggerPathHelperTests
{
    [Fact]
    public void NormalizeExecutablePath_FileUri_ReturnsLocalWindowsPath()
    {
        string normalized = RoutineTriggerPathHelper.NormalizeExecutablePath("file:///C:/Apps/Spotify/Spotify.exe");

        Assert.Equal(@"C:\Apps\Spotify\Spotify.exe", normalized);
    }

    [Fact]
    public void IsExecutablePathMatch_TreatsFileUriAndNativePathAsEquivalent()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutablePathMatch(
            "file:///C:/Apps/Spotify/Spotify.exe",
            @"C:\Apps\Spotify\Spotify.exe");

        Assert.True(matches);
    }

    [Fact]
    public void IsExecutableProcessMatch_MatchesSteamWebHelperUnderSteamInstall()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutableProcessMatch(
            @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe",
            @"C:\Program Files (x86)\Steam\steam.exe",
            "steamwebhelper");

        Assert.True(matches);
    }

    [Fact]
    public void IsExecutableProcessMatch_DoesNotMatchSteamWebHelperOutsideSteamInstall()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutableProcessMatch(
            @"C:\Temp\Steam\bin\cef\cef.win7x64\steamwebhelper.exe",
            @"C:\Program Files (x86)\Steam\steam.exe",
            "steamwebhelper");

        Assert.False(matches);
    }

    [Fact]
    public void IsExecutableProcessMatch_MatchesSquirrelVersionedAppExe()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutableProcessMatch(
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9236\Discord.exe",
            @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
            "Discord");

        Assert.True(matches);
    }

    [Fact]
    public void IsExecutableProcessMatch_DoesNotMatchDifferentSquirrelAppExe()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutableProcessMatch(
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9236\Slack.exe",
            @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
            "Slack");

        Assert.False(matches);
    }

    [Fact]
    public void IsExecutableProcessMatch_UsesExecutableNameBeforeProvidedProcessName()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutableProcessMatch(
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9236\Slack.exe",
            @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
            "Discord");

        Assert.False(matches);
    }

    [Fact]
    public void IsExecutableProcessMatch_DoesNotMatchSquirrelAppOutsideLauncherRoot()
    {
        bool matches = RoutineTriggerPathHelper.IsExecutableProcessMatch(
            @"C:\Users\ExampleUser\AppData\Local\OtherApp\app-1.0.9236\Discord.exe",
            @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
            "Discord");

        Assert.False(matches);
    }

    [Fact]
    public void TryExtractWindowsAppsPackageFamilyName_ReturnsPackageFamilyName()
    {
        bool extracted = RoutineTriggerPathHelper.TryExtractWindowsAppsPackageFamilyName(
            @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2512.29.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe",
            out string packageFamilyName);

        Assert.True(extracted);
        Assert.Equal("Microsoft.WindowsNotepad_8wekyb3d8bbwe", packageFamilyName);
    }

    [Fact]
    public void IsPackagedAppExecutablePathMatch_MatchesWindowsAppsPackageFamily()
    {
        bool matches = RoutineTriggerPathHelper.IsPackagedAppExecutablePathMatch(
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2512.29.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe");

        Assert.True(matches);
    }
}
