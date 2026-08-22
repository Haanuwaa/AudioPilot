using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Trait(TestCategories.Name, TestCategories.VisualWpf)]
[Collection("WpfApplicationIsolation")]
public sealed class InfoPopupServiceTests
{
    [VisualIntegrationTheory]
    [InlineData("collapse")]
    [InlineData("disable")]
    [InlineData("unload")]
    [InlineData("hide-window")]
    [InlineData("close-window")]
    public void TargetInvalidation_ClosesPopupWithoutPollingAndReleasesSubscriptions(string change)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var loggerScope = TestLoggerScope.CreateInMemory("info-popup-hide.log", LogLevel.Trace);
            var service = new InfoPopupService(loggerScope.Logger);
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 200, height: 120);
            var button = new Button { Content = "Hover", Width = 80, Height = 24 };
            var panel = new Grid();
            panel.Children.Add(button);
            window.Content = panel;
            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                service.ShowText(button, "info");
                var popup = TestPrivateAccess.GetField<Popup>(service, "_popup");
                Assert.True(popup.IsOpen);

                switch (change)
                {
                    case "collapse": button.Visibility = Visibility.Collapsed; break;
                    case "disable": button.IsEnabled = false; break;
                    case "unload": panel.Children.Remove(button); break;
                    case "hide-window": window.Hide(); break;
                    case "close-window": window.Close(); break;
                }
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                Assert.False(popup.IsOpen);
                Assert.Null(TestPrivateAccess.GetField<FrameworkElement?>(service, "_currentTarget"));
                Assert.Null(TestPrivateAccess.GetField<Window?>(service, "_currentWindow"));
                service.Hide(button);
                button.IsEnabled = false;
                button.Visibility = Visibility.Collapsed;
            }
            finally
            {
                service.Hide(button);
                window.Close();
            }

            string logText = loggerScope.DisposeAndReadLogText();
            Assert.Equal(1, CountOccurrences(logText, "info-popup-hide | reason="));
            Assert.DoesNotContain("reason=inactive-target-state", logText, StringComparison.Ordinal);
        });
    }

    [VisualIntegrationFact]
    public void Retargeting_DetachesPreviousTargetBeforeItsVisibilityChanges()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var service = new InfoPopupService(Logger.Instance);
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 200, height: 120);
            var first = new Button { Content = "First" };
            var second = new Button { Content = "Second" };
            var panel = new StackPanel();
            panel.Children.Add(first);
            panel.Children.Add(second);
            window.Content = panel;
            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                service.ShowText(first, "first");
                service.ShowText(second, "second");
                first.Visibility = Visibility.Collapsed;
                first.IsEnabled = false;
                service.Hide(first);
                var popup = TestPrivateAccess.GetField<Popup>(service, "_popup");
                Assert.True(popup.IsOpen);
                Assert.Same(second, TestPrivateAccess.GetField<FrameworkElement?>(service, "_currentTarget"));
                service.Hide(second);
                Assert.False(popup.IsOpen);
            }
            finally
            {
                service.Hide(second);
                window.Close();
            }
        });
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
