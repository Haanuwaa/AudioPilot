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
        int applyCalls = 0;
        int persistCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppStartupToggleCoordinatorTests), "startup-toggle-stale.log");

        await AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
            new StartupToggleExecutionInput(TargetValue: true, DebounceMs: 0, OperationId: "startup:test"),
            new StartupToggleExecutionDependencies(
                IsStaleRequest: () => true,
                ApplyRegistryChangeAsync: () =>
                {
                    applyCalls++;
                    return Task.FromResult(true);
                },
                PersistSettingsAsync: () =>
                {
                    persistCalls++;
                    return Task.FromResult(true);
                }),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(0, applyCalls);
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task ExecuteDebouncedToggleAsync_SkipsPersist_WhenRequestBecomesStaleAfterApply()
    {
        int staleChecks = 0;
        int applyCalls = 0;
        int persistCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppStartupToggleCoordinatorTests), "startup-toggle-stale-after-apply.log");

        await AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
            new StartupToggleExecutionInput(TargetValue: true, DebounceMs: 0, OperationId: "startup:test"),
            new StartupToggleExecutionDependencies(
                IsStaleRequest: () => ++staleChecks > 1,
                ApplyRegistryChangeAsync: () =>
                {
                    applyCalls++;
                    return Task.FromResult(true);
                },
                PersistSettingsAsync: () =>
                {
                    persistCalls++;
                    return Task.FromResult(true);
                }),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(1, applyCalls);
        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task ExecuteDebouncedToggleAsync_SkipsPersist_WhenRegistryUpdateFails()
    {
        int persistCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppStartupToggleCoordinatorTests), "startup-toggle-apply-fail.log");

        await AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
            new StartupToggleExecutionInput(TargetValue: false, DebounceMs: 0, OperationId: "startup:test"),
            new StartupToggleExecutionDependencies(
                IsStaleRequest: () => false,
                ApplyRegistryChangeAsync: () => Task.FromResult(false),
                PersistSettingsAsync: () =>
                {
                    persistCalls++;
                    return Task.FromResult(true);
                }),
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(0, persistCalls);
    }

    [Fact]
    public async Task ExecuteDebouncedToggleAsync_LogsWarning_WhenSettingsPersistFails()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppStartupToggleCoordinatorTests), "startup-toggle-persist-warning.log", LogLevel.Warning);

        await AppStartupToggleCoordinator.ExecuteDebouncedToggleAsync(
            new StartupToggleExecutionInput(TargetValue: true, DebounceMs: 0, OperationId: "startup:test"),
            new StartupToggleExecutionDependencies(
                IsStaleRequest: () => false,
                ApplyRegistryChangeAsync: () => Task.FromResult(true),
                PersistSettingsAsync: () => Task.FromResult(false)),
            loggerScope.Logger,
            CancellationToken.None);

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains(AppConstants.Audio.LogEvents.ViewModel.App.StartupSyncWarning, logText, StringComparison.Ordinal);
        Assert.Contains("opId=startup:test", logText, StringComparison.Ordinal);
        Assert.Contains("reason=settings-write-failed", logText, StringComparison.Ordinal);
    }
}
