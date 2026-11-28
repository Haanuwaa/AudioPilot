using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Runtime;

public sealed class AppRuntimeHostStartupOutcomeTests
{
    [Fact]
    public void HasConfiguredMediaHotkeys_ReturnsFalseWhenNoneConfiguredAndTrueWhenAnyIsSet()
    {
        Assert.False(AppRuntimeHost.HasConfiguredMediaHotkeys(null));
        Assert.False(AppRuntimeHost.HasConfiguredMediaHotkeys(new Settings()));

        Settings withPlayPause = new();
        withPlayPause.Hotkeys.Media.PlayPause = "Ctrl+Alt+P";
        Assert.True(AppRuntimeHost.HasConfiguredMediaHotkeys(withPlayPause));

        Settings withSeek = new();
        withSeek.Hotkeys.Media.SeekForward = "Ctrl+Alt+Right";
        Assert.True(AppRuntimeHost.HasConfiguredMediaHotkeys(withSeek));
    }

    [Fact]
    public void EnsureStartupSucceeded_AcceptsSucceededOutcome()
    {
        Exception? exception = Record.Exception(() =>
            AppRuntimeHost.EnsureStartupSucceeded(AppRuntimeStartupInitializationOutcome.Succeeded));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureStartupSucceeded_RejectsCancelledOutcome()
    {
        AssertRejectedOutcome(AppRuntimeStartupInitializationOutcome.Cancelled);
    }

    [Fact]
    public void EnsureStartupSucceeded_RejectsFatalOutcome()
    {
        AssertRejectedOutcome(AppRuntimeStartupInitializationOutcome.Fatal);
    }

    private static void AssertRejectedOutcome(AppRuntimeStartupInitializationOutcome outcome)
    {
        AppRuntimeStartupAbortedException exception =
            Assert.Throws<AppRuntimeStartupAbortedException>(() => AppRuntimeHost.EnsureStartupSucceeded(outcome));

        Assert.Equal(outcome, exception.Outcome);
    }
}
