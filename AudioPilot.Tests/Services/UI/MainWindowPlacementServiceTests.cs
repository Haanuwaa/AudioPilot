using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class MainWindowPlacementServiceTests
{
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
    public async Task InvalidPlacementFallsBackWithoutBlockingStartup(string kind)
    {
        using var directory = new TestScopedDirectory(nameof(MainWindowPlacementServiceTests));
        string path = Path.Combine(directory.Root, "window-placement.json");
        string content = kind switch
        {
            "malformed" => "{",
            "oversized" => new string('x', 5000),
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
                window.WindowState = WindowState.Normal;
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
}
