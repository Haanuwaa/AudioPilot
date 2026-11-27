using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowPlacementServiceTests
{
    [Fact]
    public async Task ExplicitResetReplacesAnOlderPendingWriteAndRemainsSavedAfterStop()
    {
        using var directory = new TestScopedDirectory(nameof(ExplicitResetReplacesAnOlderPendingWriteAndRemainsSavedAfterStop));
        using var logs = TestLoggerScope.CreateInMemory("placement-reset.log");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool releasedInTime = false;
        int writes = 0;
        string path = Path.Combine(directory.Root, "window-placement.json");
        var service = new MainWindowPlacementService(path, logs.Logger, (target, text) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.TrySetResult();
                releasedInTime = release.Wait(TimeSpan.FromSeconds(10));
            }
            AtomicFileWriter.WriteAllText(target, text);
        });
        Task first = service.PersistLatestAsync(new(100, 100, 700, 600, 96, true));
        Task<WindowLayoutSnapshot>? reset = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            reset = service.ChangeLayoutAsync(new(Reset: true), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(reset.IsCompleted);
            Assert.Equal("normal", (await service.GetLayoutAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
            release.Set();
            var applied = await reset.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(applied.Persisted);
            await service.StopAsync();
            var reopened = await new MainWindowPlacementService(path, logs.Logger).GetLayoutAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal((applied.X, applied.Y, applied.Width, applied.Height), (reopened.X, reopened.Y, reopened.Width, reopened.Height));
            Assert.Equal("normal", reopened.State);
            Assert.True(releasedInTime);
        }
        finally
        {
            release.Set();
            await first;
            if (reset != null) await reset;
        }
    }

    [Fact]
    public async Task CanceledGeometryEditsDoNotCreateOrOverwritePlacement()
    {
        using var directory = new TestScopedDirectory(nameof(CanceledGeometryEditsDoNotCreateOrOverwritePlacement));
        using var logs = TestLoggerScope.CreateInMemory("placement-cancel.log");
        string path = Path.Combine(directory.Root, "window-placement.json");
        var service = new MainWindowPlacementService(path, logs.Logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChangeLayoutAsync(new(Width: 700), cancellationToken: cancellation.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PlacementRoundTrips_AndRapidWritesKeepTheLatestBounds()
    {
        using var directory = new TestScopedDirectory(nameof(MainWindowPlacementServiceTests));
        string path = Path.Combine(directory.Root, "window-placement.json");
        var service = new MainWindowPlacementService(path, Logger.Instance);
        Assert.Null(await service.LoadAsync());
        var writes = new List<Task>();
        SavedWindowPlacement? expected = null;
        for (int index = 0; index < 100; index++)
        {
            expected = new(-1200 + index, 100, -600 + index, 600, 144, index % 2 == 0);
            writes.Add(service.PersistLatestAsync(expected));
        }
        await Task.WhenAll(writes);
        Assert.Equal(expected, await new MainWindowPlacementService(path, Logger.Instance).LoadAsync());
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("invalid-bounds")]
    [InlineData("incomplete")]
    [InlineData("duplicate")]
    public async Task InvalidPlacementFallsBackWithoutBlockingStartup(string kind)
    {
        using var directory = new TestScopedDirectory(nameof(MainWindowPlacementServiceTests));
        string path = Path.Combine(directory.Root, "window-placement.json");
        string content = kind switch
        {
            "malformed" => "{",
            "oversized" => new string('x', 5000),
            "incomplete" => "{\"Right\":700,\"Bottom\":600,\"Dpi\":96}",
            "duplicate" => "{\"Left\":100,\"Left\":200,\"Top\":100,\"Right\":700,\"Bottom\":600,\"Dpi\":96,\"Maximized\":false}",
            _ => JsonSerializer.Serialize(new SavedWindowPlacement(100, 100, 90, 500, 96, false)),
        };
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        Assert.Null(await new MainWindowPlacementService(path, Logger.Instance).LoadAsync());
    }

    [Fact]
    public async Task FailedAtomicWritePreservesThePreviousFile_AndCanRetry()
    {
        using var directory = new TestScopedDirectory(nameof(MainWindowPlacementServiceTests));
        string path = Path.Combine(directory.Root, "window-placement.json");
        var service = new MainWindowPlacementService(path, Logger.Instance);
        var original = new SavedWindowPlacement(100, 100, 700, 600, 96, false);
        var changed = original with { Left = 200, Right = 800 };
        await service.PersistLatestAsync(original);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await service.PersistLatestAsync(changed);
        Assert.Equal(original, await new MainWindowPlacementService(path, Logger.Instance).LoadAsync());
        Assert.Equal(changed, await service.LoadAsync());
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
        await service.PersistLatestAsync(changed);
        Assert.Equal(changed, await new MainWindowPlacementService(path, Logger.Instance).LoadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRestoreAndResizeSaveTheNormalBounds_AndStopDetachesTracking(bool maximized)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var directory = new TestScopedDirectory(nameof(MainWindowPlacementServiceTests));
            string path = Path.Combine(directory.Root, "window-placement.json");
            var service = new MainWindowPlacementService(path, Logger.Instance);
            await service.PersistLatestAsync(new(100, 100, 650, 600, 96, maximized));
            await service.LoadAsync();
            var window = new Window { Width = 550, Height = 500, Opacity = 0, ShowActivated = false };
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                Assert.True(service.TryRestore(window));
                Assert.Equal(maximized ? WindowState.Maximized : WindowState.Normal, window.WindowState);
                TestWindowFactory.CompleteInitialLayout(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                service.Start(window);
                await service.StopAsync();
                Assert.Equal(maximized, (await new MainWindowPlacementService(path, Logger.Instance).LoadAsync())!.Maximized);
                service.Start(window);
                var reset = await service.ChangeLayoutAsync(new(Reset: true), window, TestContext.Current.CancellationToken);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(reset.Persisted);
                await service.StopAsync();
                var reopened = await new MainWindowPlacementService(path, Logger.Instance).GetLayoutAsync(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal((reset.X, reset.Y, reset.Width, reset.Height), (reopened.X, reopened.Y, reopened.Width, reopened.Height));
                service.Start(window);
                window.Width = 610;
                window.Height = 490;
                TestWindowFactory.CompleteInitialLayout(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                await service.StopAsync();
                var saved = Assert.IsType<SavedWindowPlacement>(await new MainWindowPlacementService(path, Logger.Instance).LoadAsync());
                Assert.InRange(Math.Abs((saved.Right - saved.Left) * 96d / saved.Dpi - 610), 0, 3);
                Assert.InRange(Math.Abs((saved.Bottom - saved.Top) * 96d / saved.Dpi - 490), 0, 3);
                window.Width = 700;
                TestWindowFactory.CompleteInitialLayout(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                await service.StopAsync();
                Assert.Equal(saved, await new MainWindowPlacementService(path, Logger.Instance).LoadAsync());
            }
            finally { await service.StopAsync(); window.Close(); }
        });
    }

    [Fact]
    public async Task SavedWorkAreaSizedLayoutRestoresAtTheReportedSize()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var directory = new TestScopedDirectory(nameof(SavedWorkAreaSizedLayoutRestoresAtTheReportedSize));
            using var logs = TestLoggerScope.CreateInMemory("work-area-layout.log");
            string path = Path.Combine(directory.Root, "window-placement.json");
            var token = TestContext.Current.CancellationToken;
            var original = new MainWindowPlacementService(path, logs.Logger);
            var saved = await original.ChangeLayoutAsync(new(1, 1, 32768, 32768), cancellationToken: token);
            Assert.True(saved.Persisted);

            var reopened = new MainWindowPlacementService(path, logs.Logger);
            await reopened.LoadAsync();
            var window = new Window { Width = 550, Height = 500, Opacity = 0, ShowActivated = false };
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                Assert.True(reopened.TryRestore(window));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle, token);
                var actual = await reopened.GetLayoutAsync(window, token);
                Assert.Equal((saved.X, saved.Y, saved.Width, saved.Height), (actual.X, actual.Y, actual.Width, actual.Height));
                Assert.True(actual.Persisted);
            }
            finally { window.Close(); }
        });
    }
}
