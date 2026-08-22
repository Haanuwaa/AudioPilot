using System.Windows;
using System.Windows.Controls;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("WpfApplicationIsolation")]
public sealed class InfoPopupServiceTests
{
    [Fact]
    public void HideReason_IsLoggedOnce_WhenTargetBecomesInvisible()
    {
        TestExecutionGuards.RunSta(() =>
        {
            using var loggerScope = TestLoggerScope.CreateInMemory("info-popup-hide.log", LogLevel.Trace);
            var service = new InfoPopupService(loggerScope.Logger);
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 200, height: 120);
            window.Content = new Grid();

            Button button = new()
            {
                Content = "Hover",
                Width = 80,
                Height = 24,
            };

            ((Grid)window.Content).Children.Add(button);

            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                service.ShowText(button, "info");
                button.Visibility = Visibility.Collapsed;
                Assert.False(service.IsActiveFor(button));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }

            string logText = loggerScope.DisposeAndReadLogText();
            Assert.Contains("info-popup-hide | reason=", logText, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(logText, "info-popup-hide | reason="));
        });
    }

    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void ShowText_HidesPopup_WhenTargetBecomesInvisible()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(ShowText_HidesPopup_WhenTargetBecomesInvisible)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var loggerScope = TestLoggerScope.CreateInMemory("info-popup-visual.log", LogLevel.Trace);
            var service = new InfoPopupService(loggerScope.Logger);
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 200, height: 120);
            window.Content = new Grid();

            Button button = new()
            {
                Content = "Hover",
                Width = 80,
                Height = 24,
            };

            ((Grid)window.Content).Children.Add(button);

            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                service.ShowText(button, "info");

                Assert.True(
                    service.IsActiveFor(button),
                    $"Expected the popup target to be active after showing it. buttonVisible={button.IsVisible}, buttonEnabled={button.IsEnabled}, windowVisible={window.IsVisible}, windowState={window.WindowState}.");

                button.Visibility = Visibility.Collapsed;

                Assert.False(service.IsActiveFor(button));
            }
            finally
            {
                service.Hide(button);
                if (window.IsVisible)
                {
                    window.Close();
                }
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
