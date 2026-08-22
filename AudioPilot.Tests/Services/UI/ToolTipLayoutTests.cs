using System.Windows;
using System.Windows.Controls;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class ToolTipLayoutTests
{
    [Theory]
    [InlineData("DarkTheme")]
    [InlineData("LightTheme")]
    public void LongStringHelp_WrapsWithinReadableWidth(string themeName)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{themeName}.xaml", UriKind.Relative) };
            var tooltip = new ToolTip
            {
                Content = string.Join(" ", Enumerable.Repeat("Help for this setting.", 30)),
                Style = (Style)theme[typeof(ToolTip)],
            };
            tooltip.Resources.MergedDictionaries.Add(theme);
            tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tooltip.Arrange(new Rect(tooltip.DesiredSize));
            Assert.InRange(tooltip.DesiredSize.Width, 1, 320);
            Assert.True(tooltip.DesiredSize.Height > 40, "Long help must wrap rather than clip on one line.");
        });
    }
}
