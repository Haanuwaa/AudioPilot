using System.Windows;
using System.Windows.Media;
using AudioPilot.Platform;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

[Collection("WpfApplicationIsolation")]
public sealed class WindowThemeHelperTests
{
    [Fact]
    public void ApplyHighContrastPalette_UsesCurrentSystemColorPairs()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var resources = new ResourceDictionary
            {
                ["WindowBackgroundBrush"] = new SolidColorBrush(Colors.Red),
                ["ControlBackgroundBrush"] = new SolidColorBrush(Colors.Red),
                ["AccentBrush"] = new SolidColorBrush(Colors.Red),
                ["AccentSelectionHoverBrush"] = new SolidColorBrush(Colors.Red),
                ["AccentTextBrush"] = new SolidColorBrush(Colors.Red),
                ["CheckMarkBrush"] = new SolidColorBrush(Colors.Red),
                ["TextBrush"] = new SolidColorBrush(Colors.Red),
                ["PlaceholderTextBrush"] = new SolidColorBrush(Colors.Red),
                ["BorderBrush"] = new SolidColorBrush(Colors.Red),
                ["TrayMenuHoverBackgroundBrush"] = new SolidColorBrush(Colors.Red),
                ["TrayMenuHoverForegroundBrush"] = new SolidColorBrush(Colors.Red),
                ["KeyboardFocusOuterBrush"] = new SolidColorBrush(Colors.Red),
            };

            WindowThemeHelper.ApplyHighContrastPaletteForTests(resources);

            AssertBrushColor(resources, "WindowBackgroundBrush", SystemColors.WindowColor);
            AssertBrushColor(resources, "ControlBackgroundBrush", SystemColors.WindowColor);
            AssertBrushColor(resources, "AccentBrush", SystemColors.HighlightColor);
            AssertBrushColor(resources, "AccentSelectionHoverBrush", SystemColors.HighlightColor);
            AssertBrushColor(resources, "AccentTextBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "CheckMarkBrush", SystemColors.HighlightTextColor);
            AssertBrushColor(resources, "TextBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "PlaceholderTextBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "BorderBrush", SystemColors.WindowTextColor);
            AssertBrushColor(resources, "TrayMenuHoverBackgroundBrush", SystemColors.HighlightColor);
            AssertBrushColor(resources, "TrayMenuHoverForegroundBrush", SystemColors.HighlightTextColor);
            AssertBrushColor(resources, "KeyboardFocusOuterBrush", SystemColors.HighlightColor);
        });
    }

    private static void AssertBrushColor(ResourceDictionary resources, string key, Color expected)
    {
        var brush = Assert.IsType<SolidColorBrush>(resources[key]);
        Assert.Equal(expected, brush.Color);
        Assert.Equal(1d, brush.Opacity);
    }
}
