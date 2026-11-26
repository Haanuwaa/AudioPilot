using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Coordinators;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Tests.Windows;

[Collection("WpfApplicationIsolation")]
public sealed class QuickMixerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedFlyout_LosingForegroundDuringLoadClosesBeforeReveal(bool mixer)
    {
        using var workspace = new TestSettingsWorkspace(nameof(GuardedFlyout_LosingForegroundDuringLoadClosesBeforeReveal));
        using var logger = Logger.CreateInMemoryForTests();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new QuickPanelLayoutStore(Path.Combine(workspace.PrimaryDir, QuickPanelLayoutStore.FileName), logger);
        TestPrivateAccess.SetField(store, "_load", loaded.Task);
        bool originalForeground = true;
        bool CanPresent() => originalForeground;
        await WithWindow(async (mixerWindow, _, locale) =>
        {
            Window window = mixer ? mixerWindow : new QuickDevicePickerWindow(
                _ => throw new InvalidOperationException("The stale flyout must not read devices."),
                _ => Task.FromResult(false), 0, localization: locale, layouts: store, canPresent: CanPresent);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            try
            {
                Assert.False(window.ShowActivated);
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.False(closed.Task.IsCompleted);
                originalForeground = false;
                loaded.SetResult();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(0d, window.Opacity);
            }
            finally
            {
                loaded.TrySetResult();
                if (!closed.Task.IsCompleted) window.Close();
            }
        }, layouts: store, canPresent: CanPresent);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ResetPreferences_RequiresConfirmationAndPreservesSavedGeometry(bool pinned, bool confirm)
    {
        using var workspace = new TestSettingsWorkspace(nameof(QuickMixerTests));
        using var logger = Logger.CreateInMemoryForTests();
        string path = Path.Combine(workspace.PrimaryDir, QuickPanelLayoutStore.FileName);
        var store = new QuickPanelLayoutStore(path, logger);
        var originalLayout = new QuickPanelLayout(pinned, true, new(100, 100, 560, 500, 96, false));
        var pickerLayout = new QuickPanelLayout(true, false, null);
        string pin = new('A', 64);
        var originalPreferences = new QuickMixerPreferences(true, [pin]);
        await store.SaveAsync(QuickPanelKind.DevicePicker, pickerLayout);
        await store.SaveMixerPreferencesAsync(originalPreferences, originalLayout);
        var decision = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new RecordingAppDialogService { PendingConfirmation = decision.Task };

        await WithWindow(async (window, mixer, locale) =>
        {
            var controller = TestPrivateAccess.GetField<QuickPanelWindowController>(window, "_layout");
            await controller.PrepareAsync();
            TestPrivateAccess.SetField(window, "_ready", true);
            TestPrivateAccess.SetField(window, "_preferences", originalPreferences);
            TestPrivateAccess.InvokeNonPublic(window, "UpdateSortMenu");
            var app = new AudioSessionItem("Player", 40, false, false, processId: 42) { MixerPinKey = pin };
            await AddAsync(mixer, app);
            using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher, originalPreferences);
            TestPrivateAccess.GetField<Dictionary<AudioMixerMode, QuickMixerPage>>(window, "_pages").Add(AudioMixerMode.Output, page);
            BindPage(window, page);

            Task reset = TestPrivateAccess.InvokeNonPublicTask(window, "ResetMixerPreferencesAsync");
            TestPrivateAccess.InvokeNonPublic(window, "OnWindowDeactivated", null, EventArgs.Empty);
            Assert.False(TestPrivateAccess.GetField<bool>(window, "_closed"));
            Assert.True(Assert.Single(page.Rows).IsPinnedApp);
            Assert.False(reset.IsCompleted);
            decision.SetResult(confirm ? AppDialogResult.Confirmed : AppDialogResult.Declined);
            await reset;

            var request = Assert.Single(dialogs.Requests);
            Assert.Same(window, request.Owner);
            Assert.Equal(AppDialogResult.Declined, Assert.Single(request.Actions, action => action.IsDefault).Result);
            Assert.Equal("Reset preferences?", request.Localize(locale).Caption);
            Assert.Equal(pinned && !confirm, window.IsPinned);
            Assert.Equal(!confirm, window.AlwaysOnTopItem.IsChecked);
            Assert.Equal(confirm, window.SortByNameItem.IsChecked);
            Assert.Equal(!confirm, Assert.Single(page.Rows).IsPinnedApp);
            Assert.Equal(40, app.Volume);
        }, layouts: store, dialogs: dialogs);

        await store.FlushAsync();
        var reopened = new QuickPanelLayoutStore(path, logger);
        CancellationToken token = TestContext.Current.CancellationToken;
        var savedLayout = await reopened.LoadAsync(QuickPanelKind.Mixer, token);
        Assert.Equal(confirm ? originalLayout with { Pinned = false, AlwaysOnTop = false } : originalLayout, savedLayout);
        Assert.Equal(pickerLayout, await reopened.LoadAsync(QuickPanelKind.DevicePicker, token));
        var savedPreferences = await reopened.LoadMixerPreferencesAsync(token);
        Assert.Equal(!confirm, savedPreferences.PlayingFirst);
        if (confirm) Assert.Empty(savedPreferences.PinnedApps);
        else Assert.Equal(pin, Assert.Single(savedPreferences.PinnedApps));
    }

    [Fact]
    public Task SessionReplacementKeepsPlaybackOrderUntilGraceExpires_AndIgnoresOldEndpointSamples() => WithWindow(async (window, mixer, _) =>
    {
        using var clock = new ManualTimeProvider();
        var idle = new AudioSessionItem("Alpha", 50, false, false, processId: 41);
        var playing = new AudioSessionItem("Zed", 50, false, false, processId: 42, sessionInstanceId: "old", endpointId: "out");
        await AddAsync(mixer, idle, playing);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher, new(true, []), timeProvider: clock);
        BindPage(window, page);
        var row = page.Rows[^1];
        Observe("old", "out");
        Assert.Same(row, page.Rows[0]);

        playing.UpdateRoutingMetadataFromSystem("player", "new", "replacement-out");
        clock.Advance(TimeSpan.FromSeconds(1));
        Observe("old", "out");
        Assert.Same(row, page.Rows[0]);
        clock.Advance(TimeSpan.FromSeconds(1));
        Observe("old", "out");
        Assert.Same(row, page.Rows[^1]);
        Observe("new", "replacement-out");
        Assert.Same(row, page.Rows[0]);

        void Observe(string session, string endpoint)
        {
            var reading = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, endpoint, session, playing.ProcessId), .4f);
            page.UpdatePlaybackOrder(new(1, clock.GetTimestamp(), new([reading], AudioMeterModes.Output), TimeSpan.Zero, 0, false));
            page.ApplyPendingOrder();
        }
    });

    [Fact]
    public Task PlayingFirst_TracksOffscreenAudioAndDefersMovesWithoutLosingSelection() => WithWindow(async (window, mixer, locale) =>
    {
        using var clock = new ManualTimeProvider();
        var output = new AudioSessionItem("Master Volume", 80, isMaster: true, isMic: false);
        var sounds = new AudioSessionItem("System Sounds", 50, false, false, isSystemSounds: true);
        var apps = Enumerable.Range(1, 64).Select(index => new AudioSessionItem($"App {index:D2}", 50, false, false,
            processId: (uint)index, sessionInstanceId: $"session-{index}", endpointId: "output")).ToArray();
        await AddAsync(mixer, [output, sounds, .. apps]);
        DependencyObject? pointer = null, focus = null;
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher,
            isOrderHeld: () => TestPrivateAccess.InvokeNonPublic<bool>(window, "IsOrderHeldForInput", pointer, focus), timeProvider: clock);
        TestPrivateAccess.GetField<Dictionary<AudioMixerMode, QuickMixerPage>>(window, "_pages").Add(AudioMixerMode.Output, page);
        BindPage(window, page);
        var content = (FrameworkElement)window.Content;
        Layout(content);
        var player = page.Rows[^1];
        Assert.Null(window.SessionList.ItemContainerGenerator.ContainerFromItem(player));
        Assert.True(window.SortByNameItem.IsChecked);
        LocalizationContext.SetService(window.MixerOptionsMenu, locale);
        Assert.Equal("Sort apps · Name (A–Z)", window.SortAppsMenu.Header);
        var selected = page.Rows[3];
        window.SessionList.SelectedItem = selected;

        window.SortPlayingFirstItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(window.SortPlayingFirstItem.IsChecked);
        Assert.False(window.SortByNameItem.IsChecked);
        Assert.Equal("Sort apps · Playing first", window.SortAppsMenu.Header);
        locale.SetLanguage("es");
        Assert.Equal("Ordenar apps · En reproducción primero", window.SortAppsMenu.Header);
        locale.SetLanguage("en");
        var source = new AudioMeterSource(AudioMixerMode.Output, "output", apps[^1].SessionInstanceId, apps[^1].ProcessId);
        AudioMeterSnapshot Sample(float peak, bool muted = false) => new(1, clock.GetTimestamp(),
            new([AudioMeterReading.FromPeak(source, peak, .5f, muted)], AudioMeterModes.Output), TimeSpan.Zero, 0, false);
        page.UpdatePlaybackOrder(Sample(.8f, muted: true));
        page.ApplyPendingOrder();
        Assert.Same(player, page.Rows[^1]);

        int moves = 0;
        page.Rows.CollectionChanged += (_, change) => { if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) moves++; };
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new KeyboardSource(), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(key);
        page.UpdatePlaybackOrder(Sample(.8f));
        page.ApplyPendingOrder();
        Assert.False(key.Handled);
        Assert.Same(player, page.Rows[^1]);
        TestPrivateAccess.SetField(window, "_holdOrderUntil", 0L);
        var selectedContainer = Assert.IsType<ListBoxItem>(window.SessionList.ItemContainerGenerator.ContainerFromItem(selected));
        pointer = Assert.Single(WpfVisualTree.Descendants(selectedContainer).OfType<Slider>());
        page.ApplyPendingOrder();
        Assert.Same(player, page.Rows[^1]);
        focus = pointer;
        pointer = null;
        page.ApplyPendingOrder();
        Assert.Same(player, page.Rows[^1]);
        focus = null;
        page.ApplyPendingOrder();
        Assert.Equal([output, sounds, player.Source], page.Rows.Take(3).Select(x => x.Source));
        Assert.Equal(1, moves);

        page.ToggleSelectionMode();
        clock.Advance(TimeSpan.FromSeconds(1));
        page.UpdatePlaybackOrder(Sample(0));
        page.ApplyPendingOrder();
        Assert.Same(player, page.Rows[2]);
        clock.Advance(TimeSpan.FromSeconds(1));
        page.UpdatePlaybackOrder(Sample(0));
        page.ApplyPendingOrder();
        Assert.Same(player, page.Rows[2]);
        page.ClearSelection();
        page.ApplyPendingOrder();
        Layout(content);
        Assert.Same(player, page.Rows[^1]);
        Assert.Same(selected, window.SessionList.SelectedItem);
        Assert.Equal(2, moves);
        Assert.InRange(WpfVisualTree.Descendants(window.SessionList).OfType<ListBoxItem>().Count(), 1, 10);

        page.UpdatePlaybackOrder(Sample(.8f));
        page.ApplyPendingOrder();
        Assert.Same(player, page.Rows[2]);
        window.SortByNameItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        page.ApplyPendingOrder();
        Assert.True(window.SortByNameItem.IsChecked);
        Assert.Equal("Sort apps · Name (A–Z)", window.SortAppsMenu.Header);
        Assert.Same(player, page.Rows[^1]);
    });

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public Task PinMenu_UsesAppIdentityAndKeepsRowSelection(AudioMixerMode mode) => WithWindow(async (window, mixer, locale) =>
    {
        string key = new('A', 64);
        var first = new AudioSessionItem("Alpha", 50, false, false, processId: 42);
        var pinned = new AudioSessionItem("Zed", 50, false, false, processId: 43) { MixerPinKey = key };
        var sameApp = new AudioSessionItem("Zed (second process)", 50, false, false, processId: 44) { MixerPinKey = key };
        await AddAsync(mixer, first, pinned, sameApp);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        TestPrivateAccess.GetField<Dictionary<AudioMixerMode, QuickMixerPage>>(window, "_pages").Add(mode, page);
        BindPage(window, page);
        Layout((FrameworkElement)window.Content);
        Assert.Equal(mode == AudioMixerMode.Output ? Visibility.Visible : Visibility.Collapsed, window.SortAppsMenu.Visibility);
        var target = page.Rows.Single(row => ReferenceEquals(row.Source, pinned));
        window.SessionList.SelectedItem = target;
        var container = Assert.IsType<ListBoxItem>(window.SessionList.ItemContainerGenerator.ContainerFromItem(target));

        OpenFromKeyboard(container);
        Assert.Equal(locale["QuickMixer_PinApp"], window.PinAppItem.Header);
        window.PinAppItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        page.ApplyPendingOrder();
        Layout((FrameworkElement)window.Content);
        Assert.Equal([pinned, sameApp, first], page.Rows.Select(row => row.Source));
        Assert.Same(target, window.SessionList.SelectedItem);
        Assert.Equal(locale["QuickMixer_AppPinned"], AutomationProperties.GetItemStatus(
            Assert.IsType<ListBoxItem>(window.SessionList.ItemContainerGenerator.ContainerFromItem(target))));

        locale.SetLanguage("es");
        OpenFromKeyboard(window.SessionList);
        Assert.Equal(locale["QuickMixer_UnpinApp"], window.PinAppItem.Header);
        window.PinAppItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        page.ApplyPendingOrder();
        Assert.Same(first, page.Rows[0].Source);
        Assert.All(page.Rows, row => Assert.False(row.IsPinnedApp));

        static void OpenFromKeyboard(FrameworkElement origin)
        {
            var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                [origin, true, -1d, -1d], null)!;
            origin.RaiseEvent(opening);
            Assert.False(opening.Handled);
        }
    }, mode: mode);

    [Fact]
    public Task IconBackpressureWaitsBrieflyButANewSessionCanRequestImmediately() => SharedStaDispatcherHost.RunAsync(() =>
    {
        using var fixture = new MixerTestFixture();
        using var clock = new ManualTimeProvider();
        var app = new AudioSessionItem("Player", 50, false, false, processId: 42, sessionInstanceId: "one", endpointId: "out");
        using var row = new QuickMixerRow(fixture.Mixer, app, clock);
        Assert.True(row.CanRequestIcon);
        row.IconRequested = true;
        row.DeferIconRequest();
        Assert.False(row.CanRequestIcon);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(row.CanRequestIcon);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(row.CanRequestIcon);
        row.IconRequested = true;
        row.DeferIconRequest();
        app.UpdateRoutingMetadataFromSystem("player", "replacement", "other-out");
        Assert.True(row.CanRequestIcon);
        row.Dispose();
        Assert.False(row.CanRequestIcon);
        return Task.CompletedTask;
    });

    [Fact]
    public Task VirtualizingRows_ReleasesIconsAndCancelsPendingAssignments() => WithWindow(async (window, mixer, _) =>
    {
        await AddAsync(mixer, [.. Enumerable.Range(1, 64).Select(index => new AudioSessionItem($"Application {index:D3}", 30, false, false,
            processId: (uint)index, sessionInstanceId: $"session-{index}", endpointId: "output"))]);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        window.SessionList.ItemsSource = page.Rows;
        var root = (FrameworkElement)window.Content;
        Layout(root);
        var scroll = WpfVisualTree.Descendants(window.SessionList).OfType<ScrollViewer>().First();
        var visibleRows = WpfVisualTree.Descendants(window.SessionList).OfType<ListBoxItem>()
            .Select(container => Assert.IsType<QuickMixerRow>(container.Content)).Take(2).ToArray();
        Assert.Equal(2, visibleRows.Length);
        var first = visibleRows[0];
        var second = visibleRows[1];
        var icon = new DrawingImage();
        icon.Freeze();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int reads = 0;
        using var provider = new MixerAppIconProvider((pid, _) =>
        {
            Interlocked.Increment(ref reads);
            if (pid == second.Source.ProcessId)
            {
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Icon read was not released.");
            }
            return icon;
        });
        TestPrivateAccess.SetField(window, "_icons", provider);
        var token = TestContext.Current.CancellationToken;
        try
        {
            await TestPrivateAccess.InvokeNonPublicTask(window, "LoadIconAsync", first);
            Assert.Same(icon, first.Icon);
            Task waiting = TestPrivateAccess.InvokeNonPublicTask(window, "LoadIconAsync", second);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var nativeRequest = provider.GetAsync(second.Source.ProcessId!.Value, second.Source.SessionInstanceId, null);
            Assert.NotNull(nativeRequest);

            scroll.ScrollToEnd(); Layout(root);
            Assert.Null(first.Icon);
            Assert.True(first.CanRequestIcon);
            await waiting.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(second.CanRequestIcon);
            Assert.False(nativeRequest.IsCompleted);
            release.Set();
            Assert.Same(icon, await nativeRequest.WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Null(second.Icon);

            scroll.ScrollToTop(); Layout(root);
            await TestPrivateAccess.InvokeNonPublicTask(window, "LoadIconAsync", first);
            await TestPrivateAccess.InvokeNonPublicTask(window, "LoadIconAsync", second);
            Assert.Same(icon, first.Icon);
            Assert.Same(icon, second.Icon);
            Assert.Equal(2, reads);
        }
        finally { release.Set(); }
    });

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task BothPanelMenusUseTheAppThemeAndShowTheirCheckedState(AppTheme theme) => WithWindow(async (mixer, _, locale) =>
    {
        var picker = new QuickDevicePickerWindow(_ => Task.FromResult(new QuickDevicePickerSnapshot([])), _ => Task.FromResult(false), 0, localization: locale);
        try
        {
            WindowThemeResolver.ApplyWindowTheme(picker, theme);
            foreach (var (window, button, pin) in new (Window, Button, ToggleButton)[]
                { (mixer, mixer.WindowOptionsButton, mixer.KeepOpenToggle), (picker, picker.WindowOptionsButton, picker.KeepOpenToggle) })
            {
                var controller = TestPrivateAccess.GetField<QuickPanelWindowController>(window, "_layout");
                await controller.PrepareAsync();
                pin.IsChecked = true;
                var menu = Assert.IsType<ContextMenu>(button.ContextMenu);
                menu.PlacementTarget = button;
                LocalizationContext.SetService(menu, locale);
                LayoutMenu();
                Assert.True(menu.OverridesDefaultStyle);
                Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("PanelBackgroundBrush")).Color,
                    Assert.IsType<SolidColorBrush>(menu.Background).Color);
                var item = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Name == "AlwaysOnTopItem");
                Assert.True(item.OverridesDefaultStyle);
                var icon = Assert.IsType<FrameworkElement>(item.Icon, exactMatch: false);
                Assert.Equal("Always on top · Off", item.Header);
                Assert.False(window.Topmost);
                TestPrivateAccess.InvokeNonPublic(item, "OnClick");
                await menu.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                LayoutMenu();
                await menu.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.DataBind);
                Assert.True(item.IsChecked);
                Assert.True(window.Topmost);
                Assert.Equal("Always on top · On", item.Header);
                Assert.Equal(Visibility.Visible, icon.Visibility);
                if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(menu);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"{window.GetType().Name}-options-{theme}.png"));
                    encoder.Save(file);
                }
                controller.ToggleTopmost();
                await menu.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.DataBind);
                menu.UpdateLayout();
                Assert.False(item.IsChecked);
                Assert.False(window.Topmost);
                Assert.Equal("Always on top · Off", item.Header);
                Assert.Equal(Visibility.Collapsed, icon.Visibility);
                locale.SetLanguage("es");
                Assert.Equal("Siempre encima · Desactivado", item.Header);
                Assert.Equal("Siempre encima", AutomationProperties.GetName(item));
                Assert.Equal("Selector de dispositivos", picker.Title);
                locale.SetLanguage("en");
                Assert.Equal("Device picker", picker.Title);

                void LayoutMenu()
                {
                    menu.Measure(new Size(320, double.PositiveInfinity));
                    menu.Arrange(new Rect(menu.DesiredSize));
                    menu.UpdateLayout();
                }
            }
        }
        finally { picker.Close(); }
    }, theme);

    [Fact]
    public Task ShellMixerAction_ReusesFlyoutWithoutShowingTheMainWindow() => WithWindow(async (window, _, _) =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(QuickMixerTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        var host = (AppRuntimeHost)RuntimeHelpers.GetUninitializedObject(typeof(AppRuntimeHost));
        var originalLifetime = TestPrivateAccess.GetField<CancellationTokenSource>(harness.ViewModel, "_backgroundWorkCts");
        using var lifetime = new CancellationTokenSource();
        TestPrivateAccess.SetField(harness.ViewModel, "_backgroundWorkCts", lifetime);
        TestPrivateAccess.SetField(host, "_appVm", harness.ViewModel);
        TestPrivateAccess.SetField(harness.ViewModel, "_quickMixerWindow", window);
        try
        {
            Assert.True(await App.ExecuteShellActionAsync(host, Dispatcher.CurrentDispatcher, ShellAction.VolumeMixer, TestContext.Current.CancellationToken));
            Assert.Same(window, TestPrivateAccess.GetField(harness.ViewModel, "_quickMixerWindow"));
            harness.ViewModel.ToggleQuickMixer();
            Assert.False(window.IsMeterPolling);
        }
        finally
        {
            TestPrivateAccess.SetField<QuickMixerWindow?>(harness.ViewModel, "_quickMixerWindow", null);
            TestPrivateAccess.SetField(harness.ViewModel, "_backgroundWorkCts", originalLifetime);
        }
    });

    [Fact]
    public Task SingleRowEdits_DoNotBorrowMainGroupAndRejectRemovedRows() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        var first = new AudioSessionItem("Music", 20, false, false, processId: 42);
        var second = new AudioSessionItem("Game", 50, false, false, processId: 43);
        await AddAsync(fixture.Mixer, first, second);
        // Exercise the real group propagation callback without any native writer.
        first.VolumeChanged += fixture.Mixer.PropagateGroupVolume;
        second.VolumeChanged += fixture.Mixer.PropagateGroupVolume;
        fixture.Mixer.ToggleApplicationSelection(first); fixture.Mixer.ToggleApplicationSelection(second);
        fixture.Mixer.BeginGroupVolumeEdit(first);
        using var row = new QuickMixerRow(fixture.Mixer, first);
        row.Volume = 40;
        row.ToggleMuteCommand.Execute(null);
        Assert.Equal(40, first.Volume);
        Assert.Equal(50, second.Volume);
        Assert.True(first.IsMuted); Assert.False(second.IsMuted);
        Assert.Equal(2, fixture.Mixer.SelectedApplicationCount);
        fixture.Mixer.EndGroupVolumeEdit(); fixture.Mixer.BeginGroupVolumeEdit(first);
        first.Volume = 50;
        Assert.Equal(60, second.Volume);
        await fixture.Mixer.ApplyRefreshResultsAsync(0, ["pid:42"], null, null, TestContext.Current.CancellationToken);
        row.Volume = 90; row.ToggleMuteCommand.Execute(null);
        Assert.Equal(50, first.Volume); Assert.True(first.IsMuted);
    });

    [Fact]
    public Task QuickGroupPreservesOffsetsAndDoesNotBorrowTheMainSelection() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        var first = new AudioSessionItem("Music", 30, false, false, processId: 41);
        var second = new AudioSessionItem("Game", 80, false, false, processId: 42);
        var other = new AudioSessionItem("Browser", 45, false, false, processId: 43);
        var system = new AudioSessionItem("System Sounds", 75, false, false, isSystemSounds: true);
        await AddAsync(fixture.Mixer, first, second, other, system);
        first.VolumeChanged += fixture.Mixer.PropagateGroupVolume;
        fixture.Mixer.ToggleApplicationSelection(first);
        fixture.Mixer.ToggleApplicationSelection(other);
        fixture.Mixer.BeginGroupVolumeEdit(first);
        using var page = new QuickMixerPage(fixture.Mixer, Dispatcher.CurrentDispatcher);
        var music = page.Rows.Single(row => ReferenceEquals(row.Source, first));
        var game = page.Rows.Single(row => ReferenceEquals(row.Source, second));
        var systemRow = page.Rows.Single(row => ReferenceEquals(row.Source, system));
        music.SelectApplicationCommand.Execute(null);
        game.ToggleApplicationSelectionCommand.Execute(null);
        systemRow.ToggleApplicationSelectionCommand.Execute(null);
        Assert.Equal(2, page.SelectedApplicationCount);
        Assert.False(systemRow.IsGroupSelected);
        Assert.False(second.IsGroupSelected);

        music.BeginVolumeEdit();
        music.Volume = 80;
        Assert.Equal(100, second.Volume);
        music.Volume = 40;
        Assert.Equal(90, second.Volume);
        Assert.Equal(45, other.Volume);
        music.EndVolumeEdit();
        music.ToggleMuteCommand.Execute(null);
        Assert.True(first.IsMuted && second.IsMuted);
        Assert.False(other.IsMuted || system.IsMuted);

        fixture.Mixer.ResetMixerInteractions();
        Assert.Equal(2, page.SelectedApplicationCount);
        page.SuspendInteractions();
        Assert.Equal(0, page.SelectedApplicationCount);
        Assert.False(music.IsGroupSelected || game.IsGroupSelected);
    });

    [Theory]
    [InlineData(AudioMixerMode.Output, "Mixer_OutputDevice")]
    [InlineData(AudioMixerMode.Input, "Mixer_InputDevice")]
    public Task QuickRoutingKeepsItsOwnExpansionAndLetsAnApprovedWriteSettle(AudioMixerMode mode, string label)
    {
        bool playback = mode == AudioMixerMode.Output;
        string? saved = null;
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(uint Pid, bool Playback, long Identity, string? Device)>();
        var operations = new ApplicationRoutingOperations(
            (pid, _, _, _) => Task.FromResult(new ApplicationRoutingInventory(new(true, false, pid == 41 ? saved : null),
                [new() { Id = "new-device", Name = "Other device" }], 123)),
            async (pid, output, identity, device, token) =>
            {
                writes.Add((pid, output, identity, device));
                await write.Task.WaitAsync(token);
                saved = device;
                return new(ProcessAudioRoutingResult.Applied, new(true, false, device));
            });
        return WithWindow(async (window, mixer, locale) =>
        {
            var first = new AudioSessionItem("Player", 40, false, false, processId: 41);
            var second = new AudioSessionItem("Chat", 60, false, false, processId: 42);
            first.UpdateRoutingMetadataFromSystem("player", "live", "current", endpointName: "Active device");
            first.UpdateActiveDeviceUse(AudioSessionDeviceUse.One);
            await AddAsync(mixer, first, second);
            using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
            BindPage(window, page);
            var root = (FrameworkElement)window.Content;
            Layout(root);
            Assert.Equal(playback ? Visibility.Visible : Visibility.Collapsed, window.SelectAppsButton.Visibility);
            Assert.Empty(WpfVisualTree.Descendants(window.SessionList).OfType<CheckBox>());
            if (playback)
            {
                page.ToggleSelectionMode();
                Layout(root);
                Assert.Equal(2, WpfVisualTree.Descendants(window.SessionList).OfType<CheckBox>().Count());
                page.ToggleSelectionMode();
            }
            var quickFirst = page.Rows.Single(row => ReferenceEquals(row.Source, first));
            var quickSecond = page.Rows.Single(row => ReferenceEquals(row.Source, second));
            mixer.ExpandApplicationCommand.Execute(first);
            quickSecond.ToggleRoutingCommand.Execute(null);
            Assert.NotNull(first.RoutingDetails);
            Assert.Null(second.RoutingDetails);
            Assert.True(quickSecond.IsRoutingExpanded);
            quickFirst.ToggleRoutingCommand.Execute(null);
            Assert.False(quickSecond.IsRoutingExpanded);
            var quick = Assert.IsType<ApplicationRoutingDetails>(quickFirst.RoutingDetails);
            var main = Assert.IsType<ApplicationRoutingDetails>(first.RoutingDetails);
            Assert.NotSame(main, quick);
            Layout(root);
            var combo = Assert.Single(WpfVisualTree.Descendants(window.SessionList).OfType<ComboBox>());
            Assert.Same(quick.SelectedDevice, combo.SelectedValue);
            Assert.Equal(locale[label], AutomationProperties.GetName(combo));
            Assert.Same(window.FindResource(typeof(ComboBoxItem)), combo.ItemContainerStyle.BasedOn);
            Assert.Equal("Active on: Active device", locale.FormatValue(quick.ActiveDeviceText));
            locale.SetLanguage("es");
            Layout(root);
            Assert.Equal(locale[label], AutomationProperties.GetName(combo));
            Assert.Contains(WpfVisualTree.Descendants(window.SessionList).OfType<TextBlock>(),
                text => text.Text == "Dispositivo en uso: Active device");
            first.UpdateActiveDeviceUse(AudioSessionDeviceUse.Multiple);
            Assert.Equal("Varios dispositivos en uso", locale.FormatValue(quick.ActiveDeviceText));
            Assert.Equal("Varios dispositivos en uso", locale.FormatValue(main.ActiveDeviceText));
            WpfVisualAssertions.NoBindingErrors(root);
            try
            {
                combo.SetCurrentValue(Selector.SelectedValueProperty, quick.Devices.Single(device => device.Id == "new-device"));
                Assert.True(main.IsBusy && quick.IsBusy);
                page.SuspendInteractions();
                Assert.Null(quickFirst.RoutingDetails);
                Assert.Same(main, first.RoutingDetails);
            }
            finally { write.TrySetResult(); }
            await TestExecutionGuards.WaitUntilAsync(() => !main.IsBusy, "Main mixer did not pick up the settled Quick Mixer route.");
            Assert.Equal("new-device", main.SelectedDevice!.Id);
            Assert.Equal((41u, playback, 123L, "new-device"), Assert.Single(writes));
        }, mode: mode, routing: operations);
    }

    [VisualIntegrationTheory]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    [InlineData("en", "win", Key.Enter, false)]
    [InlineData("es", "pre", Key.Escape, false)]
    [InlineData("es", "áUR", Key.Enter, true)]
    [InlineData("en", "win", Key.F4, false)]
    public Task RoutingTypeAheadPreviewsTheVisibleLabelUntilConfirmed(string language, string prefix, Key closeKey, bool alreadyOpen) => WithWindow(async (mixer, _, locale) =>
    {
        var current = new ApplicationRoutingDevice("speakers", "Speakers");
        var unavailable = new ApplicationRoutingDevice("old", "Áuriculares (unavailable)", IsSelectable: false);
        var headset = new ApplicationRoutingDevice("headset", "Áuriculares USB");
        var system = new ApplicationRoutingDevice(null, "Windows default") { LabelResource = "Mixer_WindowsDefault" };
        var details = new ApplicationRoutingDetails { IsBusy = false };
        details.SetDevices([system, current, unavailable, headset], current);
        var writes = new List<ApplicationRoutingDevice>();
        details.SelectionRequested += writes.Add;
        var content = new ContentControl
        {
            Content = new { RoutingDetails = details },
            ContentTemplate = (DataTemplate)mixer.FindResource("MixerRoutingDetailsTemplate"),
        };
        var window = TestWindowFactory.CreateOffscreenWindow(width: 380, height: 180, theme: AppTheme.Dark);
        LocalizationContext.SetService(window, locale);
        window.Content = content;
        try
        {
            TestWindowFactory.ShowWindowForTest(window);
            var combo = Assert.Single(WpfVisualTree.Descendants(content).OfType<ComboBox>());
            Assert.Same(current, combo.SelectedValue);
            locale.SetLanguage(language);
            UIElement textTarget = combo;
            if (alreadyOpen)
            {
                details.IsChoosingDevice = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                textTarget = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(1));
            }
            var text = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, textTarget, prefix))
            {
                RoutedEvent = TextCompositionManager.TextInputEvent,
            };
            textTarget.RaiseEvent(text);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            int expectedIndex = prefix == "áUR" ? 3 : 0;
            Assert.True(details.IsChoosingDevice);
            Assert.Same(current, details.SelectedDevice);
            Assert.Empty(writes);
            var highlighted = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(expectedIndex));
            Assert.True(highlighted.IsHighlighted);
            Assert.Contains(WpfVisualTree.Descendants(highlighted).OfType<TextBlock>(),
                label => label.Text == (expectedIndex == 0 ? locale["Mixer_WindowsDefault"] : headset.Name));

            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(combo)!, Environment.TickCount, closeKey)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            combo.RaiseEvent(key);
            if (!key.Handled)
            {
                key.RoutedEvent = Keyboard.KeyDownEvent;
                combo.RaiseEvent(key);
            }
            Assert.False(details.IsChoosingDevice);
            if (closeKey == Key.Enter)
            {
                var expected = expectedIndex == 0 ? system : headset;
                Assert.Same(expected, details.SelectedDevice);
                Assert.Same(expected, Assert.Single(writes));
            }
            else
            {
                Assert.Same(current, details.SelectedDevice);
                Assert.Empty(writes);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RejectedRouteReloadsBothPanelsAndDistinguishesIncompleteDiscovery() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var present = new CycleDevice { Id = "present", Name = "Speakers" };
        var old = new CycleDevice { Id = "old", Name = "Headphones" };
        var refreshed = new TaskCompletionSource<ApplicationRoutingInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool reconnecting = false;
        int refreshes = 0;
        var operations = new ApplicationRoutingOperations((_, _, _, _) =>
        {
            if (!reconnecting) return Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [present, old], 123));
            refreshes++;
            return refreshed.Task;
        }, (_, _, _, id, _) =>
        {
            Assert.Equal(old.Id, id);
            reconnecting = true;
            return Task.FromResult(new ApplicationRoutingChange(ProcessAudioRoutingResult.Failed, default, MayHaveWritten: false, TargetUnavailable: true));
        });
        using var fixture = new MixerTestFixture(routing: operations);
        var player = new AudioSessionItem("Player", 40, false, false, processId: 41);
        await AddAsync(fixture.Mixer, player);
        using var page = new QuickMixerPage(fixture.Mixer, Dispatcher.CurrentDispatcher);
        fixture.Mixer.ExpandApplicationCommand.Execute(player);
        page.Rows[0].ToggleRoutingCommand.Execute(null);
        var main = Assert.IsType<ApplicationRoutingDetails>(player.RoutingDetails);
        var quick = Assert.IsType<ApplicationRoutingDetails>(page.Rows[0].RoutingDetails);

        quick.IsChoosingDevice = true;
        quick.SelectedDevice = quick.Devices.Single(device => device.Id == old.Id);
        Assert.False(quick.IsChoosingDevice);
        Assert.False(quick.CanEdit);
        Assert.False(main.CanEdit);
        refreshed.SetResult(new(new(true, false, old.Id), new AudioDeviceInventory([present], isComplete: false), 123));
        await TestExecutionGuards.WaitUntilAsync(() => !main.IsBusy && !quick.IsBusy, "Both routing panels should refresh after rejection.");

        Assert.Equal(2, refreshes);
        Assert.Contains("no longer available", quick.Status);
        foreach (var details in new[] { quick, main })
        {
            Assert.True(details.CanRetry);
            Assert.Contains(details.Devices, device => device.Id == present.Id && device.IsSelectable);
            Assert.DoesNotContain(details.Devices, device => device.Id == old.Id && device.IsSelectable);
            Assert.Equal("Mixer_RoutingDeviceStatusUnavailable", details.SelectedDevice!.LabelResource);
        }
    });

    [Fact]
    public Task SuspendingQuickRoutingCancelsItsReadButKeepsTheMainPanel() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var readCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new ApplicationRoutingOperations(async (pid, _, _, cancellationToken) =>
        {
            if (pid == 42)
            {
                using var registration = cancellationToken.Register(() => readCanceled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new(new(true, false, null), [], 123);
        }, (_, _, _, _, _) => throw new InvalidOperationException("No routing write was requested."));
        using var fixture = new MixerTestFixture(routing: operations);
        var first = new AudioSessionItem("Player", 40, false, false, processId: 41);
        var second = new AudioSessionItem("Chat", 60, false, false, processId: 42);
        await AddAsync(fixture.Mixer, first, second);
        fixture.Mixer.ExpandApplicationCommand.Execute(first);
        var main = Assert.IsType<ApplicationRoutingDetails>(first.RoutingDetails);
        using var page = new QuickMixerPage(fixture.Mixer, Dispatcher.CurrentDispatcher);
        var quick = page.Rows.Single(row => ReferenceEquals(row.Source, second));
        quick.ToggleRoutingCommand.Execute(null);
        Assert.True(quick.RoutingDetails!.IsReading);
        page.SuspendInteractions();
        await readCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(quick.IsRoutingExpanded);
        Assert.Same(main, first.RoutingDetails);
        Assert.Equal("Windows default", Assert.Single(main.Devices).Name);
    });

    [Fact]
    public Task QuickDeafenUsesTheAppMuteStateAndNotifiesMicrophoneOwnership() => WithWindow(async (_, mixer, locale) =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(QuickMixerTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        using var sampler = new AudioMeterSampler(() => throw new InvalidOperationException("Off-screen test."));
        var model = harness.ViewModel;
        var requests = new List<bool>();
        model.MicrophoneMuteChangeRequested += requests.Add;
        var inputWrites = new List<bool>();
        var outputWrites = new List<bool>();
        var window = new QuickMixerWindow(_ => mixer, _ => new TestLease(), (_, _) => Task.CompletedTask, sampler, 0,
            localization: locale, muteControls: model);
        try
        {
            AudioDeviceService.SetMicrophoneMuteOverrideForTests = inputWrites.Add;
            AudioDeviceService.SetPlaybackMuteOverrideForTests = outputWrites.Add;
            Layout((FrameworkElement)window.Content);
            window.DeafenToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(model.Deafen);
            Assert.False(model.MuteMic || model.MuteSound);
            Assert.Equal([true], requests);
            await model.WaitForQueuedBackgroundTasksForTestsAsync();
            model.Deafen = false;
            Assert.False(window.DeafenToggle.IsChecked);
            Assert.Equal([true, false], requests);
            await model.WaitForQueuedBackgroundTasksForTestsAsync();
            Assert.Equal([true, false], inputWrites);
            Assert.Equal([true, false], outputWrites);
        }
        finally
        {
            window.Close();
            try { await model.CleanupAsync(); }
            finally { AudioDeviceService.ResetTestHooks(); }
        }
    });

    [Fact]
    public Task Projection_ReusesRowsAndDoesNotExposeTheOtherEndpoint() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture(AudioMixerMode.Input);
        var master = new AudioSessionItem("Master Volume", 60, true, false, endpointId: "output");
        var mic = new AudioSessionItem("Microphone Volume", 70, false, true, endpointId: "input");
        var app = new AudioSessionItem("Recorder", 30, false, false, processId: 77, sessionInstanceId: "capture", endpointId: "input");
        await AddAsync(fixture.Mixer, master, mic, app);
        using var page = new QuickMixerPage(fixture.Mixer, Dispatcher.CurrentDispatcher);
        Assert.Equal(2, page.Rows.Count); Assert.Same(mic, page.Rows[0].Source);
        QuickMixerRow original = page.Rows[1];
        app.SetVolumeFromSystem(42);
        page.Synchronize();
        Assert.Same(original, page.Rows[1]); Assert.Equal(42, original.Volume);
        Assert.DoesNotContain(page.Rows, row => row.Source.IsMaster);
    });

    [Fact]
    public Task MeterProjection_CombinesSessionsButRejectsUnknownAbsenceAndReusedPid() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        var app = new AudioSessionItem("Player", 50, false, false, processId: 42, sessionInstanceId: "one", endpointId: "out", sessionCount: 2);
        await AddAsync(fixture.Mixer, app);
        using var row = new QuickMixerRow(fixture.Mixer, app);
        var readings = ImmutableArray.Create(
            AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "out", "one", 42), .2f),
            AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "other-out", "two", 42), .8f));
        var snapshot = new AudioMeterSnapshot(1, 0, new(readings, AudioMeterModes.Output), TimeSpan.Zero, 0, false);
        row.UpdateMeter(snapshot, AudioMixerMode.Output);
        Assert.True(row.MeterAvailable); Assert.InRange(row.Level, 39.99, 40.01);
        Assert.Equal(50, row.Volume);
        app.SetVolumeFromSystem(25);
        Assert.InRange(row.Level, 19.99, 20.01);
        Assert.InRange(row.Peak, 19.99, 20.01);
        app.SetMuteFromSystem(true);
        Assert.Equal(0, row.Level); Assert.Equal(0, row.Peak);
        app.SetMuteFromSystem(false);
        Assert.InRange(row.Level, 19.99, 20.01);
        row.UpdateMeter(snapshot with { Batch = new(readings, AudioMeterModes.None) }, AudioMixerMode.Output);
        Assert.False(row.MeterAvailable);
        row.UpdateMeter(snapshot with { Batch = new([readings[1]], AudioMeterModes.Output) }, AudioMixerMode.Output);
        Assert.False(row.MeterAvailable);
        app.UpdateRoutingMetadataFromSystem(null, "replacement", "out", 1);
        row.UpdateMeter(snapshot, AudioMixerMode.Output);
        Assert.False(row.MeterAvailable); Assert.Equal(0, row.Level);
        row.UpdateMeter(null, AudioMixerMode.Output);
        Assert.False(row.MeterAvailable);
    });

    [Fact]
    public Task ReplacedSingleSession_IgnoresRetiredPeaksForMeterAndPlayingFirst() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        using var clock = new ManualTimeProvider();
        var app = new AudioSessionItem("Player", 100, false, false, processId: 42, sessionInstanceId: "retired", endpointId: "old-out");
        await AddAsync(fixture.Mixer, app);
        using var row = new QuickMixerRow(fixture.Mixer, app);
        var retired = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "old-out", "retired", 42), .9f, 1f, false);
        var replacement = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "new-out", "current", 42), .5f, 1f, true);
        app.UpdateRoutingMetadataFromSystem(null, "current", "new-out", 1);

        AssertSample(replacement, meterAvailable: true, playing: false, level: 0);
        AssertSample(replacement with { Status = AudioMeterStatus.Unavailable, Peak = null }, meterAvailable: false, playing: false, level: 0);
        AssertSample(replacement with { IsMuted = false }, meterAvailable: true, playing: true, level: 50);

        void AssertSample(AudioMeterReading current, bool meterAvailable, bool playing, double level)
        {
            var snapshot = new AudioMeterSnapshot(1, clock.GetTimestamp(), new([retired, current], AudioMeterModes.Output), TimeSpan.Zero, 0, false);
            row.UpdateMeter(snapshot, AudioMixerMode.Output);
            foreach (var reading in snapshot.Batch.Readings) row.CollectPlaybackSample(reading);
            row.UpdatePlaybackState(clock.GetTimestamp(), clock);
            Assert.Equal(meterAvailable, row.MeterAvailable);
            Assert.Equal(playing, row.IsPlaying);
            Assert.Equal(level, row.Level);
        }
    });

    [Fact]
    public Task MeterProjection_DoesNotCombineOneSessionsPeakWithAnotherSessionsVolume() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        var app = new AudioSessionItem("Player", 100, false, false, processId: 42, sessionInstanceId: "one", endpointId: "out", sessionCount: 2);
        await AddAsync(fixture.Mixer, app);
        using var row = new QuickMixerRow(fixture.Mixer, app);
        var quiet = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "out", "one", 42), .9f, .1f, false);
        var loud = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "out", "two", 42), .2f, 1f, false);
        var snapshot = new AudioMeterSnapshot(1, 0, new([quiet, loud], AudioMeterModes.Output), TimeSpan.Zero, 0, false);
        row.UpdateMeter(snapshot, AudioMixerMode.Output);
        Assert.InRange(row.Level, 19.99, 20.01);
        row.UpdateMeter(snapshot with { Batch = new([quiet with { IsMuted = true }, loud with { VolumeScalar = .5f }], AudioMeterModes.Output) }, AudioMixerMode.Output);
        Assert.InRange(row.Level, 9.99, 10.01);
        Assert.InRange(row.Peak, 9.99, 10.01);
    });

    [Fact]
    public Task MeterProjection_TrustsFreshSessionMuteAndHonorsPendingLocalMute() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        using var clock = new ManualTimeProvider();
        var app = new AudioSessionItem("Player", 50, false, false, processId: 42, isMuted: true, sessionInstanceId: "one", endpointId: "out");
        await AddAsync(fixture.Mixer, app);
        using var row = new QuickMixerRow(fixture.Mixer, app, clock);
        var fresh = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "out", "one", 42), .8f, .5f, false);
        var frame = new AudioMeterSnapshot(1, 0, new([fresh], AudioMeterModes.Output), TimeSpan.Zero, 0, false);
        row.UpdateMeter(frame, AudioMixerMode.Output);
        Assert.InRange(row.Level, 39.99, 40.01);

        app.SetMuteFromSystem(false);
        row.ToggleMuteCommand.Execute(null);
        row.UpdateMeter(frame, AudioMixerMode.Output);
        Assert.Equal(0, row.Level);
        Assert.Equal(0, row.Peak);

        clock.Advance(TimeSpan.FromMilliseconds(251));
        row.UpdateMeter(frame, AudioMixerMode.Output);
        Assert.InRange(row.Level, 39.99, 40.01);
        row.UpdateMeter(frame with { Batch = new([fresh with { IsMuted = true }], AudioMeterModes.Output) }, AudioMixerMode.Output);
        Assert.Equal(0, row.Level);
    });

    [Fact]
    public Task MeterProjection_UsesBoundedFeedbackUntilNativeVolumeConvergesAndRejectsPartialControls() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture();
        using var clock = new ManualTimeProvider();
        var app = new AudioSessionItem("Player", 100, false, false, processId: 42, sessionInstanceId: "one", endpointId: "out", sessionCount: 2);
        await AddAsync(fixture.Mixer, app);
        using var row = new QuickMixerRow(fixture.Mixer, app, clock);
        var first = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "out", "one", 42), .9f, .1f, false);
        var second = AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "out", "two", 42), .2f, 1f, false);
        var old = new AudioMeterSnapshot(1, 0, new([first, second], AudioMeterModes.Output), TimeSpan.Zero, 0, false);
        row.UpdateMeter(old, AudioMixerMode.Output);
        row.Volume = 50;
        Assert.InRange(row.Level, 44.99, 45.01);
        row.UpdateMeter(old, AudioMixerMode.Output);
        Assert.InRange(row.Level, 44.99, 45.01);
        clock.Advance(TimeSpan.FromMilliseconds(251));
        row.UpdateMeter(old, AudioMixerMode.Output);
        Assert.InRange(row.Level, 19.99, 20.01);
        var converged = old with { Batch = new([first with { VolumeScalar = .5f }, second with { VolumeScalar = .5f }], AudioMeterModes.Output) };
        row.UpdateMeter(converged, AudioMixerMode.Output);
        Assert.InRange(row.Level, 44.99, 45.01);
        row.UpdateMeter(old with { Batch = new([first, second with { VolumeScalar = null, IsMuted = null }], AudioMeterModes.Output) }, AudioMixerMode.Output);
        Assert.False(row.MeterAvailable); Assert.Equal(0, row.Level); Assert.Equal(0, row.Peak);
    });

    [Fact]
    public Task MeterProjection_InputDoesNotDoubleApplyTheSharedMicrophoneLevelOrMute() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var fixture = new MixerTestFixture(AudioMixerMode.Input);
        var mic = new AudioSessionItem("Microphone", 10, false, true, endpointId: "input");
        mic.SetMuteFromSystem(true);
        await AddAsync(fixture.Mixer, mic);
        using var row = new QuickMixerRow(fixture.Mixer, mic);
        var reading = AudioMeterReading.FromPeak(new(AudioMixerMode.Input, "input"), .01f);
        row.UpdateMeter(new(1, 0, new([reading], AudioMeterModes.Input), TimeSpan.Zero, 0, false), AudioMixerMode.Input);
        Assert.True(row.MeterAvailable); Assert.InRange(row.Level, 33.32, 33.34);
        mic.SetVolumeFromSystem(0);
        Assert.InRange(row.Level, 33.32, 33.34);
        Assert.True(mic.IsMuted);
    });

    [Fact]
    public Task TrayOnlyDemand_DoesNotCreateTheOtherMixerAndSurvivesMainHide() => SharedStaDispatcherHost.RunAsync(() =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(QuickMixerTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        AppViewModel model = harness.ViewModel;
        using var first = model.AcquireMixerPresentation(AudioMixerMode.Input);
        using var second = model.AcquireMixerPresentation(AudioMixerMode.Input);
        model.InputMixer.Sessions.Add(new("Microphone Volume", 50, false, true));
        model.HandleWindowVisibilityChanged(false);
        Assert.Null(TestPrivateAccess.GetField<MixerViewModel?>(model, "_mixer"));
        Assert.Equal(MixerRefreshTarget.Input, TestPrivateAccess.GetField<MixerRefreshTarget?>(model, "_mixerSessionMonitoringTarget"));
        Assert.Single(model.InputMixer.Sessions);
        first.Dispose();
        Assert.Single(model.InputMixer.Sessions);
        using (model.AcquireMixerPresentation(AudioMixerMode.Output))
            Assert.Equal(MixerRefreshTarget.Both, TestPrivateAccess.GetField<MixerRefreshTarget?>(model, "_mixerSessionMonitoringTarget"));
        second.Dispose();
        Assert.Null(TestPrivateAccess.GetField<MixerRefreshTarget?>(model, "_mixerSessionMonitoringTarget"));
        Assert.Empty(model.InputMixer.Sessions);
        Assert.Equal(0, model.SelectedSettingsTabIndex);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AppShutdownWait_IsCancellableWithoutReleasingAnActiveNativeReader(bool completed) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace("quick-mixer-shutdown");
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        using var release = new ManualResetEventSlim(completed);
        var backend = new BlockingBackend(release);
        using var sampler = new AudioMeterSampler(() => backend);
        using var visible = sampler.AcquireVisibility(AudioMixerMode.Output);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        TestPrivateAccess.SetField(harness.ViewModel, "_quickMixerMeters", sampler);
        try
        {
            if (completed) await harness.ViewModel.StopQuickMixerMetersAsync(TestContext.Current.CancellationToken);
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ViewModel.StopQuickMixerMetersAsync(new CancellationToken(true)));
                Assert.False(backend.Disposed);
                Assert.False(sampler.Completion.IsCompleted);
            }
            Assert.Null(TestPrivateAccess.GetField<AudioMeterSampler?>(harness.ViewModel, "_quickMixerMeters"));
        }
        finally { release.Set(); }
        await sampler.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(backend.Disposed);
        Assert.True(backend.Released);
    });

    [Theory]
    [InlineData(AppTheme.Dark, 8)]
    [InlineData(AppTheme.Light, 64)]
    [InlineData(AppTheme.Dark, 256)]
    public Task Layout_VirtualizesAndKeepsMidListScrollThroughLiveLanguageAndThemeChanges(AppTheme theme, int count) => WithWindow(async (window, mixer, locale) =>
    {
        await AddAsync(mixer, [.. Enumerable.Range(1, count).Select(index => new AudioSessionItem($"Application {index:D3}", 30, false, false,
            processId: (uint)index, sessionInstanceId: $"session-{index}", endpointId: "output"))]);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        BindPage(window, page);
        var content = (FrameworkElement)window.Content;
        Layout(content);
        var viewport = WpfVisualTree.Descendants(window.SessionList).OfType<ScrollContentPresenter>().First();
        int fullyVisible = WpfVisualTree.Descendants(window.SessionList).OfType<ListBoxItem>().Count(item =>
        {
            double top = item.TranslatePoint(new Point(0, 0), viewport).Y;
            return top >= -0.5 && top + item.ActualHeight <= viewport.ActualHeight + 0.5;
        });
        Assert.InRange(fullyVisible, 5, count);
        var scroll = WpfVisualTree.Descendants(window.SessionList).OfType<ScrollViewer>().First();
        scroll.ScrollToVerticalOffset(120); Layout(content);
        double offset = scroll.VerticalOffset;
        var realized = WpfVisualTree.Descendants(window.SessionList).OfType<ListBoxItem>().ToArray();
        Assert.InRange(realized.Length, 1, 10);
        Assert.All(WpfVisualTree.Descendants(window.SessionList).OfType<Slider>(), slider => Assert.True(slider.ActualWidth >= 100));
        locale.SetLanguage("es"); Layout(content);
        Assert.Equal("Mezclador rápido", window.Title);
        Assert.InRange(Math.Abs(scroll.VerticalOffset - offset), 0, 1);
        var itemsHost = WpfVisualTree.Descendants(window.SessionList).OfType<VirtualizingStackPanel>().First();
        WindowThemeHelper.ApplyThemeResources(Application.Current.Resources,
            theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, highContrast: false);
        Layout(content);
        Assert.True(window.SessionList.IsAncestorOf(scroll));
        Assert.True(window.SessionList.IsAncestorOf(itemsHost));
        Assert.InRange(Math.Abs(scroll.VerticalOffset - offset), 0, 1);
        Assert.InRange(itemsHost.Children.Count, 1, 10);
        WpfVisualAssertions.NoBindingErrors(content);
        Assert.False(window.IsMeterPolling);
    }, theme);

    [Fact]
    public Task InputMeterHelpUpdatesInSpanishAndFitsTheFooter() => WithWindow(async (window, _, locale) =>
    {
        var help = Assert.IsType<TextBlock>(window.FindName("HelpText"));
        Assert.Equal(locale["QuickMixer_InputHelp"], help.Text);
        locale.SetLanguage("es");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout((FrameworkElement)window.Content);
        Assert.Contains("El medidor puede seguir activo al silenciarlo.", help.Text, StringComparison.Ordinal);
        Assert.InRange(help.DesiredSize.Height, 1, 32);
    }, mode: AudioMixerMode.Input);

    [Fact]
    public Task RowLabels_KeepTooltipsAndAccessibilityInSyncWithLanguageAndMute() => WithWindow(async (window, mixer, locale) =>
    {
        var endpoint = new AudioSessionItem("Master Volume", 70, isMaster: true, isMic: false);
        await AddAsync(mixer, endpoint);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        window.SessionList.ItemsSource = page.Rows;
        Layout((FrameworkElement)window.Content);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var row = Assert.Single(WpfVisualTree.Descendants(window.SessionList).OfType<ListBoxItem>());
        var slider = Assert.Single(WpfVisualTree.Descendants(row).OfType<Slider>());
        var muteButton = Assert.Single(WpfVisualTree.Descendants(row).OfType<Button>(), button => button.Name == "MuteButton");
        var sessionName = Assert.Single(WpfVisualTree.Descendants(row).OfType<TextBlock>(), text => text.Name == "SessionName");
        var deviceName = Assert.Single(WpfVisualTree.Descendants(row).OfType<TextBlock>(), text => text.Name == "DeviceName");

        void AssertLabels(string muteKey)
        {
            Assert.Equal(locale["Mixer_MasterVolume"], sessionName.Text);
            Assert.Equal(sessionName.Text, AutomationProperties.GetName(slider));
            Assert.Equal(locale.FormatKey(muteKey, endpoint.DisplayLabel), AutomationProperties.GetName(muteButton));
            Assert.Equal(sessionName.Text, HoverText(sessionName));
            Assert.Equal(locale["Mixer_DeviceUnavailable"], HoverText(deviceName));
            Assert.Equal(AutomationProperties.GetName(muteButton), HoverText(muteButton));
        }

        static string? HoverText(FrameworkElement element) => Assert.Single(Interaction.GetBehaviors(element).OfType<HoverInfoPopupBehavior>()).Text;

        AssertLabels("QuickMixer_Mute");
        locale.SetLanguage("es");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        AssertLabels("QuickMixer_Mute");
        endpoint.SetMuteFromSystem(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        AssertLabels("QuickMixer_Unmute");
    });

    [Fact]
    public Task Refresh_PreservesBottomAlignmentWhenViewportHeightChanges() => WithWindow(async (window, mixer, _) =>
    {
        await AddAsync(mixer, [.. Enumerable.Range(1, 64).Select(index => new AudioSessionItem($"Application {index}", 30, false, false, processId: (uint)index))]);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        TestPrivateAccess.SetField(window, "_page", page);
        TestPrivateAccess.SetField<IDisposable>(window, "_presentation", new TestLease());
        window.SessionList.ItemsSource = page.Rows;
        var root = (FrameworkElement)window.Content;
        Layout(root);
        var scroll = WpfVisualTree.Descendants(window.SessionList).OfType<ScrollViewer>().First();
        scroll.ScrollToEnd(); root.UpdateLayout();
        TestPrivateAccess.SetField(window, "_scroll", scroll);
        TestPrivateAccess.InvokeNonPublic(window, "SaveViewState");
        Assert.True(page.WasAtBottom);
        root.Measure(new Size(460, 420)); root.Arrange(new Rect(0, 0, 460, 420)); root.UpdateLayout();
        Assert.InRange(Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset), 0, 1);
        await TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
        root.UpdateLayout();
        Assert.Empty(window.StatusText.Text);
        Assert.InRange(Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset), 0, 1);
    });

    [Fact]
    public Task Refresh_DoesNotUndoScrollingPerformedWhileTheReadIsPending()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return WithWindow(async (window, mixer, _) =>
        {
            await AddAsync(mixer, [.. Enumerable.Range(1, 64).Select(index => new AudioSessionItem($"Application {index}", 30, false, false, processId: (uint)index))]);
            using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
            TestPrivateAccess.SetField(window, "_page", page);
            TestPrivateAccess.SetField<IDisposable>(window, "_presentation", new TestLease());
            window.SessionList.ItemsSource = page.Rows;
            var root = (FrameworkElement)window.Content;
            Layout(root);
            var scroll = WpfVisualTree.Descendants(window.SessionList).OfType<ScrollViewer>().First();
            TestPrivateAccess.SetField(window, "_scroll", scroll);
            scroll.ScrollToVerticalOffset(120); root.UpdateLayout();
            TestPrivateAccess.InvokeNonPublic(window, "SaveViewState");
            Task refresh = TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
            scroll.ScrollToVerticalOffset(480); root.UpdateLayout();
            double newerOffset = scroll.VerticalOffset;
            pending.SetResult();
            await refresh;
            root.UpdateLayout();
            Assert.InRange(Math.Abs(scroll.VerticalOffset - newerOffset), 0, 1);
        }, refresh: (_, _) => pending.Task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProductionFlyout_PinSurvivesDeactivationButExplicitCloseReleasesDemand(bool pinned) => WithWindow((window, _, _) =>
    {
        bool closed = false;
        var presentation = new TestLease();
        var meter = new TestLease();
        TestPrivateAccess.SetField<IDisposable>(window, "_presentation", presentation);
        TestPrivateAccess.SetField<IDisposable>(window, "_meterLease", meter);
        window.KeepOpenToggle.IsChecked = pinned;
        window.Closed += (_, _) => closed = true;
        TestPrivateAccess.SetField(window, "_ready", true);
        TestPrivateAccess.InvokeNonPublic(window, "OnWindowDeactivated", null, EventArgs.Empty);
        Assert.Equal(!pinned, closed);
        Assert.Equal(!pinned, presentation.Disposed);
        Assert.Equal(!pinned, meter.Disposed);
        if (pinned) window.Dismiss();
        Assert.True(closed);
        Assert.True(presentation.Disposed); Assert.True(meter.Disposed);
        Assert.False(window.IsMeterPolling);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Close_DetachesAnUncooperativeRefreshAndReleasesPresentation()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        return WithWindow(async (window, mixer, _) =>
        {
            var lease = new TestLease();
            TestPrivateAccess.SetField<IDisposable>(window, "_presentation", lease);
            Task refresh = TestPrivateAccess.InvokeNonPublicTask(window, "RefreshAsync");
            Assert.True(captured.CanBeCanceled);
            window.Close();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(captured.IsCancellationRequested);
            Assert.True(lease.Disposed);
            Assert.False(window.IsMeterPolling);
            Assert.False(pending.Task.IsCompleted);
            pending.SetResult();
        }, refresh: (_, token) => { captured = token; return pending.Task; });
    }

    [Fact]
    public Task FindJumpsBetweenMatchesWithoutChangingAppGroups() => WithWindow(async (window, mixer, _) =>
    {
        var first = new AudioSessionItem("Music One", 45, false, false, processId: 42);
        var second = new AudioSessionItem("Music Two", 50, false, false, processId: 43);
        var chat = new AudioSessionItem("Chat", 60, false, false, processId: 44);
        await AddAsync(mixer, first, second, chat);
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        BindPage(window, page);
        TestPrivateAccess.SetField(window, "_ready", true);
        Layout((FrameworkElement)window.Content);
        page.Rows.Single(row => ReferenceEquals(row.Source, chat)).SelectApplicationCommand.Execute(null);
        first.IsGroupSelected = true;

        Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
        ApplicationCommands.Find.Execute(null, window);
        Assert.Equal(Visibility.Visible, window.SearchPanel.Visibility);
        window.SearchBox.Text = "music";
        Assert.Same(first, Assert.IsType<QuickMixerRow>(window.SessionList.SelectedItem).Source);
        var source = new KeyboardSource();
        var next = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.SearchBox.RaiseEvent(next);
        Assert.True(next.Handled);
        Assert.Same(second, Assert.IsType<QuickMixerRow>(window.SessionList.SelectedItem).Source);
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.SearchBox.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.Equal(Visibility.Collapsed, window.SearchPanel.Visibility);
        Assert.True(page.Rows.Single(row => ReferenceEquals(row.Source, chat)).IsGroupSelected);
        Assert.Equal(1, page.SelectedApplicationCount);
        Assert.True(first.IsGroupSelected);
        Assert.False(chat.IsGroupSelected);
        Assert.Equal(3, window.SessionList.Items.Count);
    });

    [Fact]
    public Task EscapeClearsTheGroupThenClosesButVolumeNavigationIsNotIntercepted() => WithWindow(async (window, mixer, _) =>
    {
        await AddAsync(mixer, new AudioSessionItem("Music", 40, false, false, processId: 41));
        using var page = new QuickMixerPage(mixer, Dispatcher.CurrentDispatcher);
        BindPage(window, page);
        page.Rows[0].SelectApplicationCommand.Execute(null);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var source = new KeyboardSource();
        var right = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(right);
        Assert.False(right.Handled); Assert.False(closed);
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(escape);
        Assert.True(escape.Handled); Assert.False(closed);
        Assert.Equal(0, page.SelectedApplicationCount);
        escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(escape);
        Assert.True(escape.Handled); Assert.True(closed);
    });

    private static async Task AddAsync(MixerViewModel mixer, params AudioSessionItem[] rows)
    {
        static string Id(AudioSessionItem row) => row.IsMaster ? "master:primary" : row.IsMic ? "mic:primary" : row.IsSystemSounds ? "system:sounds" : $"pid:{row.ProcessId}";
        await mixer.ApplyRefreshResultsAsync(0, null, [.. rows.Select(row => (Id(row), row))], null, TestContext.Current.CancellationToken);
        foreach (var row in rows) mixer.DetachOwnedVolumeChangedHandler(Id(row), row);
    }

    private static Task WithWindow(Func<QuickMixerWindow, MixerViewModel, LocalizationService, Task> action, AppTheme theme = AppTheme.Dark,
        Func<AudioMixerMode, CancellationToken, Task>? refresh = null, AudioMixerMode mode = AudioMixerMode.Output,
        ApplicationRoutingOperations? routing = null, QuickPanelLayoutStore? layouts = null, IAppDialogService? dialogs = null,
        Func<bool>? canPresent = null)
        => SharedStaDispatcherHost.RunAsync(async () =>
        {
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ResourceDictionary previous = application.Resources;
            Window? previousWindow = application.MainWindow;
            var resources = new ResourceDictionary
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter()
            };
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative) });
            application.Resources = resources;
            using var fixture = new MixerTestFixture(mode, routing: routing);
            using var sampler = new AudioMeterSampler(() => throw new InvalidOperationException("Off-screen layout must not activate native meters."));
            var locale = new LocalizationService("en");
            var window = new QuickMixerWindow(_ => fixture.Mixer, _ => new TestLease(), refresh ?? ((_, _) => Task.CompletedTask), sampler, 0,
                mode, localization: locale, layouts: layouts, dialogs: dialogs, canPresent: canPresent);
            try { WindowThemeResolver.ApplyWindowTheme(window, theme); await action(window, fixture.Mixer, locale); }
            finally { window.Close(); application.Resources = previous; application.MainWindow = previousWindow; }
        });

    private static void BindPage(QuickMixerWindow window, QuickMixerPage page)
    {
        TestPrivateAccess.SetField(window, "_page", page);
        TestPrivateAccess.InvokeNonPublic(window, "BindInteractionPage", page);
        window.SessionList.ItemsSource = page.Rows;
    }

    private static void Layout(FrameworkElement root) { root.Measure(new Size(460, 500)); root.Arrange(new Rect(0, 0, 460, 500)); root.UpdateLayout(); }
    private sealed class TestLease : IDisposable { internal bool Disposed; public void Dispose() => Disposed = true; }
    private sealed class BlockingBackend(ManualResetEventSlim release) : IAudioMeterBackend
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed, Released;
        public AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            Released = release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return new([], modes);
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class KeyboardSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
