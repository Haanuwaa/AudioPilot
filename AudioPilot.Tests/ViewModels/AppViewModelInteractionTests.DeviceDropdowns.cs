using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.ViewModels;

public sealed partial class AppViewModelInteractionTests
{
    [Theory]
    [InlineData(true, "none")]
    [InlineData(false, "none")]
    [InlineData(true, "device")]
    [InlineData(false, "device")]
    [InlineData(true, "all")]
    [InlineData(false, "all")]
    [InlineData(true, "device-kept")]
    [InlineData(false, "device-kept")]
    [InlineData(true, "all-kept")]
    [InlineData(false, "all-kept")]
    [InlineData(true, "none", true)]
    [InlineData(false, "none", true)]
    [InlineData(true, "device", true)]
    [InlineData(false, "all", true)]
    public void ReconnectedDropdownDeviceRestoresSelectionUnlessUserChoseAnotherOption(bool output, string userChoice, bool endpointIdChanged = false)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            AppViewModel model = harness.ViewModel;
            List<CycleDevice> devices = TestPrivateAccess.GetField<List<CycleDevice>>(model, output ? "_outputDevices" : "_inputDevices");
            ObservableCollection<string> names = output ? model.AvailableOutputDeviceNames : model.AvailableInputDeviceNames;
            devices.Clear();
            devices.Add(new() { Id = "returning", Name = "Same name", StableId = "opaque" });
            names.Add("All Devices");
            names.Add("Same name");
            var combo = new ComboBox { ItemsSource = names };
            combo.SetBinding(ComboBox.SelectedIndexProperty, new Binding(output ? nameof(AppViewModel.SelectedAvailableOutputIndex) : nameof(AppViewModel.SelectedAvailableInputIndex))
            {
                Source = model,
                Mode = BindingMode.TwoWay,
            });
            combo.SelectedIndex = 1;
            MethodInfo load = typeof(AppViewModel).GetMethod(output ? "LoadAvailableOutputDevices" : "LoadAvailableInputDevices", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Refresh(List<CycleDevice> snapshot)
            {
                int current = output ? model.SelectedAvailableOutputIndex : model.SelectedAvailableInputIndex;
                int selected = (int)load.Invoke(model, [devices, names, snapshot, current])!;
                if (output) model.SelectedAvailableOutputIndex = selected;
                else model.SelectedAvailableInputIndex = selected;
            }

            Refresh([new() { Id = "other", Name = "Same name" }]);
            Assert.Equal(-1, combo.SelectedIndex);
            Assert.False((output ? model.AddOutputCycleDeviceCommand : model.AddInputCycleDeviceCommand).CanExecute(null));
            if (output) model.SelectedAvailableInputIndex = 0;
            else model.SelectedAvailableOutputIndex = 0;
            Refresh([new() { Id = "other", Name = "Same name" }]);
            Assert.Equal(-1, combo.SelectedIndex);
            if (userChoice != "none")
            {
                combo.SelectedIndex = userChoice.StartsWith("all", StringComparison.Ordinal) ? 0 : 1;
                if (!userChoice.EndsWith("kept", StringComparison.Ordinal)) combo.SelectedIndex = -1;
            }
            Refresh([new() { Id = "other", Name = "Renamed other" }]);
            int selectionBeforeReturn = userChoice == "device-kept" ? 1 : userChoice == "all-kept" ? 0 : -1;
            Assert.Equal(selectionBeforeReturn, combo.SelectedIndex);
            Refresh([new() { Id = "other", Name = "Renamed other" }, new() { Id = endpointIdChanged ? "new-id" : "returning", Name = "Returned device", StableId = "opaque" }]);
            Assert.Equal(userChoice == "none" ? 2 : selectionBeforeReturn, combo.SelectedIndex);
            string? expectedName = userChoice == "none" ? "Returned device" : userChoice == "device-kept" ? "Renamed other" : userChoice == "all-kept" ? "All Devices" : null;
            Assert.Equal(expectedName, combo.SelectedItem);
            Assert.Equal(combo.SelectedIndex >= 0, (output ? model.AddOutputCycleDeviceCommand : model.AddInputCycleDeviceCommand).CanExecute(null));
            BindingOperations.ClearAllBindings(combo);
        });
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void RefreshDeviceDropdown_PreservesUnchangedSelectionAndDisablesAddWhenSelectedEndpointDisappears(bool output, bool disappeared)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            AppViewModel model = harness.ViewModel;
            List<CycleDevice> devices = TestPrivateAccess.GetField<List<CycleDevice>>(model, output ? "_outputDevices" : "_inputDevices");
            ObservableCollection<string> names = output ? model.AvailableOutputDeviceNames : model.AvailableInputDeviceNames;
            devices.Clear();
            devices.AddRange([new() { Id = "a", Name = "Same name" }, new() { Id = "b", Name = "Same name" }]);
            names.Clear();
            names.Add("All Devices");
            names.Add("Same name");
            names.Add("Same name");
            var combo = new ComboBox { ItemsSource = names };
            combo.SetBinding(ComboBox.SelectedIndexProperty, new Binding(output ? nameof(AppViewModel.SelectedAvailableOutputIndex) : nameof(AppViewModel.SelectedAvailableInputIndex))
            {
                Source = model,
                Mode = BindingMode.TwoWay,
            });
            if (output) model.SelectedAvailableOutputIndex = 1;
            else model.SelectedAvailableInputIndex = 1;
            int notifications = 0;
            names.CollectionChanged += (_, _) => notifications++;
            List<CycleDevice> snapshot = disappeared ? [devices[1].Clone()] : [.. devices.Select(device => device.Clone())];
            MethodInfo method = typeof(AppViewModel).GetMethod(output ? "LoadAvailableOutputDevices" : "LoadAvailableInputDevices", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int selected = (int)method.Invoke(model, [devices, names, snapshot, 1])!;
            if (output) model.SelectedAvailableOutputIndex = selected;
            else model.SelectedAvailableInputIndex = selected;

            Assert.Equal(disappeared ? -1 : 1, combo.SelectedIndex);
            var add = output ? model.AddOutputCycleDeviceCommand : model.AddInputCycleDeviceCommand;
            Assert.Equal(!disappeared, add.CanExecute(null));
            if (!disappeared)
            {
                Assert.Equal(0, notifications);
                Assert.Equal("Same name", combo.SelectedItem);
            }
            BindingOperations.ClearAllBindings(combo);
        });
    }

    [Fact]
    public void ApplyExternallyReloadedSettings_ReusesKnownEndpointsWithoutEnumeratingOrResettingDropdowns()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            AppViewModel model = harness.ViewModel;
            model.SetKnownActiveDeviceInfosForTests([new() { Id = "output", Name = "Output" }], [new() { Id = "input", Name = "Input" }]);
            model.AvailableOutputDeviceNames.Add("All Devices");
            model.AvailableOutputDeviceNames.Add("Output");
            model.AvailableInputDeviceNames.Add("All Devices");
            model.AvailableInputDeviceNames.Add("Input");
            model.SelectedAvailableOutputIndex = 1;
            model.SelectedAvailableInputIndex = 1;
            int notifications = 0;
            model.AvailableOutputDeviceNames.CollectionChanged += (_, _) => notifications++;
            model.AvailableInputDeviceNames.CollectionChanged += (_, _) => notifications++;
            int endpointQueries = 0;
            using var readerLock = new ReaderWriterLockSlim();
            AudioDeviceEndpointQueryHelper previous = TestPrivateAccess.GetField<AudioDeviceEndpointQueryHelper>(harness.Audio, "_endpointQueryHelper");
            TestPrivateAccess.SetField(harness.Audio, "_endpointQueryHelper", new AudioDeviceEndpointQueryHelper(
                null!, readerLock, Logger.Instance,
                () => { endpointQueries++; return true; },
                static () => [Role.Multimedia], static () => [Role.Console], static (_, _) => { }));
            var settings = new Settings();
            settings.Hotkeys.App.ToggleAppVisibility = string.Empty;
            settings.DeviceSwitching.Output.CycleDevices = [new() { Id = "output", Name = "Output" }];
            settings.DeviceSwitching.Input.CycleDevices = [new() { Id = "input", Name = "Input" }];
            try
            {
                typeof(AppViewModel).GetMethod("ApplyExternallyReloadedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(model, [settings, "test-reload"]);
                Assert.Equal(0, endpointQueries);
                Assert.Equal(0, notifications);
                Assert.Equal(1, model.SelectedAvailableOutputIndex);
                Assert.Equal(1, model.SelectedAvailableInputIndex);
                Assert.Equal("output", Assert.Single(model.OutputCycleDevices).Id);
                Assert.Equal("input", Assert.Single(model.InputCycleDevices).Id);
            }
            finally
            {
                TestPrivateAccess.SetField(harness.Audio, "_endpointQueryHelper", previous);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefreshDeviceDropdown_RechecksAddAllWhenEndpointIdsChangeWithoutChangingNames(bool output)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = CreateHarness(Dispatcher.CurrentDispatcher);
            AppViewModel model = harness.ViewModel;
            List<CycleDevice> devices = TestPrivateAccess.GetField<List<CycleDevice>>(model, output ? "_outputDevices" : "_inputDevices");
            ObservableCollection<string> names = output ? model.AvailableOutputDeviceNames : model.AvailableInputDeviceNames;
            devices.Clear();
            devices.Add(new() { Id = "old", Name = "Same name" });
            names.Add("All Devices");
            names.Add("Same name");
            (output ? model.OutputCycleDevices : model.InputCycleDevices).Add(devices[0].Clone());
            if (output) model.SelectedAvailableOutputIndex = 0;
            else model.SelectedAvailableInputIndex = 0;
            var add = output ? model.AddOutputCycleDeviceCommand : model.AddInputCycleDeviceCommand;
            var button = new Button { Command = add };
            Assert.False(button.IsEnabled);
            MethodInfo method = typeof(AppViewModel).GetMethod(output ? "LoadAvailableOutputDevices" : "LoadAvailableInputDevices", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int selected = (int)method.Invoke(model, [devices, names, new List<CycleDevice> { new() { Id = "new", Name = "Same name" } }, 0])!;
            if (output) model.SelectedAvailableOutputIndex = selected;
            else model.SelectedAvailableInputIndex = selected;
            Assert.True(button.IsEnabled);
            button.Command = null;
        });
    }
}
