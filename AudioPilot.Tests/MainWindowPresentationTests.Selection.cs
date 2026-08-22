using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioPilot.Models;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
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
            var list = Assert.Single(Descendants(page).OfType<ListBox>());
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
                var button = Assert.Single(Descendants(page).OfType<Button>(), candidate => ReferenceEquals(candidate.Command, command));
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
                ? Assert.Single(Descendants(page).OfType<Button>(), button => ReferenceEquals(button.Command, model.ResumeAutomaticRoutinesCommand))
                : (FrameworkElement)Assert.Single(Descendants(page).OfType<CheckBox>(), check =>
                    AutomationProperties.GetName(check) == (tab == 0 ? "Enable output switching hotkey" : "Enable input switching hotkey"));
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
            var move = Assert.Single(Descendants(page).OfType<Button>(), button => ReferenceEquals(button.Command, actions[0]));
            move.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Assert.True(move.Command!.CanExecute(null));
            move.Command.Execute(null);
            Assert.Same(moved, list.Items[0]);
            Assert.Same(moved, list.SelectedItem);
        });
    }
}
