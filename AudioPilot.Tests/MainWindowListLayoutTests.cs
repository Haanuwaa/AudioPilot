using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowListLayoutTests
{
    private static readonly string[] MenuIconNames = ["Copy", "Cut", "Delete", "Duplicate", "Expand", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault", "Settings"];

    [Theory]
    [InlineData(AppTheme.Dark, false)]
    [InlineData(AppTheme.Light, true)]
    public void DeferredLists_ResizeAndReflectRoutineSelectionAndPendingEdits(AppTheme theme, bool autoSave)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            Window? originalMainWindow = application.MainWindow;
            ResourceDictionary resources = new()
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new AudioPilot.Helpers.InverseBooleanToVisibilityConverter(),
                ["CycleDeviceListBoxItemStyle"] = new Style(typeof(ListBoxItem))
                {
                    Setters = { new Setter(FrameworkElement.HeightProperty, 24d), new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)) },
                },
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative),
            });
            foreach (string name in MenuIconNames)
                resources[$"AppMenu{name}Icon"] = new DrawingImage();

            using var workspace = new TestSettingsWorkspace(nameof(MainWindowListLayoutTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.SetCachedSettings(new Settings { Miscellaneous = new() { AutoSaveEnabled = autoSave } });
            var routine = new AudioRoutine { Id = "test", Name = "Test", MasterVolumePercent = 35 };
            harness.ViewModel.Routines.Add(routine);
            harness.ViewModel.Routines.Add(new AudioRoutine { Id = "second", Name = "Second", MasterVolumePercent = 55 });
            harness.ViewModel.Routines.Add(new AudioRoutine { Id = "third", Name = "Third", MasterVolumePercent = 85 });
            var shell = TestPrivateAccess.GetField<AppShellService>(harness.ViewModel, "_shell");
            MainWindow? window = null;
            try
            {
                application.Resources = resources;
                window = new MainWindow(new MainWindowDependencies(harness.ViewModel, shell,
                    new MainWindowVisibilityCoordinator(Logger.Instance), static () => Task.CompletedTask));
                var tabs = (TabControl)window.FindName("DeviceTabControl");
                var routinesTab = (TabItem)tabs.Items[2];
                Assert.Null(routinesTab.Content);
                tabs.SelectedIndex = 2;
                Assert.Null(DeferredTabContentBehavior.GetTemplate(routinesTab));
                var content = Assert.IsType<StackPanel>(routinesTab.Content);
                TestWindowFactory.ShowWindowForLayoutTest(window);
                tabs.SelectedIndex = 2;
                TestWindowFactory.CompleteInitialLayout(window);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                var list = Assert.Single(WpfVisualTree.Descendants(content).OfType<ListBox>(), element => element.Name == "SavedRoutinesListBox");
                Assert.Contains(list.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.CommandParameter, "Routines"));
                var routineExpand = Assert.Single(WpfVisualTree.Descendants(content).OfType<Button>(), button =>
                    ReferenceEquals(harness.ViewModel.ManageListCommand, button.Command) && button.Width == 24);
                var routineTitle = Assert.Single(WpfVisualTree.Descendants(content).OfType<TextBlock>(), block => block.Name == "SavedRoutinesTitle");
                Assert.EndsWith(" (3)", routineTitle.Text, StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, routineExpand.Visibility);
                Assert.Null(routineExpand.ToolTip);
                Assert.Equal("Open full list", AutomationProperties.GetHelpText(routineExpand));
                var pending = Assert.Single(WpfVisualTree.Descendants(content).OfType<TextBlock>(), element => element.Name == "UnsavedRoutineChangesText");
                var run = Assert.Single(WpfVisualTree.Descendants(content).OfType<Button>(), element => element.Name == "RunSelectedRoutineButton");
                double initialListHeight = list.Height;
                Assert.Equal(128, initialListHeight);
                var routineViewer = Assert.Single(WpfVisualTree.Descendants(list).OfType<ScrollViewer>());
                Assert.Equal(2, routineViewer.ViewportHeight);
                Assert.Equal(3, routineViewer.ExtentHeight);
                var firstRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(60, firstRow.ActualHeight);
                Assert.Equal(routine.DisplayName, AutomationProperties.GetName(firstRow));
                Assert.Equal(autoSave ? Visibility.Collapsed : Visibility.Visible, pending.Visibility);
                list.SelectedIndex = 0;
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(run.IsEnabled);
                Assert.Same(harness.ViewModel.RunSelectedRoutineCommand, run.Command);
                routine.Enabled = false;
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(run.IsEnabled);

                window.Height = 550;
                TestWindowFactory.CompleteInitialLayout(window);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                content.Measure(new Size(400, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                Assert.True(list.Height > initialListHeight);
                Assert.True(list.Height <= 260);
                Assert.Equal(list.Height, list.ActualHeight);
                Assert.True(run.ActualWidth > 0);
                Assert.True(run.TranslatePoint(new Point(run.ActualWidth, 0), content).X <= content.ActualWidth);
                window.Height = 378;
                TestWindowFactory.CompleteInitialLayout(window);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(128, list.Height);

                var manager = new ListManagerWindow(harness.ViewModel, harness.Audio, ListManagerMode.Routines, CancellationToken.None);
                try
                {
                    TestWindowFactory.ShowWindowForLayoutTest(manager);
                    TestWindowFactory.CompleteInitialLayout(manager);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    var managedRows = Assert.IsType<ListBox>(manager.FindName("Rows"));
                    var sort = Assert.IsType<ComboBox>(manager.FindName("SortBox"));
                    var search = Assert.IsType<TextBox>(manager.FindName("SearchBox"));
                    var move = Assert.IsType<Button>(manager.FindName("MoveButton"));
                    var moveUp = Assert.Single(move.ContextMenu.Items.OfType<MenuItem>(), item => item.Name == "MoveUpMenuItem");
                    var moveDown = Assert.Single(move.ContextMenu.Items.OfType<MenuItem>(), item => item.Name == "MoveDownMenuItem");
                    var moveTop = Assert.Single(move.ContextMenu.Items.OfType<MenuItem>(), item => item.Name == "MoveTopMenuItem");
                    var moveBottom = Assert.Single(move.ContextMenu.Items.OfType<MenuItem>(), item => item.Name == "MoveBottomMenuItem");
                    Assert.Equal(3, managedRows.Items.Count);
                    sort.SelectedIndex = 1;
                    Assert.Equal("Second", Assert.IsType<ListManagerRow>(managedRows.Items[0]).Name);
                    managedRows.SelectedIndex = 0;
                    Assert.False(move.IsEnabled);
                    sort.SelectedIndex = 0;
                    Assert.Equal("Second", Assert.IsType<ListManagerRow>(managedRows.SelectedItem).Name);
                    search.Text = "sec";
                    Assert.Single(managedRows.Items);
                    Assert.False(move.IsEnabled);
                    search.Clear();
                    Assert.True(move.IsEnabled);
                    Assert.True(moveDown.IsEnabled);
                    moveDown.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Second", harness.ViewModel.Routines[2].Name);
                    Assert.False(moveBottom.IsEnabled);
                    Assert.True(moveUp.IsEnabled);
                    moveUp.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Second", harness.ViewModel.Routines[1].Name);
                    moveTop.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Second", harness.ViewModel.Routines[0].Name);
                    Assert.Equal("Second", Assert.IsType<ListManagerRow>(managedRows.SelectedItem).Name);

                    managedRows.SelectedItems.Add(managedRows.Items[1]);
                    moveDown.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(["Third", "Second", "Test"], harness.ViewModel.Routines.Select(item => item.Name));
                    Assert.Equal(2, managedRows.SelectedItems.Count);

                    manager.Width = manager.MinWidth;
                    manager.Height = manager.MinHeight;
                    TestWindowFactory.CompleteInitialLayout(manager);
                    var managerContent = Assert.IsType<Grid>(manager.Content);
                    var add = Assert.IsType<Button>(manager.FindName("AddButton"));
                    var remove = Assert.IsType<Button>(manager.FindName("RemoveButton"));
                    var save = Assert.IsType<Button>(manager.FindName("SaveButton"));
                    var close = Assert.Single(WpfVisualTree.Descendants(managerContent).OfType<Button>(), button => button.IsCancel);
                    double actionY = move.TranslatePoint(new Point(), managerContent).Y;
                    Assert.Equal(actionY, add.TranslatePoint(new Point(), managerContent).Y);
                    Assert.Equal(actionY, remove.TranslatePoint(new Point(), managerContent).Y);
                    Assert.True(save.TranslatePoint(new Point(0, save.ActualHeight), managerContent).Y <= managerContent.ActualHeight);
                    Assert.True(close.TranslatePoint(new Point(0, close.ActualHeight), managerContent).Y <= managerContent.ActualHeight);

                    managedRows.SelectedItems.Clear();
                    managedRows.SelectedIndex = 0;
                    var enable = Assert.IsType<Button>(manager.FindName("EnabledButton"));
                    Assert.True(enable.IsEnabled);
                    var selected = Assert.IsType<AudioRoutine>(Assert.IsType<ListManagerRow>(managedRows.SelectedItem).Item);
                    Assert.True(selected.Enabled);
                    enable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(selected.Enabled);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal("_Enable", enable.Content);
                    managedRows.SelectedItems.Add(managedRows.Items[1]);
                    Assert.True(remove.IsEnabled);
                    remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("Test", Assert.Single(harness.ViewModel.Routines).Name);
                }
                finally { manager.Close(); }

                harness.ViewModel.Routines.Clear();
                Assert.Equal("Saved routines", routineTitle.Text.Trim());
                Assert.Equal(Visibility.Collapsed, routineExpand.Visibility);

                for (int tabIndex = 0; tabIndex < 2; tabIndex++)
                {
                    bool playback = tabIndex == 0;
                    tabs.SelectedIndex = tabIndex;
                    TestWindowFactory.CompleteInitialLayout(window);
                    var panel = Assert.IsType<UserControl>(((TabItem)tabs.Items[tabs.SelectedIndex]).Content, exactMatch: false);
                    var devices = playback ? harness.ViewModel.OutputCycleDevices : harness.ViewModel.InputCycleDevices;
                    var deviceList = Assert.IsType<ListBox>(panel.FindName("SwitchOrderListBox"));
                    var deviceTitle = Assert.IsType<TextBlock>(panel.FindName("SwitchOrderTitle"));
                    var deviceExpand = Assert.Single(WpfVisualTree.Descendants(panel).OfType<Button>(), button =>
                        ReferenceEquals(harness.ViewModel.ManageListCommand, button.Command));
                    Assert.Contains(deviceList.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.CommandParameter, playback ? "Output" : "Input"));
                    List<CycleDevice> noDevices = [];
                    List<CycleDevice> available = [new CycleDevice { Id = "available", Name = "Available" }];
                    TestPrivateAccess.InvokeNonPublic(harness.ViewModel, "ApplyAvailableDeviceInventory", noDevices, noDevices);
                    Assert.Equal(playback ? "Output switch order" : "Input switch order", deviceTitle.Text.Trim());
                    Assert.Equal(Visibility.Collapsed, deviceExpand.Visibility);
                    TestPrivateAccess.InvokeNonPublic(harness.ViewModel, "ApplyAvailableDeviceInventory",
                        playback ? available : noDevices, playback ? noDevices : available);
                    Assert.Equal(Visibility.Visible, deviceExpand.Visibility);
                    Assert.Null(deviceExpand.ToolTip);
                    Assert.Equal("Open full list", AutomationProperties.GetHelpText(deviceExpand));
                    TestPrivateAccess.InvokeNonPublic(harness.ViewModel, "ApplyAvailableDeviceInventory", noDevices, noDevices);

                    for (int index = 0; index < 8; index++)
                        devices.Add(new CycleDevice { Id = $"device-{index}", Name = $"Device {index}", DisplayOrder = index + 1 });
                    Assert.EndsWith(" (8)", deviceTitle.Text, StringComparison.Ordinal);
                    Assert.Equal(Visibility.Visible, deviceExpand.Visibility);
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(104, deviceList.ActualHeight);
                    window.Height = 850;
                    TestWindowFactory.CompleteInitialLayout(window);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    panel.UpdateLayout();
                    Assert.True(deviceList.ActualHeight > 104);
                    double resizedHeight = deviceList.ActualHeight;
                    var viewer = Assert.Single(WpfVisualTree.Descendants(deviceList).OfType<ScrollViewer>());
                    Assert.True(deviceList.ActualHeight < deviceList.MaxHeight,
                        $"height={deviceList.ActualHeight}, max={deviceList.MaxHeight}, extent={viewer.ExtentHeight}, viewport={viewer.ViewportHeight}");
                    Assert.True(viewer.ExtentHeight <= viewer.ViewportHeight);
                    for (int index = 8; index < 30; index++)
                        devices.Add(new CycleDevice { Id = $"device-{index}", Name = $"Device {index}", DisplayOrder = index + 1 });
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(resizedHeight, deviceList.ActualHeight);
                    Assert.True(viewer.ExtentHeight > viewer.ViewportHeight);
                    window.Height = 378;
                    TestWindowFactory.CompleteInitialLayout(window);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    panel.UpdateLayout();
                    Assert.Equal(104, deviceList.ActualHeight);
                    window.Height = 850;
                    TestWindowFactory.CompleteInitialLayout(window);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    while (devices.Count > 1)
                        devices.RemoveAt(devices.Count - 1);
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(resizedHeight, deviceList.ActualHeight);
                    devices.Clear();
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(resizedHeight, deviceList.ActualHeight);
                    Assert.Equal(playback ? "Output switch order" : "Input switch order", deviceTitle.Text.Trim());
                    Assert.Equal(Visibility.Collapsed, deviceExpand.Visibility);
                    window.Height = 378;
                    TestWindowFactory.CompleteInitialLayout(window);
                }
            }
            finally
            {
                if (window != null)
                {
                    window.AllowCloseForRuntimeShutdown();
                    window.Close();
                }
                application.Resources = originalResources;
                application.MainWindow = originalMainWindow;
            }
        });
    }
}
