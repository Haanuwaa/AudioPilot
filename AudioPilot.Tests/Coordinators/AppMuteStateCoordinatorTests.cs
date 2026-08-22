using AudioPilot.Coordinators;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppMuteStateCoordinatorTests
{
    [Fact]
    public void ResolveDeafenChange_ClearsIndividualMuteFlags_AndNotifiesAllProperties()
    {
        MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveDeafenChange(true);

        Assert.True(plan.DeviceMuteMicrophone);
        Assert.True(plan.DeviceMutePlayback);
        Assert.True(plan.NewDeafen);
        Assert.False(plan.NewMuteMic);
        Assert.False(plan.NewMuteSound);
        Assert.Equal(LogLevel.Info, plan.LogLevel);
        Assert.Contains(nameof(AudioPilot.ViewModels.AppViewModel.Deafen), plan.PropertyNamesToNotify);
        Assert.Contains(nameof(AudioPilot.ViewModels.AppViewModel.MuteMic), plan.PropertyNamesToNotify);
        Assert.Contains(nameof(AudioPilot.ViewModels.AppViewModel.MuteSound), plan.PropertyNamesToNotify);
    }

    [Fact]
    public void ResolveMuteMicChange_PreservesPlaybackMuteWhenDeafened()
    {
        MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveMuteMicChange(
            value: false,
            deafenValue: true,
            currentMuteSound: false);

        Assert.True(plan.DeviceMuteMicrophone);
        Assert.True(plan.DeviceMutePlayback);
        Assert.True(plan.NewDeafen);
        Assert.False(plan.NewMuteMic);
    }

    [Fact]
    public void ResolveMuteSoundChange_UsesCurrentMicMuteForDeviceMutePlan()
    {
        MuteStateChangePlan plan = AppMuteStateCoordinator.ResolveMuteSoundChange(
            value: false,
            deafenValue: false,
            currentMuteMic: true);

        Assert.True(plan.DeviceMuteMicrophone);
        Assert.False(plan.DeviceMutePlayback);
        Assert.True(plan.NewMuteMic);
        Assert.False(plan.NewMuteSound);
        Assert.Equal(LogLevel.Trace, plan.LogLevel);
    }
}
