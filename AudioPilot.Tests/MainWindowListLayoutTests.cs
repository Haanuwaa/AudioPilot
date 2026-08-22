using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowListLayoutTests
{
    private static readonly string[] MenuIconNames = ["Copy", "Cut", "Delete", "Duplicate", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault"];

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
                    Setters = { new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)) },
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
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.Show();
                tabs.SelectedIndex = 2;
                TestWindowFactory.CompleteInitialLayout(window);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                var list = Assert.Single(Descendants(content).OfType<ListBox>(), element => element.Name == "SavedRoutinesListBox");
                var pending = Assert.Single(Descendants(content).OfType<TextBlock>(), element => element.Name == "UnsavedRoutineChangesText");
                var run = Assert.Single(Descendants(content).OfType<Button>(), element => element.Name == "RunSelectedRoutineButton");
                Assert.Equal(117, list.Height);
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
                Assert.True(list.Height > 117);
                Assert.True(list.Height <= 260);
                Assert.Equal(list.Height, list.ActualHeight);
                Assert.True(run.ActualWidth > 0);
                Assert.True(run.TranslatePoint(new Point(run.ActualWidth, 0), content).X <= content.ActualWidth);
                window.Height = 378;
                TestWindowFactory.CompleteInitialLayout(window);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(117, list.Height);

                for (int tabIndex = 0; tabIndex < 2; tabIndex++)
                {
                    bool playback = tabIndex == 0;
                    tabs.SelectedIndex = tabIndex;
                    TestWindowFactory.CompleteInitialLayout(window);
                    var panel = Assert.IsType<UserControl>(((TabItem)tabs.Items[tabs.SelectedIndex]).Content, exactMatch: false);
                    var devices = playback ? harness.ViewModel.OutputCycleDevices : harness.ViewModel.InputCycleDevices;
                    var deviceList = Assert.IsType<ListBox>(panel.FindName("SwitchOrderListBox"));
                    for (int index = 0; index < 8; index++)
                        devices.Add(new CycleDevice { Id = $"device-{index}", Name = $"Device {index}", DisplayOrder = index + 1 });
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(104, deviceList.ActualHeight);
                    window.Height = 850;
                    TestWindowFactory.CompleteInitialLayout(window);
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    panel.UpdateLayout();
                    Assert.True(deviceList.ActualHeight > 104);
                    var viewer = Assert.Single(Descendants(deviceList).OfType<ScrollViewer>());
                    Assert.True(deviceList.ActualHeight < deviceList.MaxHeight,
                        $"height={deviceList.ActualHeight}, max={deviceList.MaxHeight}, extent={viewer.ExtentHeight}, viewport={viewer.ViewportHeight}");
                    Assert.True(viewer.ExtentHeight <= viewer.ViewportHeight);
                    for (int index = 8; index < 30; index++)
                        devices.Add(new CycleDevice { Id = $"device-{index}", Name = $"Device {index}", DisplayOrder = index + 1 });
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(deviceList.MaxHeight, deviceList.ActualHeight);
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
                    Assert.Equal(104, deviceList.ActualHeight);
                    devices.Clear();
                    TestWindowFactory.CompleteInitialLayout(window);
                    Assert.Equal(104, deviceList.ActualHeight);
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child))
                yield return descendant;
        }
    }
}
