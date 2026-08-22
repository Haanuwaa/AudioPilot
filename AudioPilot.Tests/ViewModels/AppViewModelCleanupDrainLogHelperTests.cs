using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelCleanupDrainLogHelperTests
{
    [Fact]
    public void CreateCallbacks_LogsInitialTimeoutAndGraceCompletion()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelCleanupDrainLogHelperTests), "cleanup-drain.log");

        var callbacks = AppViewModelCleanupDrainLogHelper.CreateCallbacks(loggerScope.Logger, "cleanup:test-grace");

        callbacks.LogInitialTimeout(2, 1, 3);
        callbacks.LogCompletedWithinGrace(1, 3);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppViewModelCleanupDrainLogHelper.BuildInitialTimeoutMessage("cleanup:test-grace", 2, 1, 3), logText, StringComparison.Ordinal);
        Assert.Contains(AppViewModelCleanupDrainLogHelper.BuildCompletedWithinGraceMessage("cleanup:test-grace", 1, 3), logText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCallbacks_LogsForcedTimeout()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelCleanupDrainLogHelperTests), "cleanup-drain-forced.log");

        var callbacks = AppViewModelCleanupDrainLogHelper.CreateCallbacks(loggerScope.Logger, "cleanup:test-forced");

        callbacks.LogForcedTimeout(3, 2, 4);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppViewModelCleanupDrainLogHelper.BuildForcedTimeoutMessage("cleanup:test-forced", 3, 2, 4), logText, StringComparison.Ordinal);
    }
}
