using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AudioPilot.Models;

namespace AudioPilot.Tests.Helpers;

[Collection("WpfApplicationIsolation")]
public sealed partial class TestWindowFactoryTests
{
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);

    [VisualIntegrationTheory]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void VisibleFixture_UsesAppPaletteControlsAndNativeTitleBarWithoutChangingApplicationTheme(AppTheme theme)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ResourceDictionary applicationResources = Application.Current.Resources;
            ResourceDictionary[] dictionaries = [.. applicationResources.MergedDictionaries];
            Window window = TestWindowFactory.CreateOffscreenWindow(width: 320, height: 160, theme: theme);
            var button = new Button { Content = "Themed test control", Margin = new Thickness(16) };
            window.Content = button;
            try
            {
                TestWindowFactory.ShowWindowForTest(window);
                Assert.Same(window.FindResource("WindowBackgroundBrush"), window.Background);
                Assert.Same(window.FindResource("TextBrush"), window.Foreground);
                Assert.Equal("Segoe UI", window.FontFamily.Source);
                Assert.Same(window.FindResource(typeof(Button)), button.Style);
                Assert.Same(applicationResources, Application.Current.Resources);
                Assert.Equal(dictionaries, applicationResources.MergedDictionaries);
                Assert.False(window.AllowsTransparency);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.IsType<SolidColorBrush>(window.Background);
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 20, out int dark, sizeof(int)));
                    Assert.Equal(theme == AppTheme.Dark && !SystemParameters.HighContrast ? 1 : 0, dark);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CreateOffscreenWindow_DefaultMode_IsHiddenAndSilent()
    {
        string? original = Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS");
        Environment.SetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS", null);

        try
        {
            TestExecutionGuards.RunSta(() =>
            {
                Window window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: true, width: 320, height: 240);

                Assert.Equal(1, window.Width);
                Assert.Equal(1, window.Height);
                Assert.False(window.ShowInTaskbar);
                Assert.False(window.ShowActivated);
                Assert.Equal(0, window.Opacity);
                Assert.True(window.Left < 0);
                Assert.True(window.Top < 0);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.True(window.AllowsTransparency);
                Assert.Equal(Visibility.Hidden, window.Visibility);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS", original);
        }
    }

    [Fact]
    public void CreateOffscreenWindow_DebugMode_AllowsVisiblePlacement()
    {
        string? original = Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS");
        Environment.SetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS", "1");

        try
        {
            TestExecutionGuards.RunSta(() =>
            {
                Window window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: true, width: 320, height: 240);

                Assert.True(window.ShowInTaskbar);
                Assert.False(window.ShowActivated);
                Assert.Equal(1, window.Opacity);
                Assert.True(window.Left >= 0);
                Assert.True(window.Top >= 0);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS", original);
        }
    }

    [Fact]
    public void ShowWindowForTest_DefaultMode_ShowsMinimizedAndSilent()
    {
        string? original = Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS");
        Environment.SetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS", null);

        try
        {
            TestExecutionGuards.RunSta(() =>
            {
                Window window = TestWindowFactory.CreateOffscreenWindow(showInTaskbar: true, width: 320, height: 240);

                try
                {
                    TestWindowFactory.ShowWindowForTest(window);

                    Assert.Equal(WindowState.Minimized, window.WindowState);
                    Assert.False(window.ShowInTaskbar);
                    Assert.False(window.ShowActivated);
                    Assert.Equal(Visibility.Visible, window.Visibility);
                }
                finally
                {
                    if (window.Visibility != Visibility.Hidden)
                    {
                        window.Close();
                    }
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDIOPILOT_TEST_SHOW_WINDOWS", original);
        }
    }
}
