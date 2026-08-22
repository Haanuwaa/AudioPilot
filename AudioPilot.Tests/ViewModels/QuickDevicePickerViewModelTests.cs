using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class QuickDevicePickerViewModelTests
{
    [Theory]
    [InlineData(3u, 3)]
    [InlineData(4, 4)]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(uint.MaxValue, null)]
    [InlineData("Headphones", null)]
    public void DeviceType_UsesDriverMetadataAndFallsBackWithoutGuessing(object? value, int? expected)
    {
        var formFactor = AppViewModel.ParsePickerFormFactor(value);
        Assert.Equal(expected, (int?)formFactor);
        var device = new QuickDevicePickerItem("id", "Headphones", null, true, true, false, false, formFactor);
        Assert.Contains(device.DeviceType, device.HelpText);
        if (expected is null)
        {
            Assert.Equal("Audio output", device.DeviceType);
            Assert.Equal("\uE767", device.Glyph);
        }
        else
        {
            Assert.NotEqual("\uE767", device.Glyph);
        }
    }

    private static QuickDevicePickerItem Device(string id, bool current = false, bool output = true, bool available = true, bool reconnect = false)
        => new(id, id, null, output, available, current, reconnect);

    [Fact]
    public void Refresh_PreservesHighlightedIdentityAndSavedOrder_WhenCurrentDeviceChanges()
    {
        var model = new QuickDevicePickerViewModel();
        model.Update([Device("Speakers", current: true), Device("Headphones"), Device("Microphone", output: false)]);
        Assert.Equal("Headphones", model.Selected!.Id);
        model.Update([Device("Speakers"), Device("Headphones", current: true), Device("Microphone", output: false)]);
        Assert.Equal("Headphones", model.Selected!.Id);
        Assert.Equal(["Speakers", "Headphones"], model.Items.Select(item => item.Id));
        model.Update([Device("Speakers", current: true)]);
        Assert.Equal("Speakers", model.Selected!.Id);
    }

    [Fact]
    public void InitialSelection_WrapsSkipsUnavailableAndIncludesReconnectableDevices()
    {
        var model = new QuickDevicePickerViewModel();
        model.Update([Device("Offline", available: false), Device("Bluetooth", available: false, reconnect: true), Device("Speakers", current: true),
            Device("Mic", output: false, current: true), Device("Second mic", output: false)]);
        Assert.Equal("Bluetooth", model.Selected?.Id);
        Assert.True(model.CanSwitch);
        model.Output = false;
        Assert.Equal("Second mic", model.Selected?.Id);
        model.Output = true;
        Assert.Equal("Bluetooth", model.Selected?.Id);
    }

    [Fact]
    public void InitialSelection_HandlesMissingCurrentAndNoAlternativeWithoutSwitching()
    {
        var model = new QuickDevicePickerViewModel();
        model.Update([Device("Offline", available: false), Device("Speakers")]);
        Assert.Equal("Speakers", model.Selected?.Id);
        model.Update([Device("Speakers", current: true)]);
        Assert.Equal("Speakers", model.Selected?.Id);
        model.Update([Device("Offline", available: false)]);
        Assert.Equal("Offline", model.Selected?.Id);
        Assert.False(model.CanSwitch);
        model.Update([]);
        Assert.Null(model.Selected);
    }

    [Fact]
    public void SearchAndTabs_FilterWithoutChangingConfiguration_AndEmptyStateIsUseful()
    {
        var model = new QuickDevicePickerViewModel();
        model.Update([]);
        Assert.Contains("Add devices", model.Message);
        model.Update([Device("Headphones"), Device("Microphone", output: false)]);
        model.Search = "  PHONE  ";
        Assert.Equal("Headphones", Assert.Single(model.Items).Name);
        model.Output = false;
        Assert.Equal("Microphone", Assert.Single(model.Items).Name);
        model.Search = "absent";
        Assert.Empty(model.Items);
        Assert.Null(model.Selected);
        Assert.False(model.CanSwitch);
        Assert.Equal("No matching devices.", model.Message);
    }

    [Fact]
    public void BusyAndDisconnectedDevices_CannotSubmit_WhileBluetoothCanReconnect()
    {
        var model = new QuickDevicePickerViewModel();
        model.Update([Device("Disconnected", available: false), Device("Bluetooth", available: false, reconnect: true)]);
        Assert.Equal("Bluetooth", model.Selected!.Id);
        model.Move(-1);
        Assert.False(model.CanSwitch);
        model.Move(1);
        Assert.True(model.CanSwitch);
        model.Busy = true;
        Assert.False(model.CanSwitch);
        model.Move(-1);
        Assert.Equal("Bluetooth", model.Selected!.Id);
        model.Busy = false;
        Assert.True(model.CanSwitch);
    }
}
