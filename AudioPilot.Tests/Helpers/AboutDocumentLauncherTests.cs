using AudioPilot.Constants;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class AboutDocumentLauncherTests
{
    [Fact]
    public void ResolveTarget_WhenGeneratedAboutExists_ReturnsLocalFile()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "AudioPilot", "about-test");
        string expected = Path.Combine(baseDirectory, AppConstants.Files.AboutFileName);

        string target = AboutDocumentLauncher.ResolveTarget(
            baseDirectory,
            path => string.Equals(path, expected, StringComparison.Ordinal));

        Assert.Equal(expected, target);
    }

    [Fact]
    public void ResolveTarget_WhenGeneratedAboutIsUnavailable_ReturnsOnlineUserGuide()
    {
        string target = AboutDocumentLauncher.ResolveTarget(
            Path.Combine(Path.GetTempPath(), "AudioPilot", "about-test"),
            static _ => false);

        Assert.Equal(AppConstants.Links.UserGuideUrl, target);
    }
}
