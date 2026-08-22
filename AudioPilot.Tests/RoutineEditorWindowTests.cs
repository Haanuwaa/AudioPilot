using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineEditorWindowTests
{
    private static readonly string[] NetworkRefreshButtonNames = ["TriggerNetworkRefreshButton", "ConditionNetworkRefreshButton"];
    private static readonly string[] MenuIconNames = ["Undo", "Redo", "Cut", "Copy", "Paste", "Delete", "SelectAll"];

    [Theory]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.Hotkey)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.Hotkey)]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.Network)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.Network)]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.Scheduled)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.Scheduled)]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.Application)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.Application)]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.DeviceAvailability)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.DeviceAvailability)]
    [InlineData(AppTheme.Dark, RoutineTriggerKind.SessionUnlock)]
    [InlineData(AppTheme.Light, RoutineTriggerKind.SystemResume)]
    public void CompactEditor_KeepsControlsInViewportAndVolumeOnlyRoutineSubmittable(AppTheme theme, RoutineTriggerKind trigger)
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
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative),
            });
            foreach (string name in MenuIconNames)
            {
                resources[$"AppMenu{name}Icon"] = new DrawingImage();
            }

            IReadOnlyList<string> networks = ["Home", "Office"];
            using var viewModel = new RoutineEditorViewModel([], [], new AudioRoutine
            {
                Name = "Volume routine",
                SwitchOutputPerApp = theme == AppTheme.Light,
                TargetAppPath = @"C:\Apps\DifferentTarget.exe",
                OutputDeviceId = theme == AppTheme.Light ? "unavailable-output" : "",
                TriggerKind = trigger,
                TriggerDevice = new() { Id = "unavailable", Name = "Saved missing audio device" },
                Conditions = new() { Device = new() { Id = "out", Name = "Required device" }, RunningAppPath = @"C:\Apps\Required.exe", ConnectedNetwork = "Home" },
                CommunicationsOutput = theme == AppTheme.Dark ? new() { Id = "comm-out", Name = "Saved communications speaker" } : null,
                CommunicationsInput = theme == AppTheme.Dark ? new() { Id = "comm-in", Name = "Saved communications microphone", Playback = false } : null,
                MasterVolumePercent = 45,
                ShowInTrayMenu = true,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                TriggerAppPath = @"C:\Apps\Player.exe",
                ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            }, loadAvailableNetworkNamesAsync: _ => Task.FromResult(networks));
            RoutineEditorWindow? window = null;
            try
            {
                application.Resources = resources;
                window = new RoutineEditorWindow(viewModel);
                FrameworkElement content = Assert.IsType<FrameworkElement>(window.Content, exactMatch: false);
                content.Measure(new Size(424, 442));
                content.Arrange(new Rect(0, 0, 424, 442));
                content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                var viewer = (ScrollViewer)window.FindName("RoutineEditorScrollViewer");
                Assert.Equal(0, viewer.VerticalOffset);
                Assert.True(viewModel.IsVolumeTargetsExpanded);
                var outputMute = Assert.IsType<ComboBox>(window.FindName("OutputMuteActionBox"));
                var inputMute = Assert.IsType<ComboBox>(window.FindName("InputMuteActionBox"));
                Assert.Equal(3, outputMute.Items.Count);
                outputMute.SelectedValue = RoutineMuteAction.Mute;
                inputMute.SelectedValue = RoutineMuteAction.Unmute;
                Assert.Equal(RoutineMuteAction.Mute, viewModel.OutputMuteAction);
                Assert.Equal(RoutineMuteAction.Unmute, viewModel.InputMuteAction);
                Assert.True(Assert.Single(Descendants(content).OfType<Button>(), button => button.IsDefault).IsEnabled);
                Assert.Null(viewModel.Validate());
                Assert.Null(window.ResultRoutine);

                FrameworkElement body = Assert.IsType<FrameworkElement>(viewer.Content, exactMatch: false);
                FrameworkElement[] fields = [.. Descendants(body).OfType<FrameworkElement>()
                    .Where(element => element is TextBox or ComboBox or CheckBox && element.ActualWidth > 0 && element.ActualHeight > 0)];
                Assert.NotEmpty(fields);
                Assert.Contains(fields, field => System.Windows.Automation.AutomationProperties.GetName(field) == "Routine hotkey");
                Assert.Contains(fields, field => field is CheckBox { Content: "Show this routine in the tray menu" });
                foreach (FrameworkElement field in fields)
                {
                    Rect bounds = field.TransformToAncestor(body).TransformBounds(new Rect(field.RenderSize));
                    Assert.True(bounds.Left >= -0.5 && bounds.Right <= viewer.ViewportWidth + 0.5,
                        $"{field.GetType().Name} extends outside the editor viewport: {bounds}, viewport={viewer.ViewportWidth}");
                }
                var addTrigger = Assert.IsType<Button>(window.FindName("NewTriggerButton"));
                var confirmTrigger = Assert.IsType<Button>(window.FindName("ConfirmTriggerButton"));
                var triggerItems = Assert.IsType<ItemsControl>(window.FindName("AutomaticTriggersItems"));
                int configuredCount = triggerItems.Items.Count;
                addTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(configuredCount, triggerItems.Items.Count);
                Assert.True(viewModel.IsEditingAutomaticTrigger);
                Assert.False(viewModel.CanSaveRoutine);
                viewModel.SelectedTriggerMode = "Device change";
                confirmTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(configuredCount + 1, triggerItems.Items.Count);
                addTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                viewModel.SelectedTriggerMode = "Scheduled";
                viewModel.ScheduleTime = new TimeOnly(17, 45);
                confirmTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(configuredCount + 2, triggerItems.Items.Count);
                var firstEntry = triggerItems.Items[0];
                for (int iteration = 0; iteration < 10; iteration++)
                {
                    content.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    var editButtons = Descendants(triggerItems).OfType<Button>().Where(button => Equals(button.Content, "Edit")).ToArray();
                    editButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(viewModel.IsEditingAutomaticTrigger);
                    Assert.Equal(0, viewModel.SelectedTriggerIndex);
                    if (iteration == 0)
                    {
                        content.UpdateLayout();
                        var triggerActions = Assert.IsType<StackPanel>(window.FindName("TriggerActions"));
                        Assert.Equal(Visibility.Visible, triggerActions.Visibility);
                        Rect confirmBounds = confirmTrigger.TransformToAncestor(content).TransformBounds(new Rect(confirmTrigger.RenderSize));
                        Assert.True(confirmBounds.Top >= 0 && confirmBounds.Bottom <= content.ActualHeight);
                        Assert.False(viewer.IsAncestorOf(confirmTrigger));
                        foreach (FrameworkElement field in Descendants((FrameworkElement)window.FindName("TriggerEditorPanel")).OfType<FrameworkElement>()
                            .Where(element => element is TextBox or ComboBox or CheckBox && element.ActualWidth > 0 && element.ActualHeight > 0))
                        {
                            Rect bounds = field.TransformToAncestor(body).TransformBounds(new Rect(field.RenderSize));
                            Assert.True(bounds.Left >= -0.5 && bounds.Right <= viewer.ViewportWidth + 0.5,
                                $"Trigger field extends outside the viewport: {bounds}");
                        }
                    }
                    confirmTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(viewModel.IsEditingAutomaticTrigger);
                    editButtons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(triggerItems.Items.Count - 1, viewModel.SelectedTriggerIndex);
                    content.UpdateLayout();
                    var minutePicker = Assert.Single(Descendants(content).OfType<ComboBox>(),
                        box => System.Windows.Automation.AutomationProperties.GetName(box) == "Scheduled minute");
                    Assert.Equal(45, minutePicker.SelectedIndex);
                    confirmTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Assert.Same(firstEntry, triggerItems.Items[0]);
                Assert.Equal(new TimeOnly(17, 45), viewModel.ScheduleTime);
                if (trigger == RoutineTriggerKind.Network)
                {
                    var requiredNetwork = Assert.IsType<ComboBox>(window.FindName("RequiredNetworkBox"));
                    var triggerNetwork = Assert.IsType<ComboBox>(window.FindName("TriggerNetworkBox"));
                    viewModel.EditAutomaticTrigger(viewModel.TriggerEntries[0]);
                    requiredNetwork.SelectedItem = "Office";
                    triggerNetwork.SelectedItem = "Home";
                    Assert.Equal("Office", viewModel.RequiredNetworkName);
                    Assert.Equal("Home", viewModel.TriggerNetworkName);
                    networks = ["Guest"];
                    viewModel.RefreshNetworksAsync(forceRefresh: true).GetAwaiter().GetResult();
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Office", requiredNetwork.Text);
                    Assert.Equal("Home", triggerNetwork.Text);
                    requiredNetwork.Text = "Private VPN";
                    networks = ["Home", "Private VPN"];
                    viewModel.RefreshNetworksAsync(forceRefresh: true).GetAwaiter().GetResult();
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal("Private VPN", requiredNetwork.Text);
                    Assert.Equal("Private VPN", viewModel.BuildRoutine().Conditions.ConnectedNetwork);
                    foreach (string buttonName in NetworkRefreshButtonNames)
                    {
                        var refresh = Assert.IsType<Button>(window.FindName(buttonName));
                        refresh.ApplyTemplate(); refresh.UpdateLayout();
                        Assert.Same(viewModel.RefreshNetworksCommand, refresh.Command);
                        var arrow = Assert.Single(Descendants(refresh).OfType<System.Windows.Shapes.Path>(), path => path.Name == "RefreshArrow");
                        Assert.Equal(Visibility.Visible, arrow.Visibility);
                        Assert.False(arrow.Data.Bounds.IsEmpty);
                    }
                    Assert.True(viewModel.ConfirmTriggerEdit());
                    viewModel.SelectedTriggerIndex = viewModel.TriggerEntries.Count - 1;
                }
                content.Measure(new Size(2000, 1040));
                content.Arrange(new Rect(0, 0, 2000, 1040));
                content.UpdateLayout();
                var triggerSection = Assert.IsType<Border>(window.FindName("AutomaticTriggersSection"));
                Assert.True(triggerSection.ActualWidth <= 476);
                Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(window.FindName("RoutineActions")).Visibility);
                content.Measure(new Size(424, 442));
                content.Arrange(new Rect(0, 0, 424, 442));
                content.UpdateLayout();
                var applicationOption = Assert.IsType<ComboBoxItem>(window.FindName("ApplicationRoutingOption"));
                var communicationsExpander = Assert.IsType<Expander>(window.FindName("CommunicationsExpander"));
                Assert.Equal(theme == AppTheme.Light, applicationOption.IsEnabled);
                Assert.Equal(theme == AppTheme.Dark, communicationsExpander.IsEnabled);
                viewModel.SelectedCommunicationsOutputIndex = 0;
                viewModel.SelectedCommunicationsInputIndex = 0;
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(applicationOption.IsEnabled);
                viewModel.RoutingScopeIndex = 1;
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(communicationsExpander.IsEnabled);
                viewModel.RoutingScopeIndex = theme == AppTheme.Light ? 1 : 0;
                AudioRoutine saved = viewModel.BuildRoutine();
                var settings = new Settings { Routines = new RoutinesSettings { Items = [saved] } };
                AudioPilot.Services.Configuration.SettingsValidationService.Normalize(settings);
                Assert.Equal(saved.TriggerKind, settings.Routines.Items[0].TriggerKind);
                Assert.Contains(settings.Routines.Items[0].Triggers, entry => entry.Kind == RoutineTriggerKind.Scheduled && entry.Time == new TimeOnly(17, 45));
            }
            finally
            {
                window?.Close();
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
            {
                yield return descendant;
            }
        }
    }
}
