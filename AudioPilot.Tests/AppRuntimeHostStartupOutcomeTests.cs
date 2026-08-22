using AudioPilot.Coordinators;

namespace AudioPilot.Tests;

public sealed class AppRuntimeHostStartupOutcomeTests
{
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
