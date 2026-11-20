using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

public sealed partial class MainWindowPresentationTests
{
    [Theory]
    [InlineData(320d)]
    [InlineData(340d)]
    public Task VirtualMixerBringsAnUnexpectedlyTallRoutingRowIntoView(double height) => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        window.Height = height;
        var rows = await AddViewportRows(model.Mixer, 128);
        rows[64].RoutingDetails = new ApplicationRoutingDetails
        {
            IsBusy = false,
            Status = string.Join("\n", Enumerable.Repeat("Routing information could not be confirmed.", 6)),
        };
        TestWindowFactory.ShowWindowForLayoutTest(window);
        await SettleMixer(window);
        var list = (ListBox)window.FindName("VolumeMixer");
        var page = (ScrollViewer)window.FindName("MainContentScrollViewer");
        list.ScrollIntoView(rows[64]);
        await SettleMixer(window);
        var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(rows[64]));
        Assert.Same(rows[64], row.DataContext);
        var selector = Assert.Single(WpfVisualTree.Descendants(row).OfType<ComboBox>());
        selector.BringIntoView();
        await SettleMixer(window);
        Rect bounds = selector.TransformToAncestor(page).TransformBounds(new Rect(selector.RenderSize));
        Assert.InRange(bounds.Top, -1, page.ViewportHeight);
        Assert.InRange(bounds.Bottom, 0, page.ViewportHeight + 1);
        page.ScrollToBottom();
        await SettleMixer(window);
        var last = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(rows[^1]));
        Assert.InRange(last.TransformToAncestor(page).Transform(new Point(0, last.ActualHeight)).Y, 0, page.ViewportHeight + 1);
    });

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task VirtualMixerKeepsGroupEditsAndRoutingOnTheirModelsWhenRowsAreRecycled(AppTheme theme) => WithWindow(theme, async (window, model) =>
    {
        var mixer = model.Mixer;
        var rows = await AddViewportRows(mixer, 128);
        rows[0].Volume = 40;
        rows[^1].Volume = 60;
        mixer.ToggleApplicationSelection(rows[0]);
        mixer.ToggleApplicationSelection(rows[^1]);
        var details = new ApplicationRoutingDetails { DeviceLabel = "Output device:", IsBusy = false };
        var route = new ApplicationRoutingDevice("headphones", "Headphones");
        details.SetDevices([new(null, "Windows default"), route], route);
        int routingWrites = 0;
        details.SelectionRequested += _ => routingWrites++;
        rows[0].RoutingDetails = details;
        TestWindowFactory.ShowWindowForLayoutTest(window);
        await SettleMixer(window);
        var list = (ListBox)window.FindName("VolumeMixer");
        var inner = MixerViewer(list);
        Assert.InRange(WpfVisualTree.Descendants(list).OfType<ListBoxItem>().Count(), 1, 64);
        Assert.True(inner.ScrollableHeight > inner.ViewportHeight);
        Assert.True(inner.ExtentHeight > rows.Length * 24);
        var page = (ScrollViewer)window.FindName("MainContentScrollViewer");
        page.ScrollToBottom();
        await SettleMixer(window);
        Capture((FrameworkElement)window.Content, $"mixer-virtualized-{theme}", 440, 410);

        mixer.BeginGroupVolumeEdit(rows[0]);
        rows[0].Volume = 45;
        mixer.PropagateGroupVolume(rows[0]);
        mixer.EndGroupVolumeEdit(rows[0]);
        Assert.Equal(65, rows[^1].Volume);
        page.ScrollToBottom();
        await SettleMixer(window);
        var last = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(rows[^1]));
        Assert.InRange(last.TransformToAncestor(page).Transform(new Point(0, last.ActualHeight)).Y, 0, page.ViewportHeight + 1);
        Assert.Same(details, rows[0].RoutingDetails);
        Assert.Same(route, details.SelectedDevice);
        Assert.Equal(0, routingWrites);
        Assert.Equal(2, mixer.SelectedApplicationCount);
        foreach (var row in WpfVisualTree.Descendants(list).OfType<ListBoxItem>())
        {
            var item = Assert.IsType<AudioSessionItem>(row.DataContext);
            var mute = Assert.Single(WpfVisualTree.Descendants(row).OfType<Button>(), button => button.Name == "MixerMuteButton");
            Assert.Same(item, mute.CommandParameter);
            if (!ReferenceEquals(item, rows[0]))
                Assert.All(WpfVisualTree.Descendants(row).OfType<ComboBox>(), combo => Assert.Empty(combo.Items));
        }
        var viewport = (FrameworkElement)window.FindName("MixerViewport");
        double mixerTop = viewport.TransformToAncestor((Visual)page.Content).Transform(new Point()).Y;
        page.ScrollToVerticalOffset(mixerTop + 100);
        await SettleMixer(window);
        Assert.Same(details, rows[0].RoutingDetails);
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[0]);
        Assert.True(first.TransformToAncestor(list).Transform(new Point()).Y < 0);
        Assert.Same(route, Assert.Single(WpfVisualTree.Descendants(first).OfType<ComboBox>()).SelectedItem);
        Assert.Equal(0, routingWrites);
        list.ScrollIntoView(rows[64]);
        await SettleMixer(window);
        var middle = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(rows[64]));
        rows[64].RoutingDetails = new ApplicationRoutingDetails();
        await SettleMixer(window);
        middle.BringIntoView();
        await SettleMixer(window);
        var selector = Assert.Single(WpfVisualTree.Descendants(middle).OfType<ComboBox>());
        Rect selectorBounds = selector.TransformToAncestor(page).TransformBounds(new Rect(selector.RenderSize));
        Assert.InRange(selectorBounds.Top, -1, page.ViewportHeight);
        Assert.InRange(selectorBounds.Bottom, 0, page.ViewportHeight + 1);
        await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), [.. rows.Skip(8).Select(row => $"pid:{row.ProcessId}")], null, null, TestContext.Current.CancellationToken);
        await SettleMixer(window);
        Assert.Equal(8, WpfVisualTree.Descendants(list).OfType<ListBoxItem>().Count());
        Assert.InRange(inner.VerticalOffset, 0, 1);
        WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task VirtualMixerScrollPositionsSurviveTabsResizeResetAndHiddenRefresh(int initialTab) => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        await AddViewportRows(model.Mixer, 100);
        await AddViewportRows(model.InputMixer, 100);
        TestWindowFactory.ShowWindowForLayoutTest(window);
        await SettleMixer(window);
        var page = (ScrollViewer)window.FindName("MainContentScrollViewer");
        page.ScrollToVerticalOffset(650);
        await SettleMixer(window);
        double savedOffset = page.VerticalOffset;
        Assert.True(savedOffset > 500);
        int otherTab = 1 - initialTab;
        model.SelectedSettingsTabIndex = otherTab;
        await SettleMixer(window);
        Assert.InRange(page.VerticalOffset, 0, 1);
        page.ScrollToBottom();
        await SettleMixer(window);
        model.SelectedSettingsTabIndex = 3;
        await SettleMixer(window);
        window.Height = 620;
        await SettleMixer(window);
        model.SelectedSettingsTabIndex = initialTab;
        await SettleMixer(window);
        Assert.InRange(page.VerticalOffset, savedOffset - 1, savedOffset + 1);
        model.SettingsLanguageDraft = "es";
        await SettleMixer(window);
        Assert.InRange(page.VerticalOffset, savedOffset - 1, savedOffset + 1);
        model.SettingsLanguageDraft = "en";
        var changedMixer = model.ActiveMixer;
        await changedMixer.ApplyRefreshResultsAsync(changedMixer.GetActiveRefreshGeneration(),
            [$"pid:{changedMixer.Sessions[0].ProcessId}"], null, null, TestContext.Current.CancellationToken);
        await SettleMixer(window);
        savedOffset = page.VerticalOffset;
        model.Theme = AppTheme.Light;
        await SettleMixer(window);
        Assert.InRange(page.VerticalOffset, savedOffset - 1, savedOffset + 1);
        var saved = window.CaptureLayoutScrollPosition();
        window.Height = 440;
        window.RestoreLayoutScrollPosition(saved);
        await SettleMixer(window);
        Assert.InRange(page.VerticalOffset, savedOffset - 1, savedOffset + 1);
        model.SelectedSettingsTabIndex = otherTab;
        model.SelectedSettingsTabIndex = initialTab;
        model.SelectedSettingsTabIndex = otherTab;
        await SettleMixer(window);
        Assert.InRange(page.ScrollableHeight - page.VerticalOffset, 0, 1);
        window.Hide();
        model.ActiveMixer.Sessions.Clear();
        TestWindowFactory.ShowWindowForLayoutTest(window);
        await SettleMixer(window);
        await AddViewportRows(model.ActiveMixer, 100);
        await SettleMixer(window);
        Assert.InRange(page.ScrollableHeight - page.VerticalOffset, 0, 1);
        model.SelectedSettingsTabIndex = initialTab;
        window.AllowCloseForRuntimeShutdown();
        window.Close();
        await SettleMixer(window);
        Assert.Null(TestPrivateAccess.GetField<DispatcherOperation?>(window, "_tabScrollRestoreWork"));
    }, initialTab: initialTab);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task VirtualMixerUsesOneScrollbarAndKeepsTabNavigationVisible(int tab) => WithWindow(AppTheme.Dark, async (window, model) =>
    {
        model.SelectedSettingsTabIndex = tab;
        var mixer = model.ActiveMixer;
        var rows = await AddViewportRows(mixer, 100);
        TestWindowFactory.ShowWindowForLayoutTest(window);
        await SettleMixer(window);
        var page = (ScrollViewer)window.FindName("MainContentScrollViewer");
        var list = (ListBox)window.FindName("VolumeMixer");
        var inner = MixerViewer(list);
        Assert.Equal(ScrollBarVisibility.Hidden, inner.VerticalScrollBarVisibility);
        Assert.DoesNotContain(WpfVisualTree.Descendants(list).OfType<System.Windows.Controls.Primitives.ScrollBar>(), bar => bar.IsVisible);
        Assert.Single(WpfVisualTree.Descendants(window).OfType<System.Windows.Controls.Primitives.ScrollBar>(), bar => bar.IsVisible && bar.Orientation == Orientation.Vertical);
        page.ScrollToTop();
        await SettleMixer(window);
        Wheel(list, -120);
        await SettleMixer(window);
        Assert.True(page.VerticalOffset > 0);
        page.ScrollToBottom();
        await SettleMixer(window);
        Assert.True(inner.VerticalOffset > 0);
        double pageBottom = page.VerticalOffset;
        Wheel(list, 120);
        await SettleMixer(window);
        Assert.True(page.VerticalOffset < pageBottom);
        list.ScrollIntoView(rows[0]);
        await SettleMixer(window);
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(rows[0]);
        var button = WpfVisualTree.Descendants(first).OfType<Button>().First();
        Assert.True(button.Focus());
        var seen = new HashSet<AudioSessionItem>();
        for (int step = 0; step < 72; step++)
        {
            var focused = Assert.IsType<FrameworkElement>(Keyboard.FocusedElement, exactMatch: false);
            Assert.True(list.IsAncestorOf(focused));
            Rect bounds = focused.TransformToAncestor(page).TransformBounds(new Rect(focused.RenderSize));
            Assert.InRange(bounds.Top, -1, page.ViewportHeight);
            Assert.InRange(bounds.Bottom, 0, page.ViewportHeight + 1);
            if (focused.DataContext is AudioSessionItem row) seen.Add(row);
            Assert.True(focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            await SettleMixer(window);
        }
        Assert.True(seen.Count >= 18, $"Tab reached only {seen.Count} rows.");
        for (int step = 0; step < 20; step++)
        {
            var focused = Assert.IsType<FrameworkElement>(Keyboard.FocusedElement, exactMatch: false);
            Assert.True(list.IsAncestorOf(focused));
            Assert.True(focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            await SettleMixer(window);
        }
        Keyboard.ClearFocus();
        static void Wheel(ListBox target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
    });

    private static async Task<AudioSessionItem[]> AddViewportRows(MixerViewModel mixer, int count)
    {
        var rows = Enumerable.Range(1, count).Select(i => new AudioSessionItem($"Application {i:D3}", 50, false, false, processId: (uint)i)).ToArray();
        await mixer.ApplyRefreshResultsAsync(mixer.GetActiveRefreshGeneration(), null, [.. rows.Select(row => ($"pid:{row.ProcessId}", row))], null, TestContext.Current.CancellationToken);
        foreach (var row in rows) mixer.DetachOwnedVolumeChangedHandler($"pid:{row.ProcessId}", row);
        return rows;
    }

    private static ScrollViewer MixerViewer(ListBox list) => WpfVisualTree.Descendants(list).OfType<ScrollViewer>().First();
    private static async Task SettleMixer(Window window)
    {
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
}
