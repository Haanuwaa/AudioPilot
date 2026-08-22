using System.Reflection;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelVolumeControlExpansionTests
{
    [Fact]
    public void ResolveVolumeControlExpansionState_DefaultsToMasterExpanded_WhenNeitherHasHotkeys()
    {
        (bool masterExpanded, bool micExpanded) = InvokeResolveVolumeControlExpansionState(
            hasMasterHotkeys: false,
            hasMicHotkeys: false);

        Assert.True(masterExpanded);
        Assert.False(micExpanded);
    }

    [Fact]
    public void ResolveVolumeControlExpansionState_ExpandsMaster_WhenOnlyMasterHasHotkeys()
    {
        (bool masterExpanded, bool micExpanded) = InvokeResolveVolumeControlExpansionState(
            hasMasterHotkeys: true,
            hasMicHotkeys: false);

        Assert.True(masterExpanded);
        Assert.False(micExpanded);
    }

    [Fact]
    public void ResolveVolumeControlExpansionState_ExpandsMic_WhenOnlyMicHasHotkeys()
    {
        (bool masterExpanded, bool micExpanded) = InvokeResolveVolumeControlExpansionState(
            hasMasterHotkeys: false,
            hasMicHotkeys: true);

        Assert.False(masterExpanded);
        Assert.True(micExpanded);
    }

    [Fact]
    public void ResolveVolumeControlExpansionState_ExpandsBoth_WhenBothHaveHotkeys()
    {
        (bool masterExpanded, bool micExpanded) = InvokeResolveVolumeControlExpansionState(
            hasMasterHotkeys: true,
            hasMicHotkeys: true);

        Assert.True(masterExpanded);
        Assert.True(micExpanded);
    }

    private static (bool masterExpanded, bool micExpanded) InvokeResolveVolumeControlExpansionState(bool hasMasterHotkeys, bool hasMicHotkeys)
    {
        MethodInfo? method = typeof(AppViewModel).GetMethod("ResolveVolumeControlExpansionState", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object? result = method!.Invoke(null, [hasMasterHotkeys, hasMicHotkeys]);
        Assert.NotNull(result);

        Type resultType = result!.GetType();
        bool masterExpanded = (bool)(resultType.GetField("Item1")?.GetValue(result) ?? resultType.GetProperty("MasterExpanded")!.GetValue(result)!);
        bool micExpanded = (bool)(resultType.GetField("Item2")?.GetValue(result) ?? resultType.GetProperty("MicExpanded")!.GetValue(result)!);
        return (masterExpanded, micExpanded);
    }
}
