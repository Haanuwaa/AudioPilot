using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class QuickDevicePickerWindowTests
{
    private static readonly QuickDevicePickerItem[] Devices =
    [
        new("speakers", "Desk speakers", null, true, true, true, false, AudioEndpointFormFactor.Speakers),
        new("headset", "Bluetooth headphones with a very long device name", null, true, false, false, true, AudioEndpointFormFactor.Headphones),
        new("monitor", "Display audio", null, true, false, false, false, AudioEndpointFormFactor.DigitalAudioDisplayDevice),
        new("mic", "USB microphone", null, false, true, true, false, AudioEndpointFormFactor.Microphone),
    ];

    [Fact]
    public async Task Switch_RechecksAvailabilityAndIgnoresDuplicateRequests()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>();
            int switches = 0;
            var window = Create(() => pending.Task, _ => { switches++; return Task.FromResult(false); });
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update(Devices);
                model.Selected = Devices[1];
                Task first = TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                await TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                Assert.True(model.Busy);
                pending.SetResult([Devices[1] with { CanReconnect = false }]);
                await first;
                Assert.Equal(0, switches);
                Assert.False(model.Busy);
                Assert.Contains("no longer available", model.Message);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Switch_UsesFreshIdentityAndMetadata()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var refreshed = Devices[1] with { Id = "HEADSET", Name = "Renamed headset", Available = true, StableId = "stable" };
            QuickDevicePickerItem? applied = null;
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([refreshed]),
                item => { applied = item; return Task.FromResult(false); });
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update(Devices);
                model.Selected = Devices[1];
                await TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                Assert.Same(refreshed, applied);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Refresh_RecoversEmptyGuidanceAfterTransientFailure()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            bool fail = false;
            var window = Create(() => fail
                ? Task.FromException<IReadOnlyList<QuickDevicePickerItem>>(new IOException("Device inventory unavailable"))
                : Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([]), _ => Task.FromResult(false));
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                string guidance = model.Message;
                fail = true;
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                Assert.Contains("Retrying", model.Message);
                fail = false;
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                Assert.Equal(guidance, model.Message);
                Assert.Contains("Add devices", guidance);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ClosingDuringAvailabilityCheck_PreventsSwitchAndStopsRefresh()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>();
            int switches = 0;
            var window = Create(() => pending.Task, _ => { switches++; return Task.FromResult(true); });
            var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
            model.Update(Devices);
            model.Selected = Devices[1];
            Task work = TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
            window.Close();
            pending.SetResult(Devices);
            await work;
            Assert.Equal(0, switches);
            Assert.False(TestPrivateAccess.GetField<DispatcherTimer>(window, "_refreshTimer").IsEnabled);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_CancelsReadTokenAndReleasesWaitBeforeReaderCompletes(bool switching)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken readToken = default;
            int switches = 0;
            var window = new QuickDevicePickerWindow(token => { readToken = token; return pending.Task; },
                _ => { switches++; return Task.FromResult(true); }, 0);
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update(Devices);
                Task work = TestPrivateAccess.InvokeNonPublicTask(window, switching ? "SwitchAsync" : "RefreshAsync");
                window.Close();
                await work.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(readToken.IsCancellationRequested);
                Assert.False(pending.Task.IsCompleted);
                Assert.False(model.Busy);
                Assert.Equal(0, switches);
            }
            finally { pending.TrySetResult(Devices); window.Close(); }
        });
    }

    [Fact]
    public async Task Closing_SkipsReadQueuedBehindCoreAudioWork()
    {
        using var releaseWorker = new ManualResetEventSlim();
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = AudioPilot.Platform.ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            workerEntered.SetResult();
            if (!releaseWorker.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException("Test worker was not released.");
        }, TestContext.Current.CancellationToken);
        Task<IReadOnlyList<QuickDevicePickerItem>>? queuedRead = null;
        int reads = 0;
        try
        {
            await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await SharedStaDispatcherHost.RunAsync(async () =>
            {
                var window = new QuickDevicePickerWindow(token => queuedRead = AudioPilot.Platform.ComThreadingHelper.RunOnCoreAudioThreadAsync<IReadOnlyList<QuickDevicePickerItem>>(
                    () => { reads++; return Devices; }, token), _ => Task.FromResult(false), 0);
                try
                {
                    Task refresh = TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                    window.Close();
                    await refresh.WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally { window.Close(); }
            });
        }
        finally { releaseWorker.Set(); await blocker; }
        Assert.NotNull(queuedRead);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedRead);
        Assert.Equal(0, reads);
    }

    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(AppTheme.Dark, true)]
    [InlineData(AppTheme.Light, true)]
    [InlineData(AppTheme.Dark, false)]
    public async Task RepeatedOpenClose_ReleasesWindowsAndReportsResources(AppTheme theme, bool picker)
    {
        await SharedStaDispatcherHost.RunAsync(() =>
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            WindowThemeResolver.SetApplicationThemeProvider(() => theme);
            return Task.CompletedTask;
        });
        try
        {
            var references = new List<WeakReference>();
            var durations = new List<double>();
            var samples = new List<object>();
            for (int iteration = 0; iteration < 120; iteration++)
            {
                await SharedStaDispatcherHost.RunAsync(async () =>
                {
                    var timer = Stopwatch.StartNew();
                    Window window = picker
                        ? new QuickDevicePickerWindow(_ => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>(Devices), _ => Task.FromResult(false), 0)
                        : new Window { Width = 460, Height = 400, ShowInTaskbar = false };
                    try
                    {
                        window.Show();
                        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                        durations.Add(timer.Elapsed.TotalMilliseconds);
                        references.Add(new WeakReference(window));
                    }
                    finally { window.Close(); }
                    if (picker) Assert.False(TestPrivateAccess.GetField<DispatcherTimer>(window, "_refreshTimer").IsEnabled);
                });
                if ((iteration + 1) % 20 != 0) continue;
                await SharedStaDispatcherHost.RunAsync(async () =>
                    await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle));
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var process = Process.GetCurrentProcess();
                int retained = references.Count(reference => reference.IsAlive);
                samples.Add(new { Cycles = iteration + 1, ManagedBytes = GC.GetTotalMemory(false), PrivateBytes = process.PrivateMemorySize64, Handles = process.HandleCount, RetainedWindows = retained });
                Assert.Equal(0, retained);
            }
            double[] measured = [.. durations.Skip(20).Order()];
            string report = System.Text.Json.JsonSerializer.Serialize(new { Samples = samples, MedianOpenMs = measured[measured.Length / 2], P95OpenMs = measured[(int)(measured.Length * 0.95)] });
            TestContext.Current.TestOutputHelper!.WriteLine(report);
            if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"picker-lifetime-{theme}-{(picker ? "picker" : "baseline")}.json"), report);
            }
        }
        finally { WindowThemeResolver.SetApplicationThemeProvider(null); }
    }

    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(AppTheme.Dark, 2)]
    [InlineData(AppTheme.Light, 2)]
    [InlineData(AppTheme.Dark, 4)]
    [InlineData(AppTheme.Light, 4)]
    [InlineData(AppTheme.Dark, 5)]
    [InlineData(AppTheme.Light, 5)]
    public async Task Navigation_KeepsFittingRowsVisible_AndScrollsLongerLists(AppTheme theme, int count)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            QuickDevicePickerItem[] devices = [.. Enumerable.Range(0, count).Select(index =>
                new QuickDevicePickerItem($"device-{index}", $"Device {index}", null, true, true, index == 0, false))];
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>(devices), _ => Task.FromResult(false));
            WindowThemeResolver.ApplyWindowTheme(window, theme);
            try
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                for (int index = 0; index < count; index++)
                {
                    model.Selected = devices[index];
                    await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                    var row = Assert.IsType<ListBoxItem>(window.DeviceList.ItemContainerGenerator.ContainerFromIndex(index));
                    double expectedHeight = Math.Min(count, 4) * row.DesiredSize.Height
                        + window.DeviceList.Padding.Top + window.DeviceList.Padding.Bottom;
                    Assert.InRange(window.DeviceList.ActualHeight, expectedHeight - 1, expectedHeight + 1);
                    Rect bounds = row.TransformToAncestor(window.DeviceList).TransformBounds(new Rect(row.RenderSize));
                    Assert.True(bounds.Top >= -1, $"Selected row begins outside viewport: {bounds}");
                    Assert.True(bounds.Bottom <= window.DeviceList.ActualHeight + 1, $"Selected row ends outside viewport: {bounds}");
                    if (count <= 4)
                    {
                        var first = Assert.IsType<ListBoxItem>(window.DeviceList.ItemContainerGenerator.ContainerFromIndex(0));
                        Assert.True(first.TransformToAncestor(window.DeviceList).Transform(new Point()).Y >= -1);
                    }
                }
                double height = window.Height;
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                Assert.Equal(height, window.Height);
            }
            finally { window.Close(); }
        });
    }

    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_FocusesBeforeDeviceDiscovery_AndDismissesOnDeactivation(bool deactivate)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = Create(() => pending.Task, _ => Task.FromResult(false));
            var other = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
            try
            {
                other.Show();
                other.Activate();
                bool ownsForeground = QuickDevicePickerWindow.GetForegroundWindow() == new WindowInteropHelper(other).Handle;
                window.Show();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                if (ownsForeground)
                    Assert.Equal(new WindowInteropHelper(window).Handle, QuickDevicePickerWindow.GetForegroundWindow());
                Assert.True(window.DeviceList.IsKeyboardFocusWithin);
                Assert.Equal(1, window.Opacity);
                Assert.False(pending.Task.IsCompleted);
                nint pickerHandle = new WindowInteropHelper(window).Handle;
                if (deactivate) other.Activate();
                pending.SetResult(Devices);
                var completionTimeout = Stopwatch.StartNew();
                while (TestPrivateAccess.GetField<bool>(window, "_refreshing") && completionTimeout.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(10);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(!deactivate, window.IsVisible);
                if (deactivate) Assert.NotEqual(pickerHandle, QuickDevicePickerWindow.GetForegroundWindow());
                else if (ownsForeground) Assert.Equal(pickerHandle, QuickDevicePickerWindow.GetForegroundWindow());
                Assert.Equal(!deactivate, TestPrivateAccess.GetField<DispatcherTimer>(window, "_refreshTimer").IsEnabled);
            }
            finally { pending.TrySetResult(Devices); window.Close(); other.Close(); }
        });
    }

    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task KeyboardNavigation_FilterRefreshAndCurrentDeviceSelection(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            int switches = 0;
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>(Devices),
                _ => { switches++; return Task.FromResult(true); });
            WindowThemeResolver.ApplyWindowTheme(window, theme);
            try
            {
                window.Show();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(window));
                Assert.True(window.DeviceList.IsKeyboardFocusWithin);
                Assert.Equal("headset", model.Selected?.Id);
                Assert.Equal(0, switches);
                if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } captureDirectory)
                {
                    Directory.CreateDirectory(captureDirectory);
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(captureDirectory, $"device-picker-{theme}.png"));
                    encoder.Save(file);
                }
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Up)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.Equal("speakers", model.Selected?.Id);
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                Assert.Equal("speakers", window.DeviceList.SelectedValue is QuickDevicePickerItem selected ? selected.Id : null);
                var typing = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, window.DeviceList, "D"))
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                window.RaiseEvent(typing);
                Assert.True(typing.Handled);
                Assert.True(window.SearchBox.IsKeyboardFocusWithin);
                Assert.Equal("D", window.SearchBox.Text);
                Assert.Equal(1, window.SearchBox.CaretIndex);
                window.SearchBox.Text = "Desk";
                Assert.Single(model.Items);
                Assert.Equal("speakers", model.Selected?.Id);
                window.DeviceList.Focus();
                ApplicationCommands.Find.Execute(null, window);
                Assert.True(window.SearchBox.IsKeyboardFocusWithin);
                Assert.Equal("Desk", window.SearchBox.SelectedText);
                Assert.True(window.DeviceList.ActualHeight > 40);
                window.SearchBox.Text = string.Empty;
                window.DeviceTabs.SelectedIndex = 1;
                Assert.Equal("mic", model.Selected?.Id);
                Assert.True(window.DeviceList.IsKeyboardFocusWithin);
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(window.IsVisible);
                Assert.Equal(0, switches);
                Assert.False(TestPrivateAccess.GetField<DispatcherTimer>(window, "_refreshTimer").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    private static QuickDevicePickerWindow Create(Func<Task<IReadOnlyList<QuickDevicePickerItem>>> read,
        Func<QuickDevicePickerItem, Task<bool>> apply)
    {
        Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = new ResourceDictionary { Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative) };
        application.Resources.MergedDictionaries.Add(theme);
        try { return new QuickDevicePickerWindow(_ => read(), apply, 0); }
        finally { application.Resources.MergedDictionaries.Remove(theme); }
    }
}
