using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowMixerRestoreTests
{
    private static readonly string[] MenuIconNames = ["Copy", "Cut", "Delete", "Duplicate", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault"];

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
            ResourceDictionary resources = new()
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new AudioPilot.Helpers.InverseBooleanToVisibilityConverter(),
                ["CycleDeviceListBoxItemStyle"] = new Style(typeof(ListBoxItem)),
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative),
            });
            foreach (string name in MenuIconNames)
            {
                resources[$"AppMenu{name}Icon"] = new DrawingImage();
            }

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
                window.Show();
                TestWindowFactory.CompleteInitialLayout(window);
                window.Hide();
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
}
