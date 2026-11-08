using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class ToolTipLayoutTests
{
    [Theory]
    [InlineData("DarkTheme", false)]
    [InlineData("LightTheme", false)]
    [InlineData("DarkTheme", true)]
    [InlineData("LightTheme", true)]
    public void LongHelp_WrapsAndUsesReadableRoundedTheme(string themeName, bool textBlockContent)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{themeName}.xaml", UriKind.Relative) };
            string help = string.Join(" ", Enumerable.Repeat("Help for this setting.", 30));
            var tooltip = new ToolTip
            {
                Content = textBlockContent ? new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap } : help,
                Style = (Style)theme[typeof(ToolTip)],
            };
            tooltip.Resources.MergedDictionaries.Add(theme);
            tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tooltip.Arrange(new Rect(tooltip.DesiredSize));
            Assert.InRange(tooltip.DesiredSize.Width, 1, 320);
            Assert.True(tooltip.DesiredSize.Height > 40, "Long help must wrap rather than clip on one line.");
            Assert.Equal("Segoe UI", tooltip.FontFamily.Source);
            Assert.Equal(FontWeights.Normal, tooltip.FontWeight);
            Assert.Same(theme["TextBrush"], tooltip.Foreground);
            Assert.Same(theme["ControlBackgroundBrush"], tooltip.Background);
            var border = Assert.IsType<Border>(VisualTreeHelper.GetChild(tooltip, 0));
            Assert.True(border.CornerRadius.TopLeft > 0);
            Assert.Equal(border.CornerRadius.TopLeft, border.CornerRadius.BottomRight);
        });
    }
}
