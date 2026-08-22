using System.Windows;

namespace AudioPilot.Tests.Helpers;

[Collection("WpfApplicationIsolation")]
public sealed class TestWindowFactoryTests
{
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
