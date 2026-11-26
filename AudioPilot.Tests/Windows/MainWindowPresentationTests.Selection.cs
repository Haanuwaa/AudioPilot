using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioPilot.Localization;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task DirectSwitchFeedbackFollowsItsTabAndLiveLanguage(AppTheme theme) => WithWindow(theme, async (window, model) =>
    {
        var feedback = (TextBlock)window.FindName("DeviceSwitchFeedbackText");
        Assert.Equal(Visibility.Collapsed, feedback.Visibility);
        TestPrivateAccess.InvokeNonPublic(model, "SetDeviceSwitchFeedback", true, new LocalizedText("Device_SwitchUnavailable", "Desk headset"));
        foreach (string language in LiveLanguageSequence)
        {
            model.SettingsLanguageDraft = language;
            model.SelectedSettingsTabIndex = 0;
            await Layout(window, DeviceWindowWidths[0], 440);
            Assert.Equal(Visibility.Visible, feedback.Visibility);
            Assert.Contains(language == "es" ? "Dispositivo no disponible" : "Device unavailable", feedback.Text, StringComparison.Ordinal);
            Assert.Contains("Desk headset", feedback.Text, StringComparison.Ordinal);
            Assert.True(feedback.ActualWidth <= ((FrameworkElement)window.Content).ActualWidth);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(feedback));
            model.SelectedSettingsTabIndex = 1;
            await Layout(window, DeviceWindowWidths[0], 440);
            Assert.Equal(Visibility.Collapsed, feedback.Visibility);
        }
        TestPrivateAccess.InvokeNonPublic(model, "SetDeviceSwitchFeedback", true, null);
        model.SelectedSettingsTabIndex = 0;
        await Layout(window, DeviceWindowWidths[0], 440);
        Assert.Equal(Visibility.Collapsed, feedback.Visibility);
    });

    [Theory]
    [InlineData(AppTheme.Dark, 0)]
    [InlineData(AppTheme.Light, 0)]
    [InlineData(AppTheme.Dark, 1)]
    [InlineData(AppTheme.Light, 1)]
    public Task SwitchToSelectedPreservesSelectionAndFitsDuringLiveLanguageChanges(AppTheme theme, int tab) => WithWindow(theme, async (window, model) =>
    {
        var output = new CycleDevice { Id = "fixture-output", Name = "Fixture output", StableId = "stable-output" };
        var input = new CycleDevice { Id = "fixture-input", Name = "Fixture input", StableId = "stable-input" };
        model.OutputCycleDevices.Add(output);
        model.OutputCycleDevices.Add(new CycleDevice { Id = "other-output", Name = "Other output" });
        model.InputCycleDevices.Add(input);
        model.InputCycleDevices.Add(new CycleDevice { Id = "other-input", Name = "Other input" });
        TestPrivateAccess.InvokeNonPublic(model, "ApplyAvailableDeviceInventory", new AudioDeviceInventory([output]), new AudioDeviceInventory([input]));
        var tabs = (TabControl)window.FindName("DeviceTabControl");
        tabs.SelectedIndex = tab;
        await Layout(window, DeviceWindowWidths[0], 600);
        var list = Assert.Single(WpfVisualTree.Descendants((FrameworkElement)tabs.SelectedContent).OfType<ListBox>());
        var selection = tab == 0 ? model.SelectedOutputCycleDevices : model.SelectedInputCycleDevices;
        var button = (Button)window.FindName("SwitchToSelectedDeviceButton");
        var footer = (WrapPanel)window.FindName("DeviceSettingsActions");
        Assert.False(button.IsEnabled);
        list.SelectedIndex = 0;
        Assert.True(button.IsEnabled);
        foreach (string language in LiveLanguageSequence)
            foreach (double width in DeviceWindowWidths)
            {
                model.SettingsLanguageDraft = language;
                await Layout(window, width, 600);
                Assert.Equal(language == "es" ? "Usar seleccionado" : "Switch to selected", button.Content);
                Button[] actions = [.. footer.Children.OfType<Button>()];
                Assert.Equal(3, actions.Length);
                double row = actions[0].TransformToAncestor(footer).Transform(new Point()).Y;
                foreach (Button action in actions)
                {
                    Point position = action.TransformToAncestor(footer).Transform(new Point());
                    Assert.InRange(Math.Abs(position.Y - row), 0, 1);
                    Assert.True(position.X + action.ActualWidth <= footer.ActualWidth + 1);
                    var caption = new TextBlock { Text = Assert.IsType<string>(action.Content), FontFamily = action.FontFamily, FontSize = action.FontSize };
                    caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(caption.DesiredSize.Width <= action.ActualWidth - action.Padding.Left - action.Padding.Right + 1);
                }
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, list, button) { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent });
                Assert.Same(tab == 0 ? output : input, Assert.Single(selection));
                Assert.True(button.IsEnabled);
                WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
                Capture((FrameworkElement)window.Content, $"switch-selected-{theme}-{tab}-{language}-{width}", (int)width, 600);
            }
        list.SelectAll();
        Assert.False(button.IsEnabled);
        list.UnselectAll();
        list.SelectedIndex = 0;
        var emptyInventory = new AudioDeviceInventory([]);
        TestPrivateAccess.InvokeNonPublic(model, "ApplyAvailableDeviceInventory", emptyInventory, emptyInventory);
        Assert.False(button.IsEnabled);
        var replacement = new CycleDevice { Id = "replacement-endpoint", Name = "Renamed endpoint", StableId = tab == 0 ? output.StableId : input.StableId };
        TestPrivateAccess.InvokeNonPublic(model, "ApplyAvailableDeviceInventory",
            tab == 0 ? new AudioDeviceInventory([replacement]) : emptyInventory,
            tab == 1 ? new AudioDeviceInventory([replacement]) : emptyInventory);
        Assert.True(button.IsEnabled);
        model.BeginRuntimeShutdown();
        Assert.False(model.SwitchToSelectedDeviceCommand.CanExecute(null));
        await window.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Assert.False(button.IsEnabled);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ListSelectionsClearForOtherWidgets_AndRemainForListActionsAndScrolling(int tab)
    {
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            for (int index = 0; index < 2; index++)
            {
                model.OutputCycleDevices.Add(new CycleDevice { Id = $"output-{index}", Name = $"Output {index}" });
                model.InputCycleDevices.Add(new CycleDevice { Id = $"input-{index}", Name = $"Input {index}" });
                model.Routines.Add(new AudioRoutine { Id = $"routine-{index}", Name = $"Routine {index}", MasterVolumePercent = 35 });
            }
            model.PauseAutomaticRoutines(null);
            var tabs = (TabControl)window.FindName("DeviceTabControl");
            tabs.SelectedIndex = tab;
            await Layout(window, 414, 340);
            var page = (FrameworkElement)tabs.SelectedContent;
            var list = Assert.Single(WpfVisualTree.Descendants(page).OfType<ListBox>());
            IList selected = tab switch
            {
                0 => model.SelectedOutputCycleDevices,
                1 => model.SelectedInputCycleDevices,
                _ => model.SelectedRoutines,
            };
            ICommand[] actions = tab switch
            {
                0 => [model.MoveOutputCycleDeviceUpCommand, model.MoveOutputCycleDeviceDownCommand, model.RemoveOutputCycleDeviceCommand],
                1 => [model.MoveInputCycleDeviceUpCommand, model.MoveInputCycleDeviceDownCommand, model.RemoveInputCycleDeviceCommand],
                _ => [model.MoveRoutineUpCommand, model.MoveRoutineDownCommand, model.RemoveRoutineCommand, model.EditRoutineCommand,
                    model.EnableSelectedRoutinesCommand, model.DisableSelectedRoutinesCommand, model.RunSelectedRoutineCommand],
            };
            list.SelectAll();
            Assert.Equal(2, selected.Count);
            await Layout(window, 414, 340);
            foreach (ICommand command in actions)
            {
                var button = Assert.Single(WpfVisualTree.Descendants(page).OfType<Button>(), candidate => ReferenceEquals(candidate.Command, command));
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, list, button) { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent });
                Assert.Equal(2, selected.Count);
                Assert.Equal(2, list.SelectedItems.Count);
            }

            var viewer = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var scrollbar = Assert.IsType<ScrollBar>(viewer.Template.FindName("PART_VerticalScrollBar", viewer));
            scrollbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.Equal(2, selected.Count);

            var unrelated = tab == 2
                ? Assert.Single(WpfVisualTree.Descendants(page).OfType<Button>(), button => ReferenceEquals(button.Command, model.ResumeAutomaticRoutinesCommand))
                : (FrameworkElement)Assert.Single(WpfVisualTree.Descendants(page).OfType<CheckBox>(), check =>
                    AutomationProperties.GetName(check) == (tab == 0 ? "Enable output switching shortcut" : "Enable input switching shortcut"));
            unrelated.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.Empty(selected);
            Assert.Empty(list.SelectedItems);
            Assert.Equal(-1, list.SelectedIndex);
            list.SelectAll();
            unrelated.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, list, unrelated) { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent });
            Assert.Empty(selected);
            Assert.Empty(list.SelectedItems);

            object moved = list.Items[1];
            list.SelectedIndex = 1;
            var move = Assert.Single(WpfVisualTree.Descendants(page).OfType<Button>(), button => ReferenceEquals(button.Command, actions[0]));
            move.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.True(move.Command!.CanExecute(null));
            move.Command.Execute(null);
            Assert.Same(moved, list.Items[0]);
            Assert.Same(moved, list.SelectedItem);
        });
    }
}
