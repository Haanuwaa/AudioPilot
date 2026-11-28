using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 24)]
    [InlineData(0, 0)]
    public async Task TrayHideArmsMixerScroll_AndRefreshCanCorrectTheClampedOffset(int tab, int additionalRows)
    {
        using var clock = new ManualTimeProvider();
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            window.Height = 420;
            TestWindowFactory.ShowWindowForLayoutTest(window);
            await SettleMixer(window);
            var page = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var header = (Label)window.FindName("VolumeMixerHeader");
            Assert.Equal(0, page.VerticalOffset);

            await using var manager = new AppMainWindowManager(Application.Current, new RecordingAppDialogService());
            TestPrivateAccess.SetField(manager, "_window", window);
            try { Assert.True(manager.Hide()); }
            finally { TestPrivateAccess.SetField<MainWindow?>(manager, "_window", null); }
            Assert.False(window.IsVisible);
            await AddViewportRows(model.ActiveMixer, 1);

            var target = tab == 0 ? MixerRefreshTarget.Output : MixerRefreshTarget.Input;
            var settle = TestPrivateAccess.InvokeNonPublic<Action>(model, "TrackQueuedMixerRefresh", target);
            var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveLanding(object sender, ScrollChangedEventArgs e)
            {
                if (ReferenceEquals(e.OriginalSource, page) && Math.Abs(page.VerticalOffset - MixerHeaderOffset(header, page)) <= 1)
                    landed.TrySetResult();
            }

            page.ScrollChanged += ObserveLanding;
            try
            {
                double initialOffset;
                try
                {
                    window.Show();
                    await SettleMixer(window);
                    initialOffset = page.VerticalOffset;
                    Assert.InRange(Math.Abs(initialOffset - page.ScrollableHeight), 0, 1);
                    Assert.InRange(initialOffset, 1, MixerHeaderOffset(header, page) - 1);
                    if (additionalRows > 0)
                    {
                        await AddViewportRows(model.ActiveMixer, additionalRows);
                        await SettleMixer(window);
                        Assert.InRange(Math.Abs(page.VerticalOffset - initialOffset), 0, 1);
                        Assert.True(page.ScrollableHeight > MixerHeaderOffset(header, page));
                    }
                }
                finally { settle(); }
                if (additionalRows > 0)
                    await landed.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
                await SettleMixer(window);
                double expected = additionalRows > 0 ? MixerHeaderOffset(header, page) : initialOffset;
                Assert.InRange(Math.Abs(page.VerticalOffset - expected), 0, 1);
            }
            finally { page.ScrollChanged -= ObserveLanding; }
        }, initialTab: tab, mixerRestoreTimeProvider: clock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualInputAboveTheMixerCancelsThePendingCorrection(bool keyboard)
    {
        using var clock = new ManualTimeProvider();
        await WithWindow(AppTheme.Dark, async (window, model) =>
        {
            window.Height = 420;
            await AddViewportRows(model.Mixer, 1);
            var settle = TestPrivateAccess.InvokeNonPublic<Action>(model, "TrackQueuedMixerRefresh", MixerRefreshTarget.Output);
            var page = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var deviceActions = (WrapPanel)window.FindName("DeviceSettingsActions");
            try
            {
                window.MarkPendingAutoScrollOnNextShow();
                TestWindowFactory.ShowWindowForLayoutTest(window);
                await SettleMixer(window);
                Assert.NotNull(TestPrivateAccess.GetField<CancellationTokenSource?>(window, "_restoreMixerScrollCts"));
                if (keyboard)
                {
                    deviceActions.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.PageUp)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                else
                {
                    deviceActions.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                }
                Assert.Null(TestPrivateAccess.GetField<CancellationTokenSource?>(window, "_restoreMixerScrollCts"));
                page.ScrollToTop();
                await SettleMixer(window);
                await AddViewportRows(model.Mixer, 24);
            }
            finally { settle(); }
            await SettleMixer(window);
            Assert.Equal(0, page.VerticalOffset);
        }, mixerRestoreTimeProvider: clock);
    }

    private static double MixerHeaderOffset(Label header, ScrollViewer page) =>
        header.TransformToAncestor((Visual)page.Content).Transform(default).Y;
}
