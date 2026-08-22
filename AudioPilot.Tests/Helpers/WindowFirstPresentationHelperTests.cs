using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

[Collection("WpfApplicationIsolation")]
public sealed class WindowFirstPresentationHelperTests
{
    [Fact]
    public void Prepare_HidesUnmaterializedWindowAndTaskbarEntry()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var window = new Window
            {
                Opacity = 1d,
                ShowActivated = true,
                ShowInTaskbar = true,
            };

            WindowFirstPresentationHelper.Prepare(window);

            Assert.Equal(0d, window.Opacity);
            Assert.False(window.ShowActivated);
            Assert.False(window.ShowInTaskbar);
        });
    }

    [Fact]
    public void Prepare_StandaloneDialogPreservesItsTaskbarPolicy()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var window = new Window { ShowInTaskbar = true };

            WindowFirstPresentationHelper.Prepare(window, hideFromTaskbar: false);

            Assert.True(window.ShowInTaskbar);
            Assert.Equal(0d, window.Opacity);
            Assert.False(window.ShowActivated);
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void NativeClientBackground_MatchesTheThemedWindowBeforeFirstShow()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(NativeClientBackground_MatchesTheThemedWindowBeforeFirstShow)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Color expected = Color.FromRgb(0x1E, 0x1E, 0x1E);
            var window = new Window
            {
                Background = new SolidColorBrush(expected),
                ShowInTaskbar = false,
            };

            try
            {
                nint handle = new WindowInteropHelper(window).EnsureHandle();

                Assert.True(WindowFirstPresentationHelper.TryApplyNativeClientBackground(window));
                HwndTarget target = Assert.IsType<HwndTarget>(HwndSource.FromHwnd(handle)?.CompositionTarget);
                Assert.Equal(expected, target.BackgroundColor);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void FirstPresentation_RendersOffscreenThenPublishesAtTheFinalPosition()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(FirstPresentation_RendersOffscreenThenPublishesAtTheFinalPosition)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            double finalLeft = SystemParameters.VirtualScreenLeft + 64d;
            double finalTop = SystemParameters.VirtualScreenTop + 64d;
            var window = new Window
            {
                Width = 280d,
                Height = 180d,
                Left = finalLeft,
                Top = finalTop,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Content = new Border(),
            };
            var positionsDuringRelocation = new List<Point>();
            window.LocationChanged += (_, _) =>
            {
                nint handle = new WindowInteropHelper(window).Handle;
                if (handle != nint.Zero)
                {
                    positionsDuringRelocation.Add(new Point(window.Left, window.Top));
                }
            };
            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
                window.ShowInTaskbar = true;
                WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
                Assert.False(WindowFirstPresentationHelper.HasHandle(window));
                Assert.True(WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true));
                nint initialHandle = new WindowInteropHelper(window).Handle;
                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(
                    window,
                    activate: false,
                    waitForFirstRender: true);
                window.Show();

                Assert.True(window.Left < SystemParameters.VirtualScreenLeft);

                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Assert.True(reveal.Result);
                Assert.Equal(finalLeft, window.Left);
                Assert.Equal(finalTop, window.Top);
                Assert.True(window.ShowInTaskbar);
                Assert.Equal(1d, window.Opacity);
                Assert.Equal(initialHandle, new WindowInteropHelper(window).Handle);
                Assert.Contains(
                    positionsDuringRelocation,
                    position => Math.Abs(position.X - finalLeft) < 0.01d
                        && Math.Abs(position.Y - finalTop) < 0.01d);
                Assert.DoesNotContain(
                    positionsDuringRelocation,
                    position => (Math.Abs(position.X - finalLeft) < 0.01d)
                        != (Math.Abs(position.Y - finalTop) < 0.01d));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void FirstPresentation_UnpositionedWindowCreatesItsHandleOffscreenThenCentersOnReveal()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(FirstPresentation_UnpositionedWindowCreatesItsHandleOffscreenThenCentersOnReveal)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            const double width = 280d;
            const double height = 180d;
            var window = new Window
            {
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Content = new Border(),
            };

            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                WindowFirstPresentationHelper.StageOffscreenFirstRender(window);
                Assert.True(window.Left < SystemParameters.VirtualScreenLeft);
                Assert.False(WindowFirstPresentationHelper.HasHandle(window));

                window.ShowInTaskbar = true;
                WindowFirstPresentationHelper.BeginOffscreenFirstRender(window);
                Assert.True(WindowFirstPresentationHelper.TryApplyNativeClientBackground(window, ensureHandle: true));
                nint initialHandle = new WindowInteropHelper(window).Handle;
                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(
                    window,
                    activate: false,
                    waitForFirstRender: true);
                window.Show();

                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Rect workArea = SystemParameters.WorkArea;
                Assert.True(reveal.Result);
                Assert.Equal(workArea.Left + ((workArea.Width - width) / 2d), window.Left, precision: 3);
                Assert.Equal(workArea.Top + ((workArea.Height - height) / 2d), window.Top, precision: 3);
                Assert.Equal(initialHandle, new WindowInteropHelper(window).Handle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void RevealAsync_WithdrawnVisibilityIntentNeverExposesThePreparedWindow()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(RevealAsync_WithdrawnVisibilityIntentNeverExposesThePreparedWindow)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new Window
            {
                Width = 280d,
                Height = 180d,
                Left = SystemParameters.VirtualScreenLeft + 64d,
                Top = SystemParameters.VirtualScreenTop + 64d,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
            };
            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                window.Show();

                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(
                    window,
                    activate: false,
                    canReveal: static () => false);
                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Assert.False(reveal.Result);
                Assert.Equal(0d, window.Opacity);
                Assert.False(window.ShowActivated);
                Assert.False(window.ShowInTaskbar);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [VisualIntegrationFact]
    public void RevealAsync_CompletedFirstRenderPublishesOneVisibleInteractiveSurface()
    {
        if (!TestExecutionGuards.RequireVisualWpfIntegrationEnabled(nameof(RevealAsync_CompletedFirstRenderPublishesOneVisibleInteractiveSurface)))
        {
            return;
        }

        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new Window
            {
                Width = 280d,
                Height = 180d,
                Left = SystemParameters.VirtualScreenLeft + 64d,
                Top = SystemParameters.VirtualScreenTop + 64d,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
            };
            try
            {
                WindowFirstPresentationHelper.Prepare(window);
                window.ShowInTaskbar = true;
                window.Opacity = 1d;
                window.Show();

                Task<bool> reveal = WindowFirstPresentationHelper.RevealAsync(window, activate: false);
                TestPrivateAccess.RunTaskOnDispatcher(reveal);

                Assert.True(reveal.Result);
                Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Visible, window.Visibility);
                Assert.Equal(1d, window.Opacity);
                Assert.False(window.ShowActivated);
                Assert.True(window.ShowInTaskbar);
            }
            finally
            {
                window.Close();
            }
        });
    }

}
