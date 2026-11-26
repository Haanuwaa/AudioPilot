using System.Windows;
using System.Windows.Controls;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed partial class DiagnosticsWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderLinksUseCurrentLocationsAndCombineSharedStorage(bool shared)
    {
        await SharedStaDispatcherHost.RunAsync(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = app.Resources;
            app.Resources = CreateResources(AppTheme.Dark);
            using var directory = new TestScopedDirectory(nameof(FolderLinksUseCurrentLocationsAndCombineSharedStorage));
            string settings = Path.Combine(directory.Root, "settings");
            string logs = shared ? settings + Path.DirectorySeparatorChar : Path.Combine(directory.Root, "logs");
            int captures = 0;
            var opened = new List<string>();
            var window = new DiagnosticsWindow(() =>
            {
                captures++;
                return new(new(DateTimeOffset.UtcNow, "gui", "inactive", null, null, null, 0), "No history");
            }, _ => Task.FromResult(new DiagnosticBundleSaveResult(true)), TestContext.Current.CancellationToken,
                folderActions: new(() => (settings, logs), path => opened.Add(path)));
            try
            {
                var settingsButton = (Button)window.FindName("SettingsFolderButton");
                var logsButton = (Button)window.FindName("LogsFolderButton");
                Assert.Equal(shared ? "Open data folder" : "Open settings folder", settingsButton.Content);
                Assert.Equal(shared ? Visibility.Collapsed : Visibility.Visible, logsButton.Visibility);
                settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(settings, Assert.Single(opened));
                settings = Path.Combine(directory.Root, "new-active-settings");
                settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(settings, opened[1]);
                logsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(logs, opened[2]);
                Assert.Equal(1, captures);
            }
            finally { window.Close(); app.Resources = previous; }
            return Task.CompletedTask;
        });
    }
}
