using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed partial class QuickPanelLayoutStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlushRetriesTheLatestFailedPinWriteOnce(bool retrySucceeds)
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        int attempts = 0;
        var store = new QuickPanelLayoutStore(path, logger, (file, text) =>
        {
            if (++attempts == 1 || !retrySucceeds) throw new IOException("Simulated write failure");
            File.WriteAllText(file, text);
        });
        await store.FlushAsync();
        Assert.Equal(0, attempts);
        string pin = new('A', 64);
        await store.SaveMixerPreferencesAsync(new(true, [pin]));
        await store.FlushAsync();
        Assert.Equal(2, attempts);
        var restored = await new QuickPanelLayoutStore(path, logger).LoadMixerPreferencesAsync(TestContext.Current.CancellationToken);
        if (retrySucceeds) Assert.Equal(pin, Assert.Single(restored.PinnedApps));
        else Assert.Empty(restored.PinnedApps);
    }

    [Fact]
    public Task LivePinToggleKeepsNativeWindowVisibleAndUsesOneWorkingGrip() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var window = new Window
        {
            Width = 460,
            Height = 440,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            Content = new TextBox(),
        };
        var pin = new ToggleButton();
        using var controller = new QuickPanelWindowController(window, pin, new(), new(), QuickPanelKind.Mixer, () => { }, null);
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window, hideFromTaskbar: false);
            await controller.PrepareAsync();
            nint handle = new WindowInteropHelper(window).Handle;
            int visibilityChanges = 0;
            HwndSource.FromHwnd(handle).AddHook((hwnd, message, wParam, lParam, ref handled) =>
            {
                if (message == 0x0047 && (Marshal.ReadInt32(lParam, 2 * IntPtr.Size + 4 * sizeof(int)) & 0x00C0) != 0)
                    visibilityChanges++;
                return 0;
            });
            Assert.Equal(0x00000080, GetWindowLong(handle, -20) & 0x00040080);
            pin.IsChecked = true;
            window.UpdateLayout();
            Assert.Equal(0x00040000, GetWindowLong(handle, -20) & 0x00040080);
            var grip = Assert.Single(WpfVisualTree.Descendants(window).OfType<ResizeGrip>(), item => item.IsVisible);
            Point corner = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
            nint hitPoint = unchecked(((int)corner.Y & 0xffff) << 16 | ((int)corner.X & 0xffff));
            Assert.Equal((nint)17, SendMessage(handle, 0x0084, 0, hitPoint));
            pin.IsChecked = false;
            window.UpdateLayout();
            Assert.Equal(0x00000080, GetWindowLong(handle, -20) & 0x00040080);
            Assert.DoesNotContain(WpfVisualTree.Descendants(window).OfType<ResizeGrip>(), item => item.IsVisible);
            Assert.Equal(0, visibilityChanges);
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
            Assert.False(window.IsActive);
        }
        finally { window.Close(); }
    });

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [Fact]
    public async Task MixerPreferencesSurviveReopenAndInterleavedWindowLayoutWrites()
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        var store = new QuickPanelLayoutStore(path, logger);
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(QuickMixerPreferences.Default, await store.LoadMixerPreferencesAsync(token));
        string pin = new('A', 64);
        var preferences = new QuickMixerPreferences(true, [pin]);
        var layout = new QuickPanelLayout(true, true, null);
        Task saveOrder = store.SaveMixerPreferencesAsync(preferences);
        Task saveLayout = store.SaveAsync(QuickPanelKind.Mixer, layout);
        await Task.WhenAll(saveOrder, saveLayout);

        var reopened = new QuickPanelLayoutStore(path, logger);
        var restored = await reopened.LoadMixerPreferencesAsync(token);
        Assert.True(restored.PlayingFirst);
        Assert.Equal(pin, Assert.Single(restored.PinnedApps));
        Assert.Equal(layout, await reopened.LoadAsync(QuickPanelKind.Mixer, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PanelOnlyReleasesItsOwnApplicationMainWindowReference(bool ownsReference) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window? previous = application.MainWindow;
        var panel = new Window();
        var main = new Window();
        using var controller = new QuickPanelWindowController(panel, new(), new(), new(), QuickPanelKind.Mixer, () => { }, null);
        try
        {
            application.MainWindow = ownsReference ? panel : main;
            await controller.PrepareAsync();
            Assert.Same(ownsReference ? null : main, application.MainWindow);
            application.MainWindow = ownsReference ? panel : main;
            controller.Dispose();
            Assert.Same(ownsReference ? null : main, application.MainWindow);
        }
        finally { panel.Close(); main.Close(); application.MainWindow = previous; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ResetKeepsPanelPreferencesAndCannotRestoreTheOldPinnedBounds(bool pinned) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        var store = new QuickPanelLayoutStore(path, logger);
        var other = new QuickPanelLayout(true, false, new(100, 100, 560, 500, 96, false));
        await store.SaveAsync(QuickPanelKind.DevicePicker, other);
        await store.SaveAsync(QuickPanelKind.Mixer, new(pinned, true, new(100, 100, 800, 700, 96, false)));
        var window = new Window { Width = 700, Height = 600, Opacity = 0, ShowActivated = false };
        var pin = new ToggleButton();
        using var controller = new QuickPanelWindowController(window, pin, new(), new(), QuickPanelKind.Mixer, () => { }, store);
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            await controller.PrepareAsync();
            controller.ResetLayout(() => { window.Width = 460; window.Height = 400; window.Left = 140; window.Top = 160; });
            TestWindowFactory.CompleteInitialLayout(window);
            var reset = Assert.IsType<SavedWindowPlacement>(MainWindowPlacementService.Capture(window));
            Assert.Equal(pinned, controller.IsPinned);
            Assert.True(window.Topmost);
            await store.FlushAsync();
            var saved = await new QuickPanelLayoutStore(path, logger).LoadAsync(QuickPanelKind.Mixer, TestContext.Current.CancellationToken);
            Assert.Equal(pinned, saved.Pinned);
            Assert.True(saved.AlwaysOnTop);
            Assert.Equal(pinned ? reset : null, saved.Placement);
            if (!pinned)
            {
                pin.IsChecked = true;
                TestWindowFactory.CompleteInitialLayout(window);
                Assert.Equal(reset, MainWindowPlacementService.Capture(window));
            }
            Assert.Equal(other, await store.LoadAsync(QuickPanelKind.DevicePicker, TestContext.Current.CancellationToken));
        }
        finally { window.Close(); }
        await store.FlushAsync();
    });

    [Fact]
    public Task ClosingBeforePreparationDoesNotReadDisposedStateOrQueueLayoutWork() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        var store = new QuickPanelLayoutStore(path, logger);
        var window = new Window();
        using var controller = new QuickPanelWindowController(window, new(), new(), new(), QuickPanelKind.Mixer,
            () => Assert.Fail("A closed panel must not be positioned."), store);
        window.Close();
        controller.Dispose();
        await controller.PrepareAsync();
        controller.ResetLayout(() => Assert.Fail("A closed panel must not be reset."));
        Assert.False(File.Exists(path));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PinnedNativeBoundsSurviveCloseAndReopenWithoutChangingTheOtherPanel(bool picker) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        var kind = picker ? QuickPanelKind.DevicePicker : QuickPanelKind.Mixer;
        var other = picker ? QuickPanelKind.Mixer : QuickPanelKind.DevicePicker;
        var otherState = new QuickPanelLayout(false, true, new(10, 20, 470, 520, 96, false));
        var store = new QuickPanelLayoutStore(path, logger);
        await store.SaveAsync(other, otherState);
        await store.SaveAsync(kind, new(true, false, null));
        SavedWindowPlacement expected;
        var window = new Window { Width = 460, Height = 400, Opacity = 0, ShowActivated = false, WindowStyle = WindowStyle.None, AllowsTransparency = true };
        using (var controller = new QuickPanelWindowController(window, new(), new(), new(), kind, () => { }, store))
        {
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                await controller.PrepareAsync();
                Assert.NotSame(window, Application.Current?.MainWindow);
                window.Left = 120; window.Top = 140; window.Width = 520; window.Height = 430;
                TestWindowFactory.CompleteInitialLayout(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                expected = Assert.IsType<SavedWindowPlacement>(MainWindowPlacementService.Capture(window));
                Assert.False(window.Topmost);
            }
            finally { window.Close(); }
        }
        await store.FlushAsync();
        var reopened = new QuickPanelLayoutStore(path, logger);
        Assert.Equal(expected, (await reopened.LoadAsync(kind, TestContext.Current.CancellationToken)).Placement);
        Assert.Equal(otherState, await reopened.LoadAsync(other, TestContext.Current.CancellationToken));
        var restored = new Window { Width = 460, Height = 400, Opacity = 0, ShowActivated = false, WindowStyle = WindowStyle.None, AllowsTransparency = true };
        using var restoredController = new QuickPanelWindowController(restored, new(), new(), new(), kind, () => Assert.Fail("A valid pinned layout must not be recentered."), reopened);
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(restored);
            await restoredController.PrepareAsync();
            TestWindowFactory.CompleteInitialLayout(restored);
            var actual = Assert.IsType<SavedWindowPlacement>(MainWindowPlacementService.Capture(restored));
            Assert.True(restoredController.IsPinned);
            Assert.False(restored.Topmost);
            Assert.Equal(expected, actual);
        }
        finally { restored.Close(); }
        await reopened.FlushAsync();
    });

    [Fact]
    public async Task CoalescedSavesKeepBothPanelsAndTheLatestBounds()
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        var store = new QuickPanelLayoutStore(path, logger, (file, text) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            File.WriteAllText(file, text);
        });
        await store.LoadAsync(QuickPanelKind.Mixer, TestContext.Current.CancellationToken);
        var first = new QuickPanelLayout(true, false, new(100, 100, 600, 600, 96, false));
        Task pending = store.SaveAsync(QuickPanelKind.Mixer, first);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            for (int i = 0; i < 30; i++) _ = store.SaveAsync(QuickPanelKind.Mixer, first with { Placement = first.Placement! with { Right = 630 + i } });
            _ = store.SaveAsync(QuickPanelKind.DevicePicker, first with { AlwaysOnTop = true });
        }
        finally { release.Set(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var reopened = new QuickPanelLayoutStore(path, logger);
        Assert.Equal(659, (await reopened.LoadAsync(QuickPanelKind.Mixer, TestContext.Current.CancellationToken)).Placement!.Right);
        Assert.True((await reopened.LoadAsync(QuickPanelKind.DevicePicker, TestContext.Current.CancellationToken)).AlwaysOnTop);
        Assert.InRange(writes, 1, 2);
    }

    [Fact]
    public async Task FailedSaveCanRetryWithoutChangingTheLayoutAgain()
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        int writes = 0;
        var store = new QuickPanelLayoutStore(path, logger, (file, text) =>
        { if (++writes == 1) throw new IOException("Transient failure"); File.WriteAllText(file, text); });
        var state = new QuickPanelLayout(true, false, null);
        await store.SaveAsync(QuickPanelKind.Mixer, state);
        Assert.False(File.Exists(path));
        await store.SaveAsync(QuickPanelKind.Mixer, state);
        Assert.Equal(2, writes);
        Assert.Equal(state, await new QuickPanelLayoutStore(path, logger).LoadAsync(QuickPanelKind.Mixer, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOrOversizedLocalStateFallsBackWithoutRewritingIt(bool oversized)
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName);
        string original = oversized ? new string('x', 8193) : "{\"Mixer\":null,\"Mixer\":{\"Pinned\":true,\"AlwaysOnTop\":true,\"Placement\":null}}";
        File.WriteAllText(path, original);
        var state = await new QuickPanelLayoutStore(path, logger).LoadAsync(QuickPanelKind.Mixer, TestContext.Current.CancellationToken);
        Assert.False(state.Pinned);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public Task PinAndTopmostAreIndependentAndPersistSeparatelyForEachPanel() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var directory = new TestSettingsWorkspace(nameof(QuickPanelLayoutStoreTests));
        using var logger = Logger.CreateInMemoryForTests();
        var store = new QuickPanelLayoutStore(Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName), logger);
        var window = new Window { Width = 460, Height = 400 };
        var pin = new ToggleButton();
        var top = new MenuItem();
        var resize = new MenuItem();
        using var controller = new QuickPanelWindowController(window, pin, top, resize, QuickPanelKind.Mixer, () => { }, store);
        try
        {
            await controller.PrepareAsync();
            pin.IsChecked = true;
            Assert.False(window.Topmost);
            Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
            controller.ToggleTopmost();
            Assert.True(window.Topmost);
            pin.IsChecked = false;
            Assert.True(window.Topmost);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            pin.IsChecked = true;
            Assert.True(window.Topmost);
            await store.FlushAsync();
            var reopened = new QuickPanelLayoutStore(Path.Combine(directory.PrimaryDir, QuickPanelLayoutStore.FileName), logger);
            Assert.Equal(new QuickPanelLayout(true, true, null), await reopened.LoadAsync(QuickPanelKind.Mixer, TestContext.Current.CancellationToken));
            Assert.False((await reopened.LoadAsync(QuickPanelKind.DevicePicker, TestContext.Current.CancellationToken)).Pinned);
        }
        finally { window.Close(); }
    });
}
