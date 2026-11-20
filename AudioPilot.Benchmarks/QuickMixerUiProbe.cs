using System.Collections.Immutable;
using System.Diagnostics;
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
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter();
        app.Resources["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        app.Resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/AudioPilot;component/Themes/DarkTheme.xaml") });
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
                                    foreach (var container in containers) ((QuickMixerRow)container.Content).UpdateMeter(frame, mode);
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
                            P95ConstructionLayoutMs = warm[^1],
                            MedianConstructionLayoutAllocatedBytes = allocated.Skip(1).Order().ElementAt(2),
                            MedianMeterProjectionLayoutMs = Median([.. updates.Order()]),
                            P95MeterProjectionLayoutMs = updates.Order().ElementAt((int)Math.Ceiling((updates.Count - 1) * .95))
                        });
                        Console.WriteLine($"{mode}/{theme}/{language}, {count} apps: {Median(warm):F2} ms open/layout, {realized} realized rows");
                    }
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
            Notes = "Synthetic rows, real WPF controls, off-screen 460x500 DIP viewport. No native audio, icon extraction, GPU frame-pacing or cold-process startup is measured. One warmup plus five samples per case; 100 meter projection/layout samples. Images use WPF RenderTargetBitmap, not Windows capture.",
            Cases = results
        }, IpcBenchmarkSession.ReportJson));
        app.Shutdown();
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
            Task<bool> apply = Mixer.ApplyRefreshResultsAsync(0, null, rows, null, CancellationToken.None);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
            if (!apply.GetAwaiter().GetResult()) throw new InvalidOperationException("Synthetic projection did not apply.");
            foreach (var (id, row) in rows) Mixer.DetachOwnedVolumeChangedHandler(id, row);
            Readings = [.. rows.Select(pair => AudioMeterReading.FromPeak(new(mode, endpoint, pair.Item2.IsEndpoint ? null : pair.Item2.SessionInstanceId, pair.Item2.ProcessId), .3f))];
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
