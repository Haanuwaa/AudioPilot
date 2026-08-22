using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowMixerRestoreTests
{
    private static readonly string[] MenuIconNames = ["Copy", "Cut", "Delete", "Duplicate", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault", "Settings"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RestoredStateNotification_BeforeShellCompletes_DoesNotClearVisibleMixerRows(int restoredTab)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            Window? originalMainWindow = application.MainWindow;
            ResourceDictionary resources = CreateResources();

            using var workspace = new TestSettingsWorkspace(nameof(MainWindowMixerRestoreTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var output = new MixerViewModel(harness.Audio, Dispatcher.CurrentDispatcher, AudioMixerMode.Output);
            var input = new MixerViewModel(harness.Audio, Dispatcher.CurrentDispatcher, AudioMixerMode.Input);
            TestPrivateAccess.SetField(harness.ViewModel, "_mixer", output);
            TestPrivateAccess.SetField(harness.ViewModel, "_inputMixer", input);
            harness.ViewModel.MarkStartupVisibilityResolved();
            harness.ViewModel.SelectedSettingsTabIndex = restoredTab;
            MixerViewModel activeMixer = restoredTab == 1 ? input : output;
            var shell = TestPrivateAccess.GetField<AppShellService>(harness.ViewModel, "_shell");
            MainWindow? window = null;
            try
            {
                application.Resources = resources;
                window = new MainWindow(new MainWindowDependencies(harness.ViewModel, shell,
                    new MainWindowVisibilityCoordinator(Logger.Instance), static () => Task.CompletedTask))
                {
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Opacity = 0,
                };
                TestWindowFactory.ShowWindowForLayoutTest(window);
                TestWindowFactory.CompleteInitialLayout(window);
                var vm = harness.ViewModel;
                var cached = new Settings();
                cached.Miscellaneous.AutoSaveEnabled = restoredTab >= 2;
                harness.SetCachedSettings(cached);
                var outputDevice = new CycleDevice { Id = "test-output", Name = "Desk" };
                var inputDevice = new CycleDevice { Id = "test-input", Name = "Microphone" };
                var routine = new AudioRoutine { Id = "test-routine", Name = "Draft routine", Enabled = false, MasterVolumePercent = 42 };
                vm.OutputCycleDevices.Add(outputDevice);
                vm.InputCycleDevices.Add(inputDevice);
                vm.Routines.Add(routine);
                vm.SelectedOutputCycleDevices.Add(outputDevice);
                vm.SelectedInputCycleDevices.Add(inputDevice);
                vm.SelectedRoutines.Add(routine);
                vm.SelectedOutputCycleIndex = 0;
                vm.SelectedInputCycleIndex = 0;
                vm.SelectedRoutineIndex = 0;
                vm.SettingsForegroundVolumeStepPercentDraft = "7";
                long dirtyRevision = TestPrivateAccess.GetField<long>(vm, "_autoSaveDirtyRevision");
                window.Opacity = 1d;
                double? opacityWhenHidden = null;
                window.IsVisibleChanged += (_, e) =>
                {
                    if (e.NewValue is false) opacityWhenHidden = window.Opacity;
                };
                typeof(AppMainWindowManager).GetMethod("HideWindowCore", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [window]);
                Assert.Equal(1d, opacityWhenHidden);
                Assert.False(window.IsVisible);
                Assert.Equal(0d, window.Opacity);
                Assert.False(window.ShowInTaskbar);
                Assert.Empty(vm.SelectedOutputCycleDevices);
                Assert.Empty(vm.SelectedInputCycleDevices);
                Assert.Empty(vm.SelectedRoutines);
                Assert.Equal(-1, vm.SelectedOutputCycleIndex);
                Assert.Equal(-1, vm.SelectedInputCycleIndex);
                Assert.Equal(-1, vm.SelectedRoutineIndex);
                Assert.Contains(outputDevice, vm.OutputCycleDevices);
                Assert.Contains(inputDevice, vm.InputCycleDevices);
                Assert.Contains(routine, vm.Routines);
                Assert.Equal("7", vm.SettingsForegroundVolumeStepPercentDraft);
                Assert.Equal(dirtyRevision, TestPrivateAccess.GetField<long>(vm, "_autoSaveDirtyRevision"));
                window.Show();
                TestWindowFactory.CompleteInitialLayout(window);
                var row = new AudioSessionItem("Restore regression", 42f, isMaster: true, isMic: false);
                activeMixer.Sessions.Add(row);
                Assert.True(window.IsVisible);
                Assert.False(shell.IsWindowVisible);
                Assert.Contains(row, ((ListBox)window.FindName("VolumeMixer")).Items.Cast<AudioSessionItem>());

                typeof(MainWindow).GetMethod("OnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [EventArgs.Empty]);

                Assert.Contains(row, activeMixer.Sessions);
                Assert.True(TestPrivateAccess.GetField<bool>(harness.ViewModel, "_isWindowVisible"));
                harness.ViewModel.SelectedSettingsTabIndex = restoredTab == 1 ? 1 : 0;
                TestWindowFactory.CompleteInitialLayout(window);
                Assert.Contains(row, ((ListBox)window.FindName("VolumeMixer")).Items.Cast<AudioSessionItem>());
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

    [Fact]
    public void PendingMixerScroll_TracksTheTabAndOption_WhileExplicitRequestsIgnoreTheAutomaticOption()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary previousResources = application.Resources;
            Window? previousWindow = application.MainWindow;
            using var workspace = new TestSettingsWorkspace(nameof(MainWindowMixerRestoreTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var settings = new Settings();
            settings.Miscellaneous.AutoScrollToMixerOnRestore = false;
            harness.SetCachedSettings(settings);
            var shell = TestPrivateAccess.GetField<AppShellService>(harness.ViewModel, "_shell");
            TestPrivateAccess.GetField<TestAppMainWindowManager>(shell, "_windowManager").IsVisibleValue = true;
            MainWindow? window = null;
            try
            {
                application.Resources = CreateResources();
                window = new MainWindow(new MainWindowDependencies(harness.ViewModel, shell,
                    new MainWindowVisibilityCoordinator(Logger.Instance), static () => Task.CompletedTask));
                MethodInfo canContinue = typeof(MainWindow).GetMethod("CanContinueRestoreMixerScroll", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.False((bool)canContinue.Invoke(window, [0, true])!);
                Assert.True((bool)canContinue.Invoke(window, [0, false])!);

                var automatic = new CancellationTokenSource();
                CancellationToken automaticToken = automatic.Token;
                TestPrivateAccess.SetField(window, "_restoreMixerScrollCts", automatic);
                harness.ViewModel.SettingsAutoScrollToMixerOnRestoreDraft = true;
                Assert.True(automaticToken.IsCancellationRequested);

                var explicitRequest = new CancellationTokenSource();
                CancellationToken explicitToken = explicitRequest.Token;
                TestPrivateAccess.SetField(window, "_restoreMixerScrollCts", explicitRequest);
                TestPrivateAccess.SetField(window, "_restoreMixerScrollRequiresAutoScroll", false);
                harness.ViewModel.SettingsAutoScrollToMixerOnRestoreDraft = false;
                Assert.False(explicitToken.IsCancellationRequested);
                harness.ViewModel.SelectedSettingsTabIndex = 1;
                Assert.True(explicitToken.IsCancellationRequested);
                Assert.False((bool)canContinue.Invoke(window, [0, false])!);

                var header = (Label)window.FindName("VolumeMixerHeader");
                var scroll = (ScrollViewer)window.FindName("MainContentScrollViewer");
                Assert.IsType<Visual>(scroll.Content, exactMatch: false);
                header.Visibility = Visibility.Collapsed;
                MethodInfo tryScroll = typeof(MainWindow).GetMethod("TryScrollToVolumeMixerSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.False((bool)tryScroll.Invoke(window, [null, true])!);
            }
            finally
            {
                if (window != null)
                {
                    window.DataContext = null;
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                    window.AllowCloseForRuntimeShutdown();
                    window.Close();
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                }
                application.Resources = previousResources;
                application.MainWindow = previousWindow;
            }
        });
    }

    private static ResourceDictionary CreateResources()
    {
        ResourceDictionary resources = new()
        {
            ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
            ["InverseBoolToVisibilityConverter"] = new AudioPilot.Helpers.InverseBooleanToVisibilityConverter(),
            ["CycleDeviceListBoxItemStyle"] = new Style(typeof(ListBoxItem)),
        };
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative) });
        foreach (string name in MenuIconNames) resources[$"AppMenu{name}Icon"] = new DrawingImage();
        return resources;
    }
}
