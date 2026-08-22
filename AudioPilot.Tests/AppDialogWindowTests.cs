using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class AppDialogWindowTests
{
    [VisualIntegrationFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    public void NativeDialog_WithLongMessage_KeepsFocusedActionsVisibleAndClosesCleanly()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var request = AppDialogRequest.Acknowledge(
                string.Join(Environment.NewLine, Enumerable.Repeat("Detailed error information", 100)),
                "Dialog layout test", AppDialogKind.Error);
            var window = new AppDialogWindow(request) { MaxHeight = 300 };
            bool rendered = false;
            window.FirstPresented += () =>
            {
                rendered = true;
                try
                {
                    Rect actions = window.ActionsPanel.TransformToAncestor(window).TransformBounds(new Rect(window.ActionsPanel.RenderSize));
                    Assert.InRange(actions.Bottom, 1, window.ActualHeight);
                    Assert.True(window.MessageText.ViewportHeight < window.MessageText.ExtentHeight);
                    Assert.True(Assert.IsType<Button>(window.ActionsPanel.Children[0]).IsKeyboardFocused);
                }
                finally
                {
                    window.Complete(AppDialogResult.Cancelled);
                }
            };

            try
            {
                AudioPilot.Helpers.DialogWindowHelper.ShowOwnedDialog(window, owner: null);
                Assert.True(rendered);
                Assert.Equal(AppDialogResult.Cancelled, window.Result);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CancelledPresentation_DoesNotCreateOrShowAWindow()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current
                ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int windowCount = application.Windows.Count;
            var presenter = new AppDialogWindowPresenter();
            var request = AppDialogRequest.Acknowledge("message", "caption", AppDialogKind.Information);

            Task<AppDialogResult> result = presenter.PresentAsync(request, new CancellationToken(canceled: true));

            Assert.True(result.IsCompletedSuccessfully);
            Assert.Equal(AppDialogResult.Cancelled, result.GetAwaiter().GetResult());
            Assert.Equal(windowCount, application.Windows.Count);
        });
    }

    [Fact]
    public void LongMessage_InShortWorkArea_KeepsActionsWithinContentBounds()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var request = AppDialogRequest.Acknowledge(
                string.Join(Environment.NewLine, Enumerable.Repeat("Detailed error information", 100)),
                "An operation failed", AppDialogKind.Error);
            var window = new AppDialogWindow(request);
            try
            {
                var content = Assert.IsType<Grid>(window.Content);
                content.Measure(new Size(440, 260));
                content.Arrange(new Rect(0, 0, 440, 260));
                Rect actions = window.ActionsPanel.TransformToAncestor(content).TransformBounds(
                    new Rect(window.ActionsPanel.RenderSize));

                Assert.True(actions.Bottom <= 224, $"Actions bottom {actions.Bottom} exceeds the available content height 224.");
                Assert.True(window.MessageText.ActualHeight > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

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
    [InlineData(260)]
    [InlineData(520)]
    public void ThreeActionStartupDialog_KeepsButtonsWithinAvailableHeight(double availableHeight)
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

                var content = Assert.IsType<Grid>(window.Content);
                content.Measure(new Size(window.Width, availableHeight));
                content.Arrange(new Rect(0, 0, window.Width, availableHeight));

                Assert.Equal(3, window.ActionsPanel.Children.Count);
                Assert.True(window.ActionsPanel.DesiredSize.Width <= window.Width - 36);
                Rect actions = window.ActionsPanel.TransformToAncestor(content).TransformBounds(new Rect(window.ActionsPanel.RenderSize));
                Assert.InRange(actions.Bottom, 1, availableHeight - 36);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                window.Close();
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(theme);
            }
        });
    }
}
