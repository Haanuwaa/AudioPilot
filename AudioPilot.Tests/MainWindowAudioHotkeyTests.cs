using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowAudioHotkeyTests
{
    private static readonly string[] MenuIconNames = ["Copy", "Cut", "Delete", "Duplicate", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault"];

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void DeferredSettings_BindsAudioHotkeysAndFitsExpandedControls(AppTheme theme)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var application = Application.Current;
            var originalResources = application.Resources;
            var originalWindow = application.MainWindow;
            var resources = new ResourceDictionary
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new AudioPilot.Helpers.InverseBooleanToVisibilityConverter(),
                ["CycleDeviceListBoxItemStyle"] = new Style(typeof(ListBoxItem)),
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative),
            });
            foreach (string icon in MenuIconNames)
                resources[$"AppMenu{icon}Icon"] = new DrawingImage();
            using var workspace = new TestSettingsWorkspace(nameof(MainWindowAudioHotkeyTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.SetCachedSettings(new Settings { Theme = theme });
            var vm = harness.ViewModel;
            vm.SettingsForegroundVolumeControlsExpanded = true;
            vm.SettingsMicrophoneHoldControlsExpanded = true;
            vm.SettingsForegroundVolumeUpHotkeyDraftCapture.LoadFromString("Ctrl+F8");
            vm.SettingsPushToTalkHotkeyDraftCapture.LoadFromString("Mouse4");
            vm.SettingsForegroundVolumeStepPercentDraft = "7";
            MainWindow? window = null;
            try
            {
                application.Resources = resources;
                var shell = TestPrivateAccess.GetField<AppShellService>(vm, "_shell");
                window = new MainWindow(new MainWindowDependencies(vm, shell,
                    new MainWindowVisibilityCoordinator(Logger.Instance), static () => Task.CompletedTask));
                var tabs = (TabControl)window.FindName("DeviceTabControl");
                tabs.SelectedIndex = 3;
                var content = Assert.IsType<StackPanel>(((TabItem)tabs.Items[3]).Content);
                content.Measure(new Size(380, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, 380, content.DesiredSize.Height));
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                var fields = Descendants(content).OfType<TextBox>()
                    .Where(field => AutomationProperties.GetName(field) is "Foreground app volume up hotkey" or "Foreground app volume down hotkey" or "Foreground app mute hotkey" or "Foreground app volume step percent" or "Push-to-talk hotkey" or "Hold-to-mute hotkey")
                    .ToDictionary(AutomationProperties.GetName);
                Assert.Equal(6, fields.Count);
                Assert.Equal(vm.SettingsForegroundVolumeUpHotkeyDraftCapture.DisplayText, fields["Foreground app volume up hotkey"].Text);
                Assert.Equal(vm.SettingsPushToTalkHotkeyDraftCapture.DisplayText, fields["Push-to-talk hotkey"].Text);
                Assert.Equal("7", fields["Foreground app volume step percent"].Text);
                var pushToTalk = Descendants(content).OfType<CheckBox>()
                    .Single(checkBox => AutomationProperties.GetName(checkBox) == "Enable push-to-talk");
                Assert.False(pushToTalk.IsChecked);
                pushToTalk.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                Assert.True(vm.SettingsPushToTalkEnabledDraft);
                var foreground = Descendants(content).OfType<Expander>()
                    .Single(expander => expander.Header is TextBlock { Text: "Foreground App" });
                Assert.Equal(8, foreground.Margin.Bottom);
                foreach (var field in fields.Values)
                {
                    Assert.True(field.ActualWidth > 0);
                    var origin = field.TranslatePoint(new Point(), content);
                    Assert.True(origin.X >= 0 && origin.X + field.ActualWidth <= content.ActualWidth + 1);
                }
            }
            finally
            {
                if (window != null) { window.AllowCloseForRuntimeShutdown(); window.Close(); }
                application.Resources = originalResources;
                application.MainWindow = originalWindow;
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
