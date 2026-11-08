using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

[Collection("WpfApplicationIsolation")]
public sealed partial class WindowThemeHelperTests
{
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);

    [Fact]
    public void ThemeResources_ReuseUnchangedPaletteAndRestoreAfterHighContrast()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var resources = new ResourceDictionary();
            foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: false);
                ResourceDictionary normal = Assert.Single(resources.MergedDictionaries);
                Color normalBackground = Assert.IsType<SolidColorBrush>(normal["WindowBackgroundBrush"]).Color;
                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: false);
                Assert.Same(normal, Assert.Single(resources.MergedDictionaries));

                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: true);
                ResourceDictionary contrast = Assert.Single(resources.MergedDictionaries);
                Assert.NotSame(normal, contrast);
                AssertBrushColor(contrast, "WindowBackgroundBrush", SystemColors.WindowColor);
                AssertBrushColor(contrast, "AudioMeterLowBrush", SystemColors.WindowTextColor);
                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: true);
                Assert.Same(contrast, Assert.Single(resources.MergedDictionaries));

                WindowThemeHelper.ApplyThemeResources(resources, theme, highContrast: false);
                ResourceDictionary restored = Assert.Single(resources.MergedDictionaries);
                Assert.NotSame(contrast, restored);
                AssertBrushColor(restored, "WindowBackgroundBrush", normalBackground);
            }
        });
    }

    [Fact]
    public void ThemeResources_RemoveDuplicatesAndPreserveOtherResources()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var resources = new ResourceDictionary();
            var unrelated = new ResourceDictionary { ["Sentinel"] = "preserved" };
            resources.MergedDictionaries.Add(unrelated);
            WindowThemeHelper.ApplyThemeResources(resources, AppTheme.Dark, highContrast: false);
            resources.MergedDictionaries.Add(resources.MergedDictionaries[0]);

            WindowThemeHelper.ApplyThemeResources(resources, AppTheme.Light, highContrast: false);

            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.Same(unrelated, resources.MergedDictionaries[1]);
            Assert.Equal("preserved", resources["Sentinel"]);
            Assert.EndsWith("LightTheme.xaml", resources.MergedDictionaries[0].Source.OriginalString);
        });
    }

    [Fact]
    public void ApplicationTheme_UpdatesSecondaryWindowChromeAndBackground()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            Window? originalMainWindow = application.MainWindow;
            var main = new Window();
            var secondary = new Window();
            var overlay = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent };
            try
            {
                application.Resources = [];
                application.MainWindow = main;
                main.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
                secondary.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
                nint mainHandle = new WindowInteropHelper(main).EnsureHandle();
                secondary.Owner = main;
                nint secondaryHandle = new WindowInteropHelper(secondary).EnsureHandle();
                nint overlayHandle = new WindowInteropHelper(overlay).EnsureHandle();
                var overlayTarget = Assert.IsType<HwndTarget>(HwndSource.FromHwnd(overlayHandle).CompositionTarget);
                Color overlayBackground = overlayTarget.BackgroundColor;
                foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Dark })
                {
                    WindowThemeResolver.ApplyApplicationTheme(theme);
                    Assert.Equal(overlayBackground, overlayTarget.BackgroundColor);
                    foreach (Window window in new[] { main, secondary })
                    {
                        nint handle = window == main ? mainHandle : secondaryHandle;
                        var target = Assert.IsType<HwndTarget>(HwndSource.FromHwnd(handle).CompositionTarget);
                        Color background = Assert.IsType<SolidColorBrush>(window.Background).Color;
                        Assert.Equal(Color.FromRgb(background.R, background.G, background.B), target.BackgroundColor);
                        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                        {
                            Assert.Equal(0, DwmGetWindowAttribute(handle, 20, out int dark, sizeof(int)));
                            Assert.Equal(theme == AppTheme.Dark && !SystemParameters.HighContrast ? 1 : 0, dark);
                        }
                    }
                }
            }
            finally
            {
                overlay.Close();
                secondary.Close();
                main.Close();
                application.MainWindow = originalMainWindow;
                application.Resources = originalResources;
            }
        });
    }

    [Fact]
    public void ApplyWindowTheme_AfterDispatcherShutdown_DoesNotDispatch()
    {
        Window? window = null;
        TestExecutionGuards.RunSta(() =>
        {
            window = new Window();
            window.Close();
        });
        Assert.NotNull(window);
        Assert.True(window.Dispatcher.HasShutdownFinished);
        WindowThemeResolver.ApplyWindowTheme(window, AppTheme.Dark);
    }

    [Fact]
    public void ApplyHighContrastPalette_UsesCurrentSystemColorPairs()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var frozenBackground = new SolidColorBrush(Colors.Red);
            frozenBackground.Freeze();
            var resources = new ResourceDictionary
            {
                ["WindowBackgroundBrush"] = frozenBackground,
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

            WindowThemeHelper.ApplyHighContrastPalette(resources);

            Assert.NotSame(frozenBackground, resources["WindowBackgroundBrush"]);
            Assert.Equal(Colors.Red, frozenBackground.Color);
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
