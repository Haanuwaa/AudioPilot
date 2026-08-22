using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppResetStateCoordinatorTests
{
    [Fact]
    public void BuildDefaultState_ReturnsExpectedDefaults()
    {
        ResetDefaultState state = AppResetStateCoordinator.BuildDefaultState();

        Assert.False(state.RunAtStartup);
        Assert.True(state.PreserveAudioLevels);
        Assert.True(state.OverlayEnabled);
        Assert.Equal(OverlayPosition.BottomRight, state.OverlayPosition);
        Assert.Equal(AppConstants.Timing.OverlayAutoHideSeconds.ToString("0.0"), state.OverlayDurationSecondsText);
        Assert.True(state.ShowBalloonOnFirstMinimize);
        Assert.False(state.ShowBalloonAfterSave);
    }

    [Fact]
    public void ResetPersistedConfiguration_ExecutesCoreResetSteps_AndAppliesDefaults()
    {
        int unregisterUserCalls = 0;
        int unregisterAllCalls = 0;
        int removeStartupCalls = 0;
        int deleteSettingsCalls = 0;
        int clearCachedCalls = 0;
        int clearRoutinesCalls = 0;
        Settings? updatedAudioSettings = null;
        ResetDefaultState? appliedState = null;
        int overlayCalls = 0;
        int syncCalls = 0;
        using var loggerScope = new TestLoggerScope(nameof(AppResetStateCoordinatorTests), "reset-state.log");

        AppResetStateCoordinator.ResetPersistedConfiguration(
            new ResetPersistedConfigurationDependencies(
                () => unregisterUserCalls++,
                () => unregisterAllCalls++,
                () => removeStartupCalls++,
                () => deleteSettingsCalls++,
                () => clearCachedCalls++,
                () => clearRoutinesCalls++,
                settings => updatedAudioSettings = settings,
                state => appliedState = state,
                (_, _, _) => overlayCalls++,
                () => syncCalls++),
            loggerScope.Logger);

        Assert.Equal(1, unregisterUserCalls);
        Assert.Equal(1, unregisterAllCalls);
        Assert.Equal(1, removeStartupCalls);
        Assert.Equal(1, deleteSettingsCalls);
        Assert.Equal(1, clearCachedCalls);
        Assert.Equal(1, clearRoutinesCalls);
        Assert.NotNull(updatedAudioSettings);
        Assert.NotNull(appliedState);
        Assert.Equal(1, overlayCalls);
        Assert.Equal(1, syncCalls);
    }

    [Fact]
    public async Task ResetUiSelectionsAsync_ExecutesUiResetSequence_AndRefreshesMixer()
    {
        List<string> calls = [];

        await AppResetStateCoordinator.ResetUiSelectionsAsync(
            new ResetUiSelectionDependencies(
                () => calls.Add("clear-output"),
                () => calls.Add("load-output"),
                () => calls.Add("reset-hotkeys"),
                () => calls.Add("clear-input"),
                () => calls.Add("load-input"),
                () =>
                {
                    calls.Add("refresh-mixer");
                    return Task.CompletedTask;
                }));

        Assert.Equal(["clear-output", "load-output", "reset-hotkeys", "clear-input", "load-input", "refresh-mixer"], calls);
    }
}
