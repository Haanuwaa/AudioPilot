using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class ListManagerWindowTests
{
    [Fact]
    public void RoutineCheckDiscardsStaleResults_AndClosingCancelsPendingCheck()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var application = Application.Current;
            ResourceDictionary previous = application.Resources;
            Window? previousMain = application.MainWindow;
            application.Resources = new ResourceDictionary { Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative) };
            using var workspace = new TestSettingsWorkspace(nameof(ListManagerWindowTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var routine = new AudioRoutine { Name = "Manual", MasterVolumePercent = 50 };
            harness.ViewModel.Routines.Add(routine);
            TaskCompletionSource<RoutineReadinessResult> pending = new();
            CancellationToken checkToken = default;
            AudioRoutine? checkedDraft = null;
            var window = new ListManagerWindow(harness.ViewModel, harness.Audio, ListManagerMode.Routines,
                TestContext.Current.CancellationToken, checkRoutine: (draft, token) =>
                {
                    checkedDraft = draft;
                    checkToken = token;
                    return pending.Task;
                });
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                Drain();
                var rows = (ListBox)window.FindName("Rows");
                rows.SelectedIndex = 0;
                var check = (Button)window.FindName("CheckButton");
                var run = (Button)window.FindName("RunButton");
                var result = (TextBlock)window.FindName("ReadinessText");
                Assert.True(run.Command.CanExecute(run.CommandParameter));
                Assert.Same(routine, run.CommandParameter);
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotSame(routine, checkedDraft);
                Assert.Equal(50, checkedDraft!.MasterVolumePercent);
                Assert.False(check.IsEnabled);
                routine.MasterVolumePercent = 60;
                pending.SetResult(new(DateTimeOffset.UtcNow, true, "ready", []));
                Drain();
                Assert.Equal(Visibility.Collapsed, result.Visibility);
                Assert.True(check.IsEnabled);

                pending = new();
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                pending.SetResult(new(DateTimeOffset.UtcNow, true, "ready", []));
                Drain();
                Assert.Equal(Visibility.Visible, result.Visibility);
                Assert.Contains("Ready", result.Text, StringComparison.OrdinalIgnoreCase);
                harness.ViewModel.Localization.SetLanguage("es");
                Drain();
                Assert.Contains(harness.ViewModel.Localization["Routine_Ready"], result.Text, StringComparison.Ordinal);
                Assert.Equal(60, routine.MasterVolumePercent);
                routine.Enabled = false;
                Drain();
                Assert.Equal(Visibility.Collapsed, result.Visibility);
                Assert.False(run.Command.CanExecute(run.CommandParameter));
                routine.Enabled = true;
                Drain();

                var other = new AudioRoutine { Name = "Other manual", MasterVolumePercent = 30 };
                harness.ViewModel.Routines.Add(other);
                Drain();
                pending = new();
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                rows.SelectedItems.Add(rows.Items.OfType<ListManagerRow>().Single(row => ReferenceEquals(row.Item, other)));
                pending.SetResult(new(DateTimeOffset.UtcNow, true, "ready", []));
                Drain();
                Assert.Equal(2, rows.SelectedItems.Count);
                Assert.Equal(Visibility.Collapsed, result.Visibility);
                rows.SelectedItems.Clear();
                rows.SelectedIndex = 0;

                pending = new();
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Close();
                Assert.True(checkToken.IsCancellationRequested);
                pending.SetException(new InvalidOperationException("Late read failure"));
                Drain();
                Assert.False(window.IsVisible);
                harness.ViewModel.BeginRuntimeShutdown();
                Assert.False(run.Command.CanExecute(routine));

                static void Drain() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                window.Close();
                application.Resources = previous;
                application.MainWindow = previousMain;
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeviceManager_OpensAtRequestedEntry_RefreshesAnyRole_AndDisablesSaveDuringShutdown(bool output)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var application = Application.Current;
            ResourceDictionary previousResources = application.Resources;
            Window? previousMain = application.MainWindow;
            application.Resources = new ResourceDictionary
            {
                Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative),
            };
            using var workspace = new TestSettingsWorkspace(nameof(ListManagerWindowTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.ViewModel.Routines.Add(new AudioRoutine { Id = "routine", Name = "Routine", MasterVolumePercent = 50 });
            var cycle = output ? harness.ViewModel.OutputCycleDevices : harness.ViewModel.InputCycleDevices;
            for (int index = 0; index < 24; index++)
                cycle.Add(new CycleDevice { Id = $"device-{index}", Name = $"Device {index}" });
            cycle[23].StableId = "last-device";

            Task<ListManagerDeviceInventory> read = Task.FromResult(new ListManagerDeviceInventory([cycle[23]],
                new Dictionary<Role, string> { [Role.Communications] = "device-23" }, []));
            var window = new ListManagerWindow(harness.ViewModel, harness.Audio, output ? ListManagerMode.Output : ListManagerMode.Input,
                TestContext.Current.CancellationToken, initialDeviceId: "device-23", readDevices: _ => read);
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var rows = Assert.IsType<ListBox>(window.FindName("Rows"));
                Assert.Same(cycle[23], Assert.IsType<ListManagerRow>(rows.SelectedItem).Item);
                Assert.NotNull(rows.ItemContainerGenerator.ContainerFromIndex(23));
                Assert.True(((Button)window.FindName("TestButton")).IsEnabled);
                Assert.True(((Button)window.FindName("SetDefaultButton")).IsEnabled);

                var status = Assert.IsType<TextBlock>(window.FindName("DetailStatus"));
                var details = Assert.IsType<ItemsControl>(window.FindName("Details"));
                Assert.Equal("Connected", status.Text);
                Assert.Equal(new ListManagerDetail("Windows default roles", "Communications"), Assert.Single(details.Items.OfType<ListManagerDetail>()));

                read = Task.FromResult(new ListManagerDeviceInventory([new CycleDevice { Id = "reconnected", Name = "Device 23", StableId = "last-device" }], new Dictionary<Role, string>(), []));
                RefreshAndProject();
                Assert.Equal("reconnected", Assert.IsType<CycleDevice>(TestPrivateAccess.InvokeNonPublic(window, "ResolveActionDevice")).Id);

                var add = Assert.IsType<Button>(window.FindName("AddButton"));
                var view = Assert.IsType<ComboBox>(window.FindName("FilterBox"));
                var empty = Assert.IsType<TextBlock>(window.FindName("EmptyText"));
                Assert.Equal("_Add devices…", add.Content);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(3, view.SelectedIndex);
                Assert.Equal("_Add selected", add.Content);
                Assert.Equal("No devices available to add.", empty.Text);

                var removed = new CycleDevice { Id = "disconnected", Name = "Disconnected device" };
                read = Task.FromResult(new ListManagerDeviceInventory([removed], new Dictionary<Role, string>(), []));
                RefreshAndProject();
                rows.SelectedIndex = 0;
                Assert.Same(removed, Assert.IsType<ListManagerRow>(rows.SelectedItem).Item);
                Assert.True(add.IsEnabled);

                read = Task.FromResult(new ListManagerDeviceInventory([removed], new Dictionary<Role, string>(), [], IsComplete: false));
                RefreshAndProject();
                Assert.False(add.IsEnabled);
                Assert.Contains("Some device status is unavailable", Assert.IsType<TextBlock>(window.FindName("CountText")).Text, StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(window.FindName("RefreshStatusButton")).Visibility);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.DoesNotContain(cycle, device => device.Id == removed.Id);

                read = Task.FromException<ListManagerDeviceInventory>(new InvalidOperationException("Device inventory unavailable"));
                RequestRefresh();
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.DoesNotContain(cycle, device => device.Id == removed.Id);
                TestPrivateAccess.InvokeNonPublic(window, "RefreshDevices");
                Assert.Same(removed, Assert.IsType<ListManagerRow>(rows.SelectedItem).Item);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.DoesNotContain(cycle, device => device.Id == removed.Id);
                ProjectPendingRows();
                Assert.False(((Button)window.FindName("TestButton")).IsEnabled);
                Assert.False(((Button)window.FindName("SetDefaultButton")).IsEnabled);
                Assert.Contains("status is unavailable", empty.Text, StringComparison.Ordinal);

                var replacement = new CycleDevice { Id = "replacement", Name = "Replacement device", StableId = "replacement-stable" };
                read = Task.FromResult(new ListManagerDeviceInventory([replacement], new Dictionary<Role, string>(), []));
                RefreshAndProject();
                rows.SelectedIndex = 0;
                Assert.True(add.IsEnabled);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, view.SelectedIndex);
                var added = cycle[^1];
                Assert.Equal((replacement.Id, replacement.Name, replacement.StableId), (added.Id, added.Name, added.StableId));
                Assert.NotSame(replacement, added);

                rows.SelectedItems.Clear();
                rows.SelectedItems.Add(rows.Items[0]);
                rows.SelectedItems.Add(rows.Items[^1]);
                var remove = Assert.IsType<Button>(window.FindName("RemoveButton"));
                Assert.True(remove.IsEnabled);
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(23, cycle.Count);
                Assert.DoesNotContain(cycle, device => device.Id is "device-0" or "replacement");
                Assert.Equal(Enumerable.Range(1, cycle.Count), cycle.Select(device => device.DisplayOrder));

                view.SelectedIndex = 0;
                cycle.Clear();
                TestPrivateAccess.InvokeNonPublic(window, "ProjectRows");
                Assert.StartsWith("No devices in the switch order.", empty.Text, StringComparison.Ordinal);

                var onRoleChanged = TestPrivateAccess.GetField<Action<DataFlow, Role, string?>>(harness.Audio, "DefaultRoleChanged");
                onRoleChanged(output ? DataFlow.Capture : DataFlow.Render, Role.Communications, "other");
                Assert.Equal(0, TestPrivateAccess.GetField<int>(window, "_refreshRequested"));
                onRoleChanged(output ? DataFlow.Render : DataFlow.Capture, Role.Communications, "device-23");
                Assert.Equal(1, TestPrivateAccess.GetField<int>(window, "_refreshRequested"));

                var save = Assert.IsType<Button>(window.FindName("SaveButton"));
                Assert.True(save.IsEnabled);
                Assert.True(harness.ViewModel.SaveRoutinesCommand.CanExecute(null));
                harness.ViewModel.BeginRuntimeShutdown();
                Assert.False(save.Command.CanExecute(null));
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(save.IsEnabled);
                Assert.False(harness.ViewModel.SaveRoutinesCommand.CanExecute(null));

                void RequestRefresh() => NavigationCommands.Refresh.Execute(null, window);

                void ProjectPendingRows() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                void RefreshAndProject()
                {
                    RequestRefresh();
                    ProjectPendingRows();
                }
            }
            finally
            {
                window.Close();
                application.Resources = previousResources;
                application.MainWindow = previousMain;
            }
        });
    }
}
