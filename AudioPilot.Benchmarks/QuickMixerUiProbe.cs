using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Platform;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.Services.UI;
using AudioPilot.ViewModels;

namespace AudioPilot.Benchmarks;

internal static class QuickMixerUiProbe
{
    internal static int Run(string outputDirectory, bool interactive)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(Path.GetFullPath(outputDirectory), interactive); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("Quick Mixer UI probe failed.", failure);
        return 0;
    }

    private static void RunOnSta(string output, bool interactive)
    {
        Directory.CreateDirectory(output);
        var app = CreateApplication();
        if (interactive)
        {
            using var layoutLogger = Logger.CreateInMemoryForTests();
            var layouts = new QuickPanelLayoutStore(Path.Combine(output, QuickPanelLayoutStore.FileName), layoutLogger);
            if (!File.Exists(Path.Combine(output, QuickPanelLayoutStore.FileName)))
                layouts.SaveAsync(QuickPanelKind.Mixer, new(true, false, null)).GetAwaiter().GetResult();
            using var outputFixture = new Fixture(AudioMixerMode.Output, 64);
            using var inputFixture = new Fixture(AudioMixerMode.Input, 16);
            using var sampler = new AudioMeterSampler(() => new MeterBackend(outputFixture, inputFixture));
            var window = new QuickMixerWindow(mode => mode == AudioMixerMode.Output ? outputFixture.Mixer : inputFixture.Mixer,
                _ => new Lease(), (_, _) => Task.CompletedTask, sampler, QuickDevicePickerWindow.GetForegroundWindow(), dismissOnDeactivate: false, layouts: layouts)
            {
                Title = "AudioPilot Quick Mixer - test data",
                ShowInTaskbar = true
            };
            window.Closed += async (_, _) => { await layouts.FlushAsync(); app.Shutdown(); };
            app.Run(window);
            sampler.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            return;
        }

        var (results, playingFirst, closedWindows) = CaptureWindows(output);
        var drain = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => drain.Continue = false));
        Dispatcher.PushFrame(drain);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int surviving = closedWindows.Count(window => window.IsAlive);
        Console.WriteLine($"Closed windows still reachable after dispatcher drain and GC: {surviving}/{closedWindows.Count}");
        File.WriteAllText(Path.Combine(output, "quick-mixer-ui.json"), JsonSerializer.Serialize(new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ClosedWindowCount = closedWindows.Count,
            ClosedWindowsAliveAfterGc = surviving,
            Notes = "Synthetic rows, real WPF controls, off-screen 460x500 DIP viewport. No native audio, icon extraction, GPU frame-pacing or cold-process startup is measured. One warmup plus five samples per layout case; 100 meter projection/layout samples. Images use WPF RenderTargetBitmap, not Windows capture.",
            Cases = results,
            PlayingFirst = playingFirst
        }, IpcBenchmarkSession.ReportJson));
        app.Shutdown();
    }

    private static Application CreateApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter();
        app.Resources["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        app.Resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/AudioPilot;component/Themes/DarkTheme.xaml") });
        return app;
    }

    internal static void ValidatePlayingFirst()
    {
        var app = CreateApplication();
        try { MeasurePlayingFirst(64, 2, []); }
        finally { app.Shutdown(); }
    }

    // Let all strong references in the layout loops leave the stack before measuring retention.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (List<object> Cases, object[] PlayingFirst, List<WeakReference> ClosedWindows) CaptureWindows(string output)
    {
        var results = new List<object>();
        var closedWindows = new List<WeakReference>();
        foreach (int count in new[] { 8, 64, 256 })
            foreach (AudioMixerMode mode in Enum.GetValues<AudioMixerMode>())
                foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
                    foreach (string language in new[] { "en", "es" })
                    {
                        var layouts = new List<double>();
                        var updates = new List<double>();
                        var allocated = new List<long>();
                        int realized = 0;
                        for (int sample = 0; sample < 6; sample++)
                        {
                            using var fixture = new Fixture(mode, count);
                            using var meters = new AudioMeterSampler(() => throw new InvalidOperationException("Off-screen profiling must not read native audio."));
                            using var page = new QuickMixerPage(fixture.Mixer, Dispatcher.CurrentDispatcher);
                            var locale = new LocalizationService(language);
                            long bytes = GC.GetAllocatedBytesForCurrentThread();
                            long started = Stopwatch.GetTimestamp();
                            var window = new QuickMixerWindow(_ => fixture.Mixer, _ => new Lease(), (_, _) => Task.CompletedTask,
                                meters, 0, mode, locale);
                            closedWindows.Add(new(window));
                            try
                            {
                                WindowThemeResolver.ApplyWindowTheme(window, theme);
                                window.SessionList.ItemsSource = page.Rows;
                                var root = (FrameworkElement)window.Content;
                                Layout(root);
                                var containers = Descendants(root).OfType<ListBoxItem>().ToArray();
                                if (containers.Length is < 1 or > 12) throw new InvalidOperationException($"Unexpected realization count: {containers.Length} of {count}.");
                                realized = Math.Max(realized, containers.Length);
                                layouts.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                allocated.Add(GC.GetAllocatedBytesForCurrentThread() - bytes);
                                for (int tick = 0; tick < 20; tick++)
                                {
                                    var readings = fixture.Readings.Select(reading => reading with
                                    {
                                        Peak = tick % 2 == 0 ? .2f : .65f,
                                        VolumeScalar = mode == AudioMixerMode.Output && !reading.Source.IsEndpoint ? (25 + reading.Source.ProcessId!.Value % 50) / 100f : null,
                                        IsMuted = mode == AudioMixerMode.Output && !reading.Source.IsEndpoint ? false : null
                                    }).ToImmutableArray();
                                    var frame = new AudioMeterSnapshot(0, 0, new(readings, AudioMeterModes.Both), TimeSpan.Zero, 0, false);
                                    started = Stopwatch.GetTimestamp();
                                    foreach (var container in containers) page.UpdateRowMeter((QuickMixerRow)container.Content, frame);
                                    root.UpdateLayout();
                                    if (sample > 0) updates.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                }
                                if (sample == 1 && count == 8)
                                {
                                    window.SessionList.SelectedItem = page.Rows[1];
                                    root.UpdateLayout();
                                    var bitmap = new RenderTargetBitmap(460, 500, 96, 96, PixelFormats.Pbgra32);
                                    bitmap.Render(root);
                                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                    using var stream = File.Create(Path.Combine(output, $"{mode}-{theme}-{language}.png")); encoder.Save(stream);
                                }
                            }
                            finally { window.Close(); }
                        }
                        double[] warm = [.. layouts.Skip(1).Order()];
                        results.Add(new
                        {
                            Apps = count,
                            Mode = mode.ToString(),
                            Theme = theme.ToString(),
                            Language = language,
                            RealizedRows = realized,
                            FirstConstructionLayoutMs = layouts[0],
                            MedianConstructionLayoutMs = Median(warm),
                            P95ConstructionLayoutMs = IpcBenchmarkSession.Percentile(warm, .95),
                            MedianConstructionLayoutAllocatedBytes = allocated.Skip(1).Order().ElementAt(2),
                            MedianMeterProjectionLayoutMs = Median([.. updates.Order()]),
                            P95MeterProjectionLayoutMs = updates.Order().ElementAt((int)Math.Ceiling((updates.Count - 1) * .95))
                        });
                        Console.WriteLine($"{mode}/{theme}/{language}, {count} apps: {Median(warm):F2} ms open/layout, {realized} realized rows");
                    }
        return (results, [MeasurePlayingFirst(64, 12, closedWindows), MeasurePlayingFirst(256, 12, closedWindows)], closedWindows);
    }

    private static object MeasurePlayingFirst(int count, int samples, List<WeakReference> closedWindows)
    {
        using var fixture = new Fixture(AudioMixerMode.Output, count);
        using var meters = new AudioMeterSampler(() => throw new InvalidOperationException("Off-screen profiling must not read native audio."));
        var window = new QuickMixerWindow(_ => fixture.Mixer, _ => new Lease(), (_, _) => Task.CompletedTask, meters, 0);
        closedWindows.Add(new(window));
        try
        {
            const string pinKey = "fixture:pinned";
            fixture.Mixer.Sessions.Single(row => row.ProcessId == count - 1).MixerPinKey = pinKey;
            Slider? heldSlider = null;
            var preferences = new QuickMixerPreferences(true, [pinKey]);
            using var page = new QuickMixerPage(fixture.Mixer, Dispatcher.CurrentDispatcher, preferences,
                () => window.IsOrderHeldForInput(heldSlider, null));
            window.SessionList.ItemsSource = page.Rows;
            var root = (FrameworkElement)window.Content;
            Layout(root);
            var scroll = Descendants(window.SessionList).OfType<ScrollViewer>().First();
            var heldTimes = new List<double>(samples);
            var releasedTimes = new List<double>(samples);
            uint retired = (uint)count;
            var originalReadings = fixture.Readings.Where(reading => reading.Source.ProcessId != retired)
                .Select(reading => reading with { Peak = 0 }).ToImmutableArray();
            for (int sample = 0; sample <= samples; sample++)
            {
                uint active = (uint)(count + 1 + sample);
                string session = $"replacement-{active}";
                Fixture.ApplyRows(fixture.Mixer, [$"pid:{retired}"],
                    [($"pid:{active}", new("Replacement app", 55, false, false, processId: active, sessionInstanceId: session, endpointId: "synthetic-output"))]);
                retired = active;
                page.Synchronize();
                scroll.ScrollToEnd();
                root.UpdateLayout();
                heldSlider = Descendants(window.SessionList).OfType<Slider>().First(slider => window.IsOrderHeldForInput(slider, null));
                double offset = scroll.VerticalOffset;
                var playing = page.Rows.Single(row => row.Source.ProcessId == active);
                var snapshot = new AudioMeterSnapshot(0, Stopwatch.GetTimestamp(), new(originalReadings.Add(
                    AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "synthetic-output", session, active), .6f, .55f, false)),
                    AudioMeterModes.Output), TimeSpan.Zero, 0, false);

                long started = Stopwatch.GetTimestamp();
                page.UpdatePlaybackOrder(snapshot);
                page.ApplyPendingOrder();
                root.UpdateLayout();
                double heldMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!playing.IsPlaying || !ReferenceEquals(playing, page.Rows[^1]) || scroll.ScrollableHeight <= 0 ||
                    scroll.ScrollableHeight - offset > 1 || scroll.VerticalOffset != offset || !page.Rows[1].IsPinnedApp)
                    throw new InvalidOperationException("Playing first did not preserve the pin, bottom position and held order.");

                heldSlider = null;
                started = Stopwatch.GetTimestamp();
                page.ApplyPendingOrder();
                root.UpdateLayout();
                double releasedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!page.Rows[1].IsPinnedApp || !ReferenceEquals(playing, page.Rows[2]) || page.Rows.Count != count + 1 ||
                    Descendants(window.SessionList).OfType<ListBoxItem>().Count() is < 1 or > 12)
                    throw new InvalidOperationException("Playing first did not promote the replacement below the pin with bounded realization.");

                if (sample == 0) continue;
                heldTimes.Add(heldMs);
                releasedTimes.Add(releasedMs);
            }
            double[] held = [.. heldTimes.Order()];
            double[] released = [.. releasedTimes.Order()];
            double heldP95 = IpcBenchmarkSession.Percentile(held, .95);
            double releasedP95 = IpcBenchmarkSession.Percentile(released, .95);
            Console.WriteLine($"Playing first, {count} apps: held p95 {heldP95:F2} ms, released p95 {releasedP95:F2} ms");
            return new
            {
                Apps = count,
                Samples = samples,
                MedianHeldMeterOrderLayoutMs = IpcBenchmarkSession.Percentile(held, .5),
                P95HeldMeterOrderLayoutMs = heldP95,
                MedianReleasedReorderLayoutMs = IpcBenchmarkSession.Percentile(released, .5),
                P95ReleasedReorderLayoutMs = releasedP95,
                Notes = "Pinned app, replacement session signal, bottom scroll and synthetic slider pointer. Session mutation and scrolling are outside timing."
            };
        }
        finally { window.Close(); }
    }

    private static double Median(double[] values) => values[values.Length / 2];
    private static void Layout(FrameworkElement root) { root.Measure(new Size(460, 500)); root.Arrange(new Rect(0, 0, 460, 500)); root.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }

    internal sealed class Fixture : IDisposable
    {
        private readonly Logger _logger = Logger.CreateInMemoryForTests();
        private readonly DeviceCacheHelper _cache;
        internal MixerViewModel Mixer { get; }
        internal ImmutableArray<AudioMeterReading> Readings { get; }
        internal Fixture(AudioMixerMode mode, int count)
        {
            _logger.MinimumLevel = LogLevel.None;
            _cache = new(null!, new DeviceCacheHelperRuntime
            {
                CaptureSnapshotsAsync = () => throw new InvalidOperationException("No native discovery."),
                MaterializeDeviceById = _ => null,
                GetFallbackPlaybackDevice = (_, _) => null,
                GetFallbackRecordingDevice = (_, _) => null,
                ProbeMuteState = (_, _, _) => null,
                RunRefreshWorkAsync = work => work()
            }, _logger);
            Mixer = new(null!, Dispatcher.CurrentDispatcher, mode, _logger, _cache);
            bool input = mode == AudioMixerMode.Input;
            string endpoint = input ? "synthetic-input" : "synthetic-output";
            var rows = new List<(string, AudioSessionItem)>
            {
                (input ? "mic:primary" : "master:primary", new(input ? "Microphone Volume" : "Master Volume", 70, !input, input,
                    endpointId: endpoint, endpointName: input ? "Synthetic microphone" : "Synthetic speakers"))
            };
            rows.AddRange(Enumerable.Range(1, count).Select(index => ($"pid:{index}", new AudioSessionItem($"Application {index:D3}", 25 + index % 50,
                false, false, processId: (uint)index, sessionInstanceId: $"session-{mode}-{index}", endpointId: endpoint))));
            ApplyRows(Mixer, null, rows);
            Readings = [.. rows.Select(pair => AudioMeterReading.FromPeak(new(mode, endpoint, pair.Item2.IsEndpoint ? null : pair.Item2.SessionInstanceId, pair.Item2.ProcessId), .3f))];
        }

        internal static void ApplyRows(MixerViewModel mixer, List<string>? removed, List<(string Id, AudioSessionItem Item)> rows)
        {
            Task<bool> apply = mixer.ApplyRefreshResultsAsync(0, removed, rows, null, CancellationToken.None);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
            if (!apply.GetAwaiter().GetResult()) throw new InvalidOperationException("Synthetic projection did not apply.");
            foreach (var (id, row) in rows) mixer.DetachOwnedVolumeChangedHandler(id, row);
        }
        public void Dispose() { Mixer.Cleanup(); _cache.Dispose(); _logger.Dispose(); }
    }

    private sealed class MeterBackend(Fixture output, Fixture input) : IAudioMeterBackend
    {
        public AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken)
        {
            double pulse = (Math.Sin(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 3) + 1) / 2;
            return new([.. output.Readings.AddRange(input.Readings).Select(reading => reading with { Peak = (float)(pulse * .7) })], modes);
        }
        public void Dispose() { }
    }
}
