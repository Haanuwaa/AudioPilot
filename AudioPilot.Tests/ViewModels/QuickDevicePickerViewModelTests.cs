using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class QuickDevicePickerViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inventory_ReservesExactAndStableIdentityBeforeEarlierNameRecovery(bool changedEndpointId)
    {
        CycleDevice[] configured =
        [
            new() { Id = "missing", Name = "Speakers" },
            new() { Id = "a", Name = "Headphones", StableId = "stable-a" },
            new() { Id = changedEndpointId ? "old-b" : "b", Name = "Speakers", StableId = "stable-b" },
        ];
        CycleDevice[] active = [configured[1].Clone(), new() { Id = "b", Name = "Speakers", StableId = "stable-b" }];

        var inventory = QuickDevicePickerItem.BuildInventory(configured, active, true, [], _ => true);
        Assert.Equal(["missing", "a", "b"], inventory.Select(item => item.Id));
        Assert.False(inventory[0].Available);
        Assert.True(inventory[0].CanReconnect);
        var model = new QuickDevicePickerViewModel { ShowAllDevices = true };
        model.Update(inventory);
        Assert.Equal(["a", "b"], model.Items.Select(item => item.Id));
        Assert.Equal(changedEndpointId ? "old-b" : "b", configured[2].Id);
    }

    [Fact]
    public void Inventory_DoesNotResolveAmbiguousStableIdentityAfterOtherEndpointsAreReserved()
    {
        var inventory = QuickDevicePickerItem.BuildInventory(
            [new() { Id = "a", Name = "Other" }, new() { Id = "old", Name = "Speakers", StableId = "shared" }],
            [new() { Id = "a", Name = "Other", StableId = "shared" }, new() { Id = "b", Name = "Speakers", StableId = "shared" }],
            true, [], _ => false);
        Assert.Equal(["a", "old", "b"], inventory.Select(item => item.Id));
        Assert.False(inventory[1].Available);
        Assert.False(inventory[2].Configured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_IsolatesFailedDirectionAndRecoversWithoutReportingEmptyDevices(bool failedOutput)
    {
        var error = new IOException("Driver read failed");
        QuickDevicePickerSnapshot snapshot = QuickDevicePickerSnapshot.Capture(output => output == failedOutput
            ? throw error : [Device(output ? "out" : "in", output: output)], CancellationToken.None);
        Assert.Same(error, failedOutput ? snapshot.OutputError : snapshot.InputError);
        Assert.Null(failedOutput ? snapshot.InputError : snapshot.OutputError);
        Assert.Equal(!failedOutput, Assert.Single(snapshot.Items).Output);
        var model = new QuickDevicePickerViewModel { Output = failedOutput, ShowAllDevices = true };
        model.Update(snapshot.Items, outputFailed: snapshot.OutputError != null, inputFailed: snapshot.InputError != null);
        Assert.Empty(model.Items);
        Assert.False(model.CanSwitch);
        Assert.Contains("Could not read", model.Message);
        Assert.DoesNotContain("No available", model.Message);
        model.Output = !failedOutput;
        Assert.Single(model.Items);
        Assert.True(model.CanSwitch);
        Assert.Empty(model.Message);
        model.Update(snapshot.Items);
        model.Output = failedOutput;
        Assert.Equal($"No available {(failedOutput ? "output" : "input")} devices.", model.Message);
    }

    [Fact]
    public void Snapshot_CancellationDoesNotBecomeAPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => QuickDevicePickerSnapshot.Capture(output =>
        {
            reads++;
            cancellation.Cancel();
            return [Device("out")];
        }, cancellation.Token));
        Assert.Equal(1, reads);
        Assert.ThrowsAny<OperationCanceledException>(() => QuickDevicePickerSnapshot.Capture(_ =>
            throw new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void Inventory_UsesResolvedIdentityKeepsSavedOrderAndAppendsUniqueAvailableDevices()
    {
        CycleDevice[] configured =
        [
            new() { Id = "old-headset", Name = "Old name", StableId = " Opaque " },
            new() { Id = "speakers", Name = "Desk speakers" },
            new() { Id = "offline", Name = "Disconnected headset" },
        ];
        CycleDevice[] available =
        [
            new() { Id = "z", Name = "Z display" },
            new() { Id = "new-headset", Name = "New name", StableId = " Opaque " },
            new() { Id = "a2", Name = "A display" },
            new() { Id = "speakers", Name = "Desk speakers" },
            new() { Id = "a1", Name = "A display" },
            new() { Id = "SPEAKERS", Name = "Duplicate" },
        ];

        var inventory = QuickDevicePickerItem.BuildInventory(configured, available, true, ["new-headset"], device => device.Id == "offline");

        Assert.Equal(["new-headset", "speakers", "offline", "a1", "a2", "z"], inventory.Select(item => item.Id));
        Assert.Equal(" Opaque ", inventory[0].StableId);
        Assert.True(inventory[0].Current);
        Assert.True(inventory[2].CanReconnect);
        Assert.All(inventory.Take(3), item => Assert.True(item.Configured));
        Assert.All(inventory.Skip(3), item => Assert.False(item.Configured));
        Assert.Equal("old-headset", configured[0].Id);
        Assert.Equal(3, configured.Length);
    }

    [Fact]
    public void AllDevices_TogglesScopePreservesSelectionAndUsesAvailableDevicesWithoutConfiguration()
    {
        var model = new QuickDevicePickerViewModel();
        model.Update([
            Device("Offline", available: false, reconnect: true), Device("Speakers", current: true),
            Device("Headphones") with { Configured = false },
            Device("Mic", output: false) with { Configured = false },
        ]);
        Assert.False(model.ShowAllDevices);
        Assert.Equal(["Offline", "Speakers"], model.Items.Select(item => item.Id));
        model.Selected = model.Items[1];
        model.ShowAllDevices = true;
        Assert.Equal(["Speakers", "Headphones"], model.Items.Select(item => item.Id));
        Assert.Equal("Speakers", model.Selected?.Id);
        model.Search = "Head";
        Assert.Equal("Headphones", Assert.Single(model.Items).Id);
        Assert.Equal(2, model.DeviceCount);
        model.Search = "";
        model.Output = false;
        Assert.Equal("Mic", Assert.Single(model.Items).Id);
        model.ShowAllDevices = false;
        Assert.Empty(model.Items);
        Assert.Contains("Show all available devices", model.Message);
        model.ShowAllDevices = true;
        Assert.Equal("Mic", model.Selected?.Id);
        model.Update([]);
        Assert.Equal("No available input devices.", model.Message);
    }

    [Fact]
    public void AllDevices_RefreshRemovesDisconnectedEndpointsAndChoosesAnotherAvailableDevice()
    {
        var model = new QuickDevicePickerViewModel { ShowAllDevices = true };
        model.Update([Device("Speakers", current: true), Device("Headphones") with { Configured = false }]);
        Assert.Equal("Headphones", model.Selected?.Id);
        model.Update([Device("Speakers", current: true), Device("Headphones", available: false, reconnect: true)]);
        Assert.Equal("Speakers", Assert.Single(model.Items).Id);
        Assert.Equal("Speakers", model.Selected?.Id);
        model.ShowAllDevices = false;
        Assert.Equal(2, model.Items.Count);
    }

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
