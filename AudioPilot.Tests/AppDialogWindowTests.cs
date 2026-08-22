using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioPilot.Services.UI;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed class AppDialogWindowTests
{
    [Theory]
    [InlineData("/AudioPilot;component/Themes/LightTheme.xaml")]
    [InlineData("/AudioPilot;component/Themes/DarkTheme.xaml")]
    public void Window_ConstructsWithThemedSelectableContentAndAccessibleActions(string themeUri)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current
                ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Relative) };
            application.Resources.MergedDictionaries.Add(theme);
            try
            {
                var request = new AppDialogRequest(
                    new string('x', 2_000),
                    "Reset settings",
                    AppDialogKind.Warning,
                    [
                        new AppDialogAction("_Reset", AppDialogResult.Confirmed, AppDialogActionStyle.Destructive, isDefault: true),
                        new AppDialogAction("_Cancel", AppDialogResult.Declined, isCancel: true),
                    ],
                    allowCopy: true,
                    automationHelpText: "Review the warning before resetting settings.");

                var window = new AppDialogWindow(request);

                Assert.Equal(440, window.Width);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.True(window.MessageText.IsReadOnly);
                Assert.True(window.MessageText.IsReadOnlyCaretVisible);
                Assert.Equal(ScrollBarVisibility.Auto, window.MessageText.VerticalScrollBarVisibility);
                Assert.Equal(Visibility.Visible, window.CopyButton.Visibility);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.MessageText));
                Assert.Equal(2, window.ActionsPanel.Children.Count);
                Button defaultButton = Assert.IsType<Button>(window.ActionsPanel.Children[0]);
                Assert.True(defaultButton.IsDefault);
                Assert.Equal("Reset", AutomationProperties.GetName(defaultButton));

                window.Close();
                Assert.Equal(AppDialogResult.Declined, window.Result);
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(theme);
            }
        });
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ThreeActionStartupDialog_RemainsCompactAtScaledDpi(double dpiScale)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current
                ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = new ResourceDictionary
            {
                Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative),
            };
            application.Resources.MergedDictionaries.Add(theme);
            try
            {
                var request = new AppDialogRequest(
                    "AudioPilot appears to be running but is not responding.",
                    "Startup error",
                    AppDialogKind.Warning,
                    [
                        new AppDialogAction("_Retry", AppDialogResult.Retry, AppDialogActionStyle.Primary, isDefault: true),
                        new AppDialogAction("_Terminate and continue", AppDialogResult.TerminateExisting, AppDialogActionStyle.Destructive),
                        new AppDialogAction("E_xit", AppDialogResult.Cancelled, isCancel: true),
                    ]);
                var window = new AppDialogWindow(request);

                window.Measure(new Size(window.Width, window.MaxHeight));
                window.Arrange(new Rect(0, 0, window.Width, Math.Min(window.DesiredSize.Height, window.MaxHeight)));

                Assert.Equal(3, window.ActionsPanel.Children.Count);
                Assert.True(window.ActionsPanel.DesiredSize.Width <= window.Width - 36);
                Assert.Equal(440 * dpiScale, window.Width * dpiScale, precision: 3);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.True(window.ActualHeight <= window.MaxHeight);
                window.Close();
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(theme);
            }
        });
    }
}
