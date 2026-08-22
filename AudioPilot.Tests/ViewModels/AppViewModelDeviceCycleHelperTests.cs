using System.Collections.ObjectModel;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelDeviceCycleHelperTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    public void IsAllDevicesSelection_ReturnsExpectedValue(int selectedIndex, bool expected)
    {
        bool result = AppViewModelDeviceCycleHelper.IsAllDevicesSelection(selectedIndex);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1, -2)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public void ToDeviceIndex_MapsComboIndexToDeviceIndex(int selectedIndex, int expected)
    {
        int result = AppViewModelDeviceCycleHelper.ToDeviceIndex(selectedIndex);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public void ToComboIndex_MapsDeviceIndexToComboIndex(int deviceIndex, int expected)
    {
        int result = AppViewModelDeviceCycleHelper.ToComboIndex(deviceIndex);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CanAddAnyDevice_ReturnsFalse_WhenAllAvailableDevicesAlreadyConfigured()
    {
        var available = new[]
        {
            new CycleDevice { Id = "a", Name = "A" },
            new CycleDevice { Id = "b", Name = "B" },
        };

        var configured = new[]
        {
            new CycleDevice { Id = "A", Name = "Alpha" },
            new CycleDevice { Id = "B", Name = "Beta" },
        };

        bool canAdd = AppViewModelDeviceCycleHelper.CanAddAnyDevice(available, configured);

        Assert.False(canAdd);
    }

    [Fact]
    public void CanAddAnyDevice_ReturnsTrue_WhenAtLeastOneAvailableDeviceMissingFromCycle()
    {
        var available = new[]
        {
            new CycleDevice { Id = "a", Name = "A" },
            new CycleDevice { Id = "b", Name = "B" },
        };

        var configured = new[]
        {
            new CycleDevice { Id = "a", Name = "A" },
        };

        bool canAdd = AppViewModelDeviceCycleHelper.CanAddAnyDevice(available, configured);

        Assert.True(canAdd);
    }

    [Fact]
    public void ApplyCycleFromSettingsCore_RemovesDuplicates_AndPreservesOrder()
    {
        ObservableCollection<CycleDevice> target =
        [
            new CycleDevice { Id = "old", Name = "Old" },
        ];

        AppViewModelDeviceCycleHelper.ApplyCycleFromSettingsCore(
            target,
            [
                new CycleDevice { Id = "out-1", Name = "Speakers" },
                new CycleDevice { Id = "OUT-1", Name = "Duplicate" },
                new CycleDevice { Id = "out-2", Name = "Headset" },
            ]);

        Assert.Equal(["out-1", "out-2"], [.. target.Select(static device => device.Id)]);
        Assert.Equal(["Speakers", "Headset"], [.. target.Select(static device => device.Name)]);
    }

    [Fact]
    public void SyncCycleDevices_ReturnsTrue_WhenSourceBecomesEmpty()
    {
        ObservableCollection<CycleDevice> target =
        [
            new CycleDevice { Id = "out-1", Name = "Speakers" },
        ];

        bool changed = AppViewModelDeviceCycleHelper.SyncCycleDevices(target, []);

        Assert.True(changed);
        Assert.Empty(target);
    }

    [Fact]
    public void ReconcilePersistedDevice_RefreshesName_WhenIdMatches()
    {
        CycleDevice reconciled = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
            new CycleDevice { Id = "out-1", Name = "Old Headset" },
            [new CycleDevice { Id = "out-1", Name = "Renamed Headset" }]);

        Assert.Equal("out-1", reconciled.Id);
        Assert.Equal("Renamed Headset", reconciled.Name);
    }

    [Fact]
    public void ReconcilePersistedDevice_RemapstoUniqueBestNameMatch_WhenIdIsStale()
    {
        CycleDevice reconciled = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
            new CycleDevice { Id = "stale-id", Name = "JBL Tune 500" },
            [new CycleDevice { Id = "fresh-id", Name = "JBL Tune 500 Hands-Free AG Audio" }]);

        Assert.Equal("fresh-id", reconciled.Id);
        Assert.Equal("JBL Tune 500 Hands-Free AG Audio", reconciled.Name);
    }

    [Fact]
    public void ReconcilePersistedDevice_PreservesOriginal_WhenNameMatchIsAmbiguous()
    {
        CycleDevice reconciled = AppViewModelDeviceCycleHelper.ReconcilePersistedDevice(
            new CycleDevice { Id = "stale-id", Name = "Desk Speaker" },
            [
                new CycleDevice { Id = "fresh-a", Name = "Desk Speaker" },
                new CycleDevice { Id = "fresh-b", Name = "Desk Speaker" },
            ]);

        Assert.Equal("stale-id", reconciled.Id);
        Assert.Equal("Desk Speaker", reconciled.Name);
    }
}
