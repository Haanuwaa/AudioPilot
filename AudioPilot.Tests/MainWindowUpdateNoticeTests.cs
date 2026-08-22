using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Updates;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowUpdateNoticeTests
{
    private static readonly string[] MenuIconNames = ["Copy", "Cut", "Delete", "Duplicate", "Paste", "Redo", "SelectAll", "Undo", "Output", "Input", "Stop", "SetDefault"];
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void DeferredSettings_DisplaysKnownUpdate_AndClearsNoticeWhenDisabled(AppTheme theme)
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
                Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative),
            });
            foreach (string name in MenuIconNames)
            {
                resources[$"AppMenu{name}Icon"] = new DrawingImage();
            }

            using var workspace = new TestSettingsWorkspace(nameof(MainWindowUpdateNoticeTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            harness.SetCachedSettings(new Settings { Theme = theme });
            var version = new Version(typeof(MainWindow).Assembly.GetName().Version!.Major + 1, 0, 0);
            var release = new PublishedRelease(version, new Uri($"https://github.com/Haanuwaa/AudioPilot/releases/tag/v{version}"));
            TestPrivateAccess.SetField(harness.ViewModel.Updates, "_latest", release);
            harness.ViewModel.Updates.SetEnabled(true);
            var shell = TestPrivateAccess.GetField<AppShellService>(harness.ViewModel, "_shell");
            MainWindow? window = null;
            try
            {
                application.Resources = resources;
                window = new MainWindow(new MainWindowDependencies(harness.ViewModel, shell,
                    new MainWindowVisibilityCoordinator(Logger.Instance), static () => Task.CompletedTask));
                var tabs = (TabControl)window.FindName("DeviceTabControl");
                var settingsTab = (TabItem)tabs.Items[3];
                Assert.Null(settingsTab.Content);
                tabs.SelectedIndex = 3;
                Assert.Null(DeferredTabContentBehavior.GetTemplate(settingsTab));
                var content = Assert.IsType<StackPanel>(settingsTab.Content);
                content.Measure(new Size(400, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                var header = Assert.IsType<StackPanel>(Assert.IsType<Border>(content.Children[0]).Child);
                var repositoryText = Assert.IsType<TextBlock>(header.Children[0]);
                var updateText = Assert.IsType<TextBlock>(header.Children[1]);
                Hyperlink repositoryLink = Assert.Single(repositoryText.Inlines.OfType<Hyperlink>());
                Hyperlink updateLink = Assert.Single(updateText.Inlines.OfType<Hyperlink>());
                Assert.True(harness.ViewModel.Updates.IsUpdateAvailable);
                Assert.Equal(Visibility.Visible, updateText.Visibility);
                Assert.Contains(version.ToString(), new TextRange(updateLink.ContentStart, updateLink.ContentEnd).Text);
                Assert.Equal(release.Url, updateLink.NavigateUri);
                Assert.Equal("https://github.com/Haanuwaa/AudioPilot", repositoryLink.NavigateUri.AbsoluteUri);
                Assert.Equal(((SolidColorBrush)window.FindResource("DialogSuccessBrush")).Color, ((SolidColorBrush)repositoryLink.Foreground).Color);

                harness.ViewModel.Updates.SetEnabled(false);
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Collapsed, updateText.Visibility);
                Assert.Equal(SystemColors.HotTrackColor, ((SolidColorBrush)repositoryLink.Foreground).Color);
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
