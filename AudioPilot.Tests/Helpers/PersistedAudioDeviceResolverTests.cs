using System.Collections.ObjectModel;
using System.Text.Json;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Helpers;

public sealed class PersistedAudioDeviceResolverTests
{
    [Fact]
    public void StableIdentity_RecoversChangedIdAndName_WithoutChoosingIdenticalPeripheral()
    {
        var configured = new CycleDevice { Id = "old-id", Name = "Headset", StableId = " Opaque-ID " };
        var intended = new CycleDevice { Id = "new-id", Name = "Renamed headset", StableId = configured.StableId };
        CycleDevice? result = PersistedAudioDeviceResolver.TryResolveMatch(configured,
            [new() { Id = "other-id", Name = "Headset", StableId = "other" }, intended]);
        Assert.Same(intended, result);
    }

    [Theory]
    [InlineData("opaque-ID")]
    [InlineData(" Opaque-ID ")]
    [InlineData(null)]
    public void StableIdentity_IsOptionalAndComparedWithoutNormalization(string? availableId)
    {
        Assert.Null(PersistedAudioDeviceResolver.TryResolveMatch(
            new CycleDevice { Id = "old", Name = "Original", StableId = "Opaque-ID" },
            [new() { Id = "new", Name = "Different", StableId = availableId }]));
    }

    [Fact]
    public void StableIdentity_RejectsDuplicateStableMatches()
    {
        Assert.Null(PersistedAudioDeviceResolver.TryResolveMatch(
            new CycleDevice { Id = "old", Name = "Original", StableId = "same" },
            [new() { Id = "a", Name = "Original", StableId = "same" }, new() { Id = "b", StableId = "same" }]));
    }

    [Fact]
    public void CycleSwitch_ReservesStableIdentityBeforeApplyingNameFallbacks()
    {
        SwitchCycleSnapshotState state = AppSwitchCycleStateResolver.BuildCycleSnapshotState(
            [new() { Id = "old-name-only", Name = "Speakers" }, new() { Id = "old-stable", Name = "Old name", StableId = "fixed" }],
            [new() { Id = "intended", Name = "Speakers", StableId = "fixed" }]);
        CycleDevice connected = Assert.Single(state.ConnectedCycle);
        Assert.Equal("intended", connected.Id);
        Assert.Equal("fixed", connected.StableId);
        Assert.Equal("old-name-only", Assert.Single(state.SkippedDevices).Id);
    }

    [Fact]
    public void StableIdentity_SurvivesSettingsNormalizationCloningAndCycleRefresh()
    {
        const string stable = " Opaque-CaSe-Sensitive ";
        var settings = new Settings();
        settings.DeviceSwitching.Output.CycleDevices = [new() { Id = "ordinary", Name = "Device", StableId = stable }];
        settings.Routines.Items = [new() { Id = "routine", Name = "Routine", OutputDeviceId = "ordinary", OutputDeviceStableId = stable }];
        Settings imported = SettingsTransferService.ParseImportedSettings(JsonSerializer.Serialize(settings, SettingsJson.Options), null, true);
        CycleDevice saved = Assert.Single(imported.DeviceSwitching.Output.CycleDevices);
        Assert.Equal(stable, saved.Clone().StableId);
        Assert.Equal(stable, Assert.Single(imported.Routines.Items).Clone().OutputDeviceStableId);
        var current = new ObservableCollection<CycleDevice> { new() { Id = saved.Id, Name = saved.Name } };
        Assert.True(AppViewModelDeviceCycleHelper.SyncCycleDevices(current, [saved]));
        Assert.Equal(stable, Assert.Single(current).StableId);
    }
}
