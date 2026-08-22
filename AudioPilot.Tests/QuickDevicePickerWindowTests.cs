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
    [Theory]
    [InlineData(AppTheme.Dark, 0)]
    [InlineData(AppTheme.Light, 0)]
    [InlineData(AppTheme.Dark, 5)]
    [InlineData(AppTheme.Light, 5)]
    public async Task DelayedDiscovery_FitsFourRowsWithoutResizingOnUnchangedRefresh(AppTheme theme, int currentIndex)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            QuickDevicePickerItem[] devices = [.. Enumerable.Range(0, 7).Select(index =>
                new QuickDevicePickerItem($"device-{index}", $"Device {index}", null, true, true, index == currentIndex, false))];
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = Create(() => pending.Task, _ => Task.FromResult(false), showAllDevices: true);
            WindowThemeResolver.ApplyWindowTheme(window, theme);
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, "OnLoaded");
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                Task refresh = TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                pending.SetResult(devices);
                await refresh;
                double firstHeight = window.Height;
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                Assert.Equal(firstHeight, window.Height);
                window.UpdateLayout();
                var selected = Assert.IsType<ListBoxItem>(window.DeviceList.ItemContainerGenerator.ContainerFromItem(window.DeviceList.SelectedItem));
                double expectedHeight = 4 * selected.DesiredSize.Height + window.DeviceList.Padding.Top + window.DeviceList.Padding.Bottom;
                Assert.InRange(window.DeviceList.ActualHeight, expectedHeight - 1, expectedHeight + 1);
            }
            finally { pending.TrySetResult(devices); window.Close(); }
        });
    }

    [Fact]
    public async Task TabChange_SizesCachedDevicesBeforeFreshDiscoveryCompletes()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IReadOnlyList<QuickDevicePickerItem>> read = Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>(Devices);
            var window = Create(() => read, _ => Task.FromResult(false));
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, "OnLoaded");
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                double outputHeight = window.Height;
                read = pending.Task;
                TestPrivateAccess.SetField(window, "_ready", true);
                window.DeviceTabs.SelectedIndex = 1;
                Assert.True(TestPrivateAccess.GetField<bool>(window, "_refreshing"));
                Assert.True(window.Height < outputHeight);
                window.UpdateLayout();
                var row = Assert.IsType<ListBoxItem>(window.DeviceList.ItemContainerGenerator.ContainerFromIndex(0));
                double expectedHeight = row.DesiredSize.Height + window.DeviceList.Padding.Top + window.DeviceList.Padding.Bottom;
                Assert.InRange(window.DeviceList.ActualHeight, expectedHeight - 1, expectedHeight + 1);
            }
            finally { window.Close(); pending.TrySetResult(Devices); }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PartialDiscovery_KeepsHealthyDirectionUsableAndRecoversFailedDirection(bool failedOutput, bool showAll)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var error = new IOException("Driver failed");
            bool fail = true;
            int switches = 0;
            var window = new QuickDevicePickerWindow(_ => Task.FromResult(QuickDevicePickerSnapshot.Capture(output =>
                output == failedOutput && fail ? throw error : [Devices[output ? 0 : 3] with { Current = false }], CancellationToken.None)),
                _ => { switches++; return Task.FromResult(false); }, 0, failedOutput, showAll);
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                Assert.Empty(model.Items);
                Assert.Contains("Could not read", model.Message);
                model.Output = !failedOutput;
                Assert.Single(model.Items);
                await TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                Assert.Equal(1, switches);
                fail = false;
                await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                model.Output = failedOutput;
                Assert.Single(model.Items);
                Assert.True(model.CanSwitch);
                Assert.Empty(model.Message);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switch_RejectsFailedVerificationOfSelectedDirection(bool output)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            int switches = 0;
            var error = new IOException("Driver failed");
            var window = new QuickDevicePickerWindow(_ => Task.FromResult(new QuickDevicePickerSnapshot(Devices,
                output ? error : null, output ? null : error)), _ => { switches++; return Task.FromResult(true); }, 0, output);
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update([Devices[output ? 0 : 3] with { Current = false }]);
                await TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                Assert.Equal(0, switches);
                Assert.Contains("Could not verify", model.Message);
                Assert.False(model.Busy);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 460)]
    [InlineData(AppTheme.Light, 460)]
    [InlineData(AppTheme.Dark, 360)]
    [InlineData(AppTheme.Light, 360)]
    public void Header_KeepsCheckboxBesideTabsAndAboveDividerWithoutOverlap(AppTheme theme, double width)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([]), _ => Task.FromResult(false));
            try
            {
                WindowThemeResolver.ApplyWindowTheme(window, theme);
                Assert.IsType<QuickDevicePickerViewModel>(window.DataContext).Update(Devices);
                var content = Assert.IsType<Border>(window.Content);
                content.Measure(new Size(width, 400));
                content.Arrange(new Rect(0, 0, width, 400));
                content.UpdateLayout();
                WpfVisualAssertions.NoBindingErrors(content);
                Rect tabs = window.DeviceTabs.TransformToAncestor(window.DeviceHeaderGrid).TransformBounds(new Rect(window.DeviceTabs.RenderSize));
                Rect check = window.ShowAllDevicesCheckBox.TransformToAncestor(window.DeviceHeaderGrid).TransformBounds(new Rect(window.ShowAllDevicesCheckBox.RenderSize));
                Rect divider = window.TabDivider.TransformToAncestor(window.DeviceHeaderGrid).TransformBounds(new Rect(window.TabDivider.RenderSize));
                Assert.True(tabs.Right + 8 <= check.Left, $"Tabs overlap checkbox: {tabs}, {check}");
                Assert.InRange(check.Right, 1, window.DeviceHeaderGrid.ActualWidth + 1);
                Assert.InRange(Math.Abs((tabs.Top + tabs.Bottom - check.Top - check.Bottom) / 2), 0, 1);
                Assert.True(Math.Max(tabs.Bottom, check.Bottom) < divider.Top);
                Assert.Equal(window.DeviceHeaderGrid.ActualWidth, window.TabDivider.ActualWidth, precision: 1);
                if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)width, 400, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"picker-header-{theme}-{width}.png"));
                    encoder.Save(file);
                }
            }
            finally { window.Close(); }
        });
    }

    private static readonly QuickDevicePickerItem[] Devices =
    [
        new("speakers", "Desk speakers", null, true, true, true, false, AudioEndpointFormFactor.Speakers),
        new("headset", "Bluetooth headphones with a very long device name", null, true, false, false, true, AudioEndpointFormFactor.Headphones),
        new("monitor", "Display audio", null, true, false, false, false, AudioEndpointFormFactor.DigitalAudioDisplayDevice),
        new("mic", "USB microphone", null, false, true, true, false, AudioEndpointFormFactor.Microphone),
    ];

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ScopeCheckbox_RestoresPreferenceUpdatesBothViewsAndReportsSaveFailure(bool initialShowAll, bool saveSucceeds)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            List<bool> saved = [];
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([]), _ => Task.FromResult(false),
                showAllDevices: initialShowAll, saveViewPreference: value => { saved.Add(value); return Task.FromResult(saveSucceeds); });
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update([Devices[0], Devices[3] with { Configured = false }]);
                Assert.Equal(initialShowAll, model.ShowAllDevices);
                TestPrivateAccess.RunTaskOnDispatcher(window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task);
                Assert.Equal(initialShowAll, window.ShowAllDevicesCheckBox.IsChecked);
                Assert.Empty(saved);
                TestPrivateAccess.SetField(window, "_ready", true);
                window.ShowAllDevicesCheckBox.IsChecked = !initialShowAll;
                Assert.Equal(!initialShowAll, model.ShowAllDevices);
                Assert.Equal([!initialShowAll], saved);
                Assert.Equal("speakers", model.Selected?.Id);
                model.Output = false;
                Assert.Equal(!initialShowAll ? 1 : 0, model.Items.Count);
                if (!saveSucceeds)
                {
                    model.Output = true;
                    window.ShowAllDevicesCheckBox.IsChecked = initialShowAll;
                    Assert.Contains("could not be saved", model.Message);
                }
            }
            finally { window.Close(); }
        });
    }

    [VisualIntegrationTheory]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opening_UsesRequestedFlowAndHighlightsNextEligibleDevice(bool output)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var secondMic = Devices[3] with { Id = "second-mic", Current = false };
            QuickDevicePickerItem[] snapshot = [.. Devices, secondMic];
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = Create(() => pending.Task,
                _ => Task.FromResult(false), output);
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                TestWindowFactory.ShowWindowForTest(window);
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(output ? 0 : 1, window.DeviceTabs.SelectedIndex);
                Assert.Equal("Loading devices…", model.Message);
                Assert.True(window.SearchBox.IsKeyboardFocusWithin);
                pending.SetResult(snapshot);
                await TestExecutionGuards.WaitUntilAsync(() => model.Items.Count > 0, "Initial device discovery did not complete.");
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.All(model.Items, item => Assert.Equal(output, item.Output));
                Assert.Equal(output ? "headset" : "second-mic", model.Selected?.Id);
                Assert.True(window.DeviceList.IsKeyboardFocusWithin);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancel")]
    [InlineData("current")]
    [InlineData("closed-during-switch")]
    public async Task RememberedFlow_ChangesOnlyAfterConfirmedSwitch(string outcome)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            bool rememberedOutput = true;
            bool current = outcome == "current";
            var target = Devices[3] with { Current = current };
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([target]),
                _ => outcome switch
                {
                    "closed-during-switch" => pending.Task,
                    "exception" => Task.FromException<bool>(new IOException("Switch failed")),
                    _ => Task.FromResult(outcome == "success"),
                }, output: false);
            window.DeviceSwitched += output => rememberedOutput = output;
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update([target]);
                if (outcome == "cancel") window.Dismiss();
                else
                {
                    Task switching = TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                    if (outcome == "closed-during-switch")
                    {
                        window.Close();
                        pending.SetResult(true);
                    }
                    await switching;
                }
                bool switched = outcome is "success" or "closed-during-switch";
                Assert.Equal(!switched, rememberedOutput);
                Assert.False(AppViewModel.ResolveQuickDevicePickerOutput(true, 1, rememberedOutput));
                Assert.True(AppViewModel.ResolveQuickDevicePickerOutput(true, 0, rememberedOutput));
                Assert.Equal(rememberedOutput, AppViewModel.ResolveQuickDevicePickerOutput(false, 1, rememberedOutput));
                Assert.Equal(rememberedOutput, AppViewModel.ResolveQuickDevicePickerOutput(true, 2, rememberedOutput));
                Assert.Equal(rememberedOutput, AppViewModel.ResolveQuickDevicePickerOutput(true, 3, rememberedOutput));
            }
            finally { window.Close(); }
        });
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switch_RejectsDeviceThatLeavesTheSelectedScope(bool showAllDevices)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var selected = Devices[0] with { Current = false };
            var refreshed = selected with { Configured = showAllDevices, Available = !showAllDevices, CanReconnect = true };
            int switches = 0;
            var window = Create(() => Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([refreshed]),
                _ => { switches++; return Task.FromResult(true); }, showAllDevices: showAllDevices);
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update([selected]);
                await TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                Assert.Equal(0, switches);
                Assert.False(model.Busy);
                Assert.Contains("no longer available in this view", model.Message);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderRefresh_CannotOverwriteNewerSwitchSnapshotOrFeedback(bool oldRefreshFails)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<QuickDevicePickerItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fresh = Devices[0] with { Name = "Current name", Current = false };
            int reads = 0;
            var window = Create(() => ++reads == 1 ? pending.Task : Task.FromResult<IReadOnlyList<QuickDevicePickerItem>>([fresh]),
                _ => Task.FromResult(false));
            try
            {
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                model.Update([fresh]);
                Task refresh = TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
                await TestPrivateAccess.InvokeNonPublicTask(window, "SwitchAsync");
                string feedback = model.Message;
                if (oldRefreshFails) pending.SetException(new IOException("Old inventory failed"));
                else pending.SetResult([fresh with { Name = "Stale name" }]);
                await refresh;
                Assert.Equal("Current name", Assert.Single(model.Items).Name);
                Assert.Equal(feedback, model.Message);
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
            var pending = new TaskCompletionSource<QuickDevicePickerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            finally { pending.TrySetResult(new(Devices)); window.Close(); }
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
        Task<QuickDevicePickerSnapshot>? queuedRead = null;
        int reads = 0;
        try
        {
            await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await SharedStaDispatcherHost.RunAsync(async () =>
            {
                var window = new QuickDevicePickerWindow(token => queuedRead = AudioPilot.Platform.ComThreadingHelper.RunOnCoreAudioThreadAsync(
                    () => { reads++; return new QuickDevicePickerSnapshot(Devices); }, token), _ => Task.FromResult(false), 0);
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
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task RepeatedOpenClose_ReleasesPickerWindowsAndReportsResources(AppTheme theme)
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
                    var window = new QuickDevicePickerWindow(_ => Task.FromResult(new QuickDevicePickerSnapshot(Devices)), _ => Task.FromResult(false), 0);
                    try
                    {
                        window.Show();
                        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                        durations.Add(timer.Elapsed.TotalMilliseconds);
                        references.Add(new WeakReference(window));
                    }
                    finally { window.Close(); }
                    Assert.False(TestPrivateAccess.GetField<DispatcherTimer>(window, "_refreshTimer").IsEnabled);
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
                Assert.True(retained == 0 || (retained == 1 && references[^1].IsAlive),
                    $"Older picker windows remain after {iteration + 1} cycles; retained={retained}.");
            }
            double[] measured = [.. durations.Skip(20).Order()];
            string report = System.Text.Json.JsonSerializer.Serialize(new { Samples = samples, MedianOpenMs = measured[measured.Length / 2], P95OpenMs = measured[(int)(measured.Length * 0.95)] });
            TestContext.Current.TestOutputHelper!.WriteLine(report);
            if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"picker-lifetime-{theme}.json"), report);
            }
        }
        finally { WindowThemeResolver.SetApplicationThemeProvider(null); }
    }

    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(AppTheme.Dark, 0)]
    [InlineData(AppTheme.Light, 0)]
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
                WpfVisualAssertions.NoBindingErrors(window);
                var selectedTab = Assert.IsType<TabItem>(window.DeviceTabs.SelectedItem);
                var outline = Assert.IsType<Border>(selectedTab.Template.FindName("Bd", selectedTab));
                Border divider = window.TabDivider;
                Rect outlineBounds = outline.TransformToAncestor(window.DeviceHeaderGrid).TransformBounds(new Rect(outline.RenderSize));
                Rect dividerBounds = divider.TransformToAncestor(window.DeviceHeaderGrid).TransformBounds(new Rect(divider.RenderSize));
                Assert.InRange(outlineBounds.Top, 0, window.DeviceHeaderGrid.ActualHeight);
                Assert.InRange(outlineBounds.Bottom, 1, window.DeviceHeaderGrid.ActualHeight);
                Assert.True(outlineBounds.Bottom < dividerBounds.Top, $"Tab outline touches divider: {outlineBounds}, {dividerBounds}");
                var model = Assert.IsType<QuickDevicePickerViewModel>(window.DataContext);
                Assert.Equal(count > 0, window.DeviceList.Focusable);
                Assert.Equal(count > 0, window.DeviceList.IsTabStop);
                Assert.Null(window.DeviceList.FocusVisualStyle);
                if (count == 0)
                {
                    Assert.True(window.SearchBox.IsKeyboardFocusWithin);
                    Assert.False(window.DeviceList.Focus());
                    window.SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Assert.False(window.DeviceList.IsKeyboardFocusWithin);
                    Assert.InRange(window.DeviceList.ActualHeight, 0, 1);
                    if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } captureDirectory)
                    {
                        Directory.CreateDirectory(captureDirectory);
                        DpiScale dpi = VisualTreeHelper.GetDpi(window);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(captureDirectory, $"device-picker-empty-{theme}.png"));
                        encoder.Save(file);
                    }
                }
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
                Assert.True(window.SearchBox.IsKeyboardFocusWithin);
                Assert.False(window.DeviceList.Focusable);
                Assert.Equal(1, window.Opacity);
                Assert.False(pending.Task.IsCompleted);
                nint pickerHandle = new WindowInteropHelper(window).Handle;
                if (deactivate) other.Activate();
                pending.SetResult(Devices);
                await TestExecutionGuards.WaitUntilAsync(() => !TestPrivateAccess.GetField<bool>(window, "_refreshing"),
                    "The picker device refresh did not finish.", TimeSpan.FromSeconds(10));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(!deactivate, window.IsVisible);
                if (!deactivate) Assert.True(window.DeviceList.IsKeyboardFocusWithin);
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
                WpfVisualAssertions.NoBindingErrors(window);
                if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } captureDirectory)
                {
                    Directory.CreateDirectory(captureDirectory);
                    DpiScale dpi = VisualTreeHelper.GetDpi(window);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                        (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
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
        Func<QuickDevicePickerItem, Task<bool>> apply, bool output = true, bool showAllDevices = false,
        Func<bool, Task<bool>>? saveViewPreference = null)
    {
        Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = new ResourceDictionary { Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative) };
        application.Resources.MergedDictionaries.Add(theme);
        try { return new QuickDevicePickerWindow(async _ => new QuickDevicePickerSnapshot(await read()), apply, 0, output, showAllDevices, saveViewPreference); }
        finally { application.Resources.MergedDictionaries.Remove(theme); }
    }
}
