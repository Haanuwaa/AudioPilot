using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppStartupToggleCoordinatorTests
{
    [Fact]
    public async Task ExecuteDebouncedToggleAsync_SkipsWork_WhenRequestIsAlreadyStale()
    {
        int commitCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppStartupToggleCoordinatorTests), "startup-toggle-stale.log");

        await AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
            new StartupToggleExecutionInput(DebounceMs: 0, OperationId: "startup:test"),
            new StartupToggleExecutionDependencies(
                IsStaleRequest: () => true,
                ApplyAndPersistAsync: () =>
                {
                    commitCalls++;
                    return Task.FromResult(true);
                }),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(0, commitCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteDebouncedToggleAsync_LogsWarning_OnlyWhenCommitFails(bool succeeded)
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupToggleCoordinatorTests), "startup-toggle-warning.log", LogLevel.Warning);

        await AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
            new StartupToggleExecutionInput(DebounceMs: 0, OperationId: "startup:test"),
            new StartupToggleExecutionDependencies(
                IsStaleRequest: () => false,
                ApplyAndPersistAsync: () => Task.FromResult(succeeded)),
            loggerScope.Logger,
            CancellationToken.None);

        string logText = loggerScope.DisposeAndReadLogText();
        if (succeeded)
        {
            Assert.DoesNotContain(AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning, logText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning, logText, StringComparison.Ordinal);
            Assert.Contains("opId=startup:test", logText, StringComparison.Ordinal);
            Assert.Contains("reason=registration-or-settings-write-failed", logText, StringComparison.Ordinal);
        }
    }
}
