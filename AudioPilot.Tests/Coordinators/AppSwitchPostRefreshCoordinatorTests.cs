using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchPostRefreshCoordinatorTests
{
    [Fact]
    public async Task ExecuteOutputPostSwitchRefreshAsync_SkipsMixerRefresh_WhenWindowHidden()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchPostRefreshCoordinatorTests), "switch-post-refresh-hidden.log");
        int cacheRefreshCalls = 0;
        int muteRefreshCalls = 0;
        int mixerRefreshCalls = 0;

        await AppSwitchPostRefreshCoordinator.ExecuteOutputPostSwitchRefreshAsync(
            new SwitchPostRefreshInput("switch:test", IsWindowVisible: false, IsCleaningUp: false),
            () => cacheRefreshCalls++,
            () =>
            {
                muteRefreshCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                mixerRefreshCalls++;
                return Task.CompletedTask;
            },
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(1, cacheRefreshCalls);
        Assert.Equal(1, muteRefreshCalls);
        Assert.Equal(0, mixerRefreshCalls);
    }

    [Fact]
    public async Task ExecuteOutputPostSwitchRefreshAsync_SkipsAllWork_WhenCleaningUp()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchPostRefreshCoordinatorTests), "switch-post-refresh-cleanup.log");
        int cacheRefreshCalls = 0;

        await AppSwitchPostRefreshCoordinator.ExecuteOutputPostSwitchRefreshAsync(
            new SwitchPostRefreshInput("switch:test", IsWindowVisible: true, IsCleaningUp: true),
            () => cacheRefreshCalls++,
            static () => Task.CompletedTask,
            static () => Task.CompletedTask,
            loggerScope.Logger,
            CancellationToken.None);

        Assert.Equal(0, cacheRefreshCalls);
    }

    [Theory]
    [InlineData(true, true, false, true, false, false, true)]
    [InlineData(true, false, false, false, true, false, true)]
    [InlineData(false, false, false, false, false, false, false)]
    public void ResolveMuteFlagUpdate_ReturnsExpectedStates(
        bool isPlaybackMuted,
        bool isMicMuted,
        bool currentDeafen,
        bool expectedDeafen,
        bool expectedMuteSound,
        bool expectedMuteMic,
        bool expectedChanged)
    {
        MuteFlagUpdateResult result = AppSwitchPostRefreshCoordinator.ResolveMuteFlagUpdate(
            isPlaybackMuted,
            isMicMuted,
            currentDeafen,
            currentMuteSound: false,
            currentMuteMic: false);

        Assert.Equal(expectedDeafen, result.NewDeafen);
        Assert.Equal(expectedMuteSound, result.NewMuteSound);
        Assert.Equal(expectedMuteMic, result.NewMuteMic);
        Assert.Equal(expectedChanged, result.AnyChanged);
    }
}
