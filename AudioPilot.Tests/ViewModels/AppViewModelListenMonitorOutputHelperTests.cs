using System.Collections.ObjectModel;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelListenMonitorOutputHelperTests
{
    [Fact]
    public void RefreshOptions_PreservesUnavailableSelection()
    {
        ObservableCollection<CycleDevice> targetDevices = [];
        List<CycleDevice> availableDevices =
        [
            new CycleDevice { Id = "out-1", Name = "Speakers" },
        ];

        CycleDevice selected = AppViewModelListenMonitorOutputHelper.RefreshOptions(
            targetDevices,
            availableDevices,
            currentSelectedId: "monitor-1",
            currentSelectedName: "Office Dock");

        Assert.Equal("monitor-1", selected.Id);
        Assert.Equal("Office Dock", selected.Name);
        Assert.Collection(
            targetDevices,
            device =>
            {
                Assert.Equal(string.Empty, device.Id);
                Assert.Equal("Default output", device.Name);
            },
            device => Assert.Equal("out-1", device.Id),
            device =>
            {
                Assert.Equal("monitor-1", device.Id);
                Assert.Equal("Configured output (unavailable): Office Dock", device.Name);
            });
    }

    [Fact]
    public void RefreshOptions_DeduplicatesAndPrefersExplicitSelection()
    {
        ObservableCollection<CycleDevice> targetDevices = [];
        List<CycleDevice> availableDevices =
        [
            new CycleDevice { Id = "out-1", Name = "Speakers" },
            new CycleDevice { Id = "OUT-1", Name = "Speakers duplicate" },
            new CycleDevice { Id = "out-2", Name = "Headset" },
        ];

        CycleDevice selected = AppViewModelListenMonitorOutputHelper.RefreshOptions(
            targetDevices,
            availableDevices,
            currentSelectedId: "out-1",
            currentSelectedName: "Speakers",
            preferredDeviceId: "out-2");

        Assert.Equal("out-2", selected.Id);
        Assert.Collection(
            targetDevices,
            device => Assert.Equal(string.Empty, device.Id),
            device => Assert.Equal("out-1", device.Id),
            device => Assert.Equal("out-2", device.Id));
    }

    [Fact]
    public void RefreshOptions_RemapstoUniqueBestNameMatch_WhenConfiguredIdIsStale()
    {
        ObservableCollection<CycleDevice> targetDevices = [];
        List<CycleDevice> availableDevices =
        [
            new CycleDevice { Id = "fresh-output", Name = "Bluetooth Headset Stereo" },
        ];

        CycleDevice selected = AppViewModelListenMonitorOutputHelper.RefreshOptions(
            targetDevices,
            availableDevices,
            currentSelectedId: "missing-output",
            currentSelectedName: "Bluetooth Headset");

        Assert.Equal("fresh-output", selected.Id);
        Assert.Equal("Bluetooth Headset Stereo", selected.Name);
        Assert.DoesNotContain(targetDevices, static device => device.Id == "missing-output");
    }

    [Fact]
    public void RefreshOptions_PreservesDefaultOptionInstance_WhenDefaultIsSelected()
    {
        CycleDevice defaultOption = new() { Id = string.Empty, Name = "Default output" };
        ObservableCollection<CycleDevice> targetDevices =
        [
            defaultOption,
            new CycleDevice { Id = "out-1", Name = "Speakers" },
        ];
        List<CycleDevice> availableDevices =
        [
            new CycleDevice { Id = "out-1", Name = "Speakers" },
            new CycleDevice { Id = "out-2", Name = "Headset" },
        ];

        CycleDevice selected = AppViewModelListenMonitorOutputHelper.RefreshOptions(
            targetDevices,
            availableDevices,
            currentSelectedId: string.Empty,
            currentSelectedName: string.Empty);

        Assert.Equal(string.Empty, selected.Id);
        Assert.Same(defaultOption, targetDevices[0]);
        Assert.Collection(
            targetDevices,
            device => Assert.Equal(string.Empty, device.Id),
            device => Assert.Equal("out-1", device.Id),
            device => Assert.Equal("out-2", device.Id));
    }
}
