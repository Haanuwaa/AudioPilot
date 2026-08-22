using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class OverlayResourceTests
{
    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task MediaUpdatesAndTimedHiding_ReuseWindowAndReleaseItOnShutdown(AppTheme theme)
    {
        var samples = new List<object>();
        var times = new List<double>();
        WeakReference? closedWindow = null;
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            WindowThemeResolver.SetApplicationThemeProvider(() => theme);
            WindowThemeResolver.ApplyApplicationTheme(theme);
            var original = application.Windows.Cast<Window>().ToHashSet();
            using var service = new OverlayService();
            try
            {
                service.UpdateDisplayOptions(OverlayPosition.BottomRight, 0.5);
                OverlayWindow? overlay = null;
                nint handle = 0;
                for (int cycle = 1; cycle <= 120; cycle++)
                {
                    string title = cycle % 3 == 0 ? $"Track {cycle} 🎵 日本語 - a longer changing title with 👩🏽‍💻 emoji" : $"Track {cycle}";
                    string? artist = cycle % 4 == 0 ? null : $"Artist {cycle} 🎸";
                    string header = cycle % 2 == 0 ? "Paused" : "Now playing";
                    var timer = Stopwatch.StartNew();
                    service.ShowMediaTrack(header, title, artist, cycle % 5 == 0 ? "01:25 / 04:10" : null);
                    await application.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                    times.Add(timer.Elapsed.TotalMilliseconds);
                    var current = Assert.IsType<OverlayWindow>(Assert.Single(application.Windows.Cast<Window>().Except(original)));
                    if (overlay == null)
                    {
                        overlay = current;
                        handle = new WindowInteropHelper(current).Handle;
                        closedWindow = new WeakReference(current);
                    }
                    Assert.Same(overlay, current);
                    Assert.Equal(handle, new WindowInteropHelper(current).Handle);
                    Assert.True(current.IsVisible);
                    Assert.Equal(title, current.GetMediaOverlayTextStateForTests().Title);
                    Assert.Equal(artist ?? string.Empty, current.GetMediaOverlayTextStateForTests().Artist);
                    service.ShowMediaTrack("Stale", "Must not replace current track", null, isCurrent: () => false);
                    await application.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                    Assert.Equal(title, current.GetMediaOverlayTextStateForTests().Title);
                    if (cycle % 6 == 0)
                    {
                        var timeout = Stopwatch.StartNew();
                        while (current.IsVisible && timeout.Elapsed < TimeSpan.FromSeconds(3))
                            await Task.Delay(25, TestContext.Current.CancellationToken);
                        Assert.False(current.IsVisible);
                        Assert.False(TestPrivateAccess.GetField<DispatcherTimer>(current, "_closeTimer").IsEnabled);
                        Assert.False(current.GetDisplayStateForTests().IsFadeOutCompletionHooked);
                    }
                    if (cycle % 20 == 0) Sample(cycle, samples);
                }
                service.ShowMediaTrack("Now playing", "Shutdown during presentation 🎵", "Artist");
                await application.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                service.Dispose();
                Assert.DoesNotContain(overlay!, application.Windows.Cast<Window>());
                Assert.False(TestPrivateAccess.GetField<DispatcherTimer>(overlay!, "_closeTimer").IsEnabled);
                service.ShowMediaTrack("Late", "Ignored after shutdown", null);
                await application.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Empty(application.Windows.Cast<Window>().Except(original));
            }
            finally { WindowThemeResolver.SetApplicationThemeProvider(null); }
        }, TimeSpan.FromMinutes(2));
        await SharedStaDispatcherHost.RunAsync(async () =>
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle));
        Sample(121, samples);
        Assert.NotNull(closedWindow);
        Assert.False(closedWindow.IsAlive);
        double[] measured = [.. times.Skip(20).Order()];
        string report = JsonSerializer.Serialize(new { Samples = samples, MedianUpdateMs = measured[measured.Length / 2], P95UpdateMs = measured[(int)(measured.Length * 0.95)], RetainedWindows = closedWindow.IsAlive ? 1 : 0 });
        TestContext.Current.TestOutputHelper!.WriteLine(report);
        if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"media-overlay-lifetime-{theme}.json"), report);
        }
    }

    private static void Sample(int cycle, List<object> samples)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        samples.Add(new { Cycle = cycle, ManagedBytes = GC.GetTotalMemory(false), PrivateBytes = process.PrivateMemorySize64, Handles = process.HandleCount });
    }
}
