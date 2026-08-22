using AudioPilot.Coordinators;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchInteractionCoordinatorTests
{
    [Fact]
    public async Task RegisterResumeHotkeysOnDispatcherAsync_MapsResult_AndRegistersRoutineHotkeys()
    {
        int dispatcherCalls = 0;
        int routineRegistrationCalls = 0;

        AppViewModel.ResumeHotkeyRegistrationResult result = await AppSwitchInteractionCoordinator.RegisterResumeHotkeysOnDispatcherAsync(
            callback =>
            {
                dispatcherCalls++;
                return Task.FromResult(callback());
            },
            () => new HotkeyRegistrationResult(
                ToggleAppVisibilityRegistered: true,
                MediaHotkeysRegistered: false,
                MuteHotkeysRegistered: true,
                ListenToInputRegistered: true,
                VolumeStepHotkeysRegistered: true,
                OutputSwitchRegistered: true,
                InputSwitchRegistered: false,
                OutputReverseSwitchRegistered: true,
                InputReverseSwitchRegistered: true),
            () =>
            {
                routineRegistrationCalls++;
                return new RoutineHotkeyRegistrationResult(RegisteredGroupCount: 1, FailedGroupCount: 0, ActiveRoutineCount: 1);
            });

        Assert.Equal(1, dispatcherCalls);
        Assert.Equal(1, routineRegistrationCalls);
        Assert.True(result.ToggleAppVisibilityRegistered);
        Assert.False(result.MediaHotkeysRegistered);
        Assert.True(result.VolumeStepHotkeysRegistered);
        Assert.False(result.InputSwitchRegistered);
        Assert.Equal(2, result.FailedCount);
    }

    [Fact]
    public async Task RegisterResumeHotkeysOnDispatcherAsync_ReportsRoutineRegistrationFailures()
    {
        AppViewModel.ResumeHotkeyRegistrationResult result = await AppSwitchInteractionCoordinator.RegisterResumeHotkeysOnDispatcherAsync(
            callback => Task.FromResult(callback()),
            () => new HotkeyRegistrationResult(true, true, true, true, true, true, true, true, true),
            () => new RoutineHotkeyRegistrationResult(RegisteredGroupCount: 0, FailedGroupCount: 1, ActiveRoutineCount: 1));

        Assert.False(result.RoutineHotkeysRegistered);
        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.FailedCount);
    }

}
