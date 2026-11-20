using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class QuickPanelLayoutStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PanelOnlyReleasesItsOwnApplicationMainWindowReference(bool ownsReference) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window? previous = application.MainWindow;
        var panel = new Window();
        var main = new Window();
        using var controller = new QuickPanelWindowController(panel, new(), new(), new(), new(), QuickPanelKind.Mixer, () => { }, null);
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
        using var controller = new QuickPanelWindowController(window, pin, new(), new(), new(), QuickPanelKind.Mixer, () => { }, store);
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
        using var controller = new QuickPanelWindowController(window, new(), new(), new(), new(), QuickPanelKind.Mixer,
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
        using (var controller = new QuickPanelWindowController(window, new(), new(), new(), new(), kind, () => { }, store))
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
        using var restoredController = new QuickPanelWindowController(restored, new(), new(), new(), new(), kind, () => Assert.Fail("A valid pinned layout must not be recentered."), reopened);
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
        var grip = new ResizeGrip();
        using var controller = new QuickPanelWindowController(window, pin, top, resize, grip, QuickPanelKind.Mixer, () => { }, store);
        try
        {
            await controller.PrepareAsync();
            pin.IsChecked = true;
            Assert.False(window.Topmost);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
            Assert.Equal(Visibility.Visible, grip.Visibility);
            controller.ToggleTopmost();
            Assert.True(window.Topmost);
            pin.IsChecked = false;
            Assert.True(window.Topmost);
            Assert.False(window.ShowInTaskbar);
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
