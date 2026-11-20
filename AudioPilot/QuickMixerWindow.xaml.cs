using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.ViewModels;

namespace AudioPilot;

internal partial class QuickMixerWindow : Window
{
    private readonly Func<AudioMixerMode, MixerViewModel> _getMixer;
    private readonly Func<AudioMixerMode, IDisposable> _acquirePresentation;
    private readonly Func<AudioMixerMode, CancellationToken, Task> _refresh;
    private readonly AudioMeterSampler _meters;
    private readonly nint _previousWindow;
    private readonly bool _dismissOnDeactivate;
    private readonly Dictionary<AudioMixerMode, QuickMixerPage> _pages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private MixerAppIconProvider? _icons;
    private readonly DispatcherTimer _meterTimer;
    private readonly DialogWorkAreaLifetime _workArea;
    private readonly QuickPanelWindowController _layout;
    private IDisposable? _presentation, _meterLease;
    private CancellationTokenSource? _refreshCancellation;
    private QuickMixerPage? _page;
    private VirtualizingStackPanel? _itemsHost;
    private ScrollViewer? _scroll;
    private bool _ready, _closed, _restoring;
    private int _refreshRevision;
    internal AudioMixerMode Mode => MixerTabs.SelectedIndex == 1 ? AudioMixerMode.Input : AudioMixerMode.Output;
    internal bool IsMeterPolling => _meterTimer.IsEnabled;
    internal bool IsPinned => _layout.IsPinned;

    internal QuickMixerWindow(Func<AudioMixerMode, MixerViewModel> getMixer, Func<AudioMixerMode, IDisposable> acquirePresentation,
        Func<AudioMixerMode, CancellationToken, Task> refresh, AudioMeterSampler meters, nint previousWindow,
        AudioMixerMode mode = AudioMixerMode.Output, LocalizationService? localization = null, bool dismissOnDeactivate = true,
        QuickPanelLayoutStore? layouts = null, AppViewModel? muteControls = null)
    {
        _getMixer = getMixer;
        _acquirePresentation = acquirePresentation;
        _refresh = refresh;
        _meters = meters;
        _previousWindow = previousWindow;
        _dismissOnDeactivate = dismissOnDeactivate;
        InitializeComponent();
        if (muteControls != null)
            DeafenToggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(AppViewModel.Deafen)) { Source = muteControls, Mode = BindingMode.TwoWay });
        else DeafenToggle.IsEnabled = false;
        SessionList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        SessionList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRowButtonClick));
        VirtualizingStackPanel.AddCleanUpVirtualizedItemHandler(SessionList, OnRowVirtualized);
        LocalizationContext.SetService(this, localization ?? LocalizationService.Default);
        MixerTabs.SelectedIndex = mode == AudioMixerMode.Input ? 1 : 0;
        LocalizationContext.SetText(HelpText, TextBlock.TextProperty, new(mode == AudioMixerMode.Input ? "QuickMixer_InputHelp" : "QuickMixer_Help"));
        WindowThemeResolver.ApplyOwnerOrMainWindowTheme(this);
        Opacity = 0;
        _layout = new(this, KeepOpenToggle, AlwaysOnTopItem, ResizeWindowItem, PanelResizeGrip, QuickPanelKind.Mixer,
            () => QuickPanelPlacement.Position(this, _previousWindow, 460, 500), layouts);
        _meterTimer = new(DispatcherPriority.Render, Dispatcher) { Interval = AudioMeterPresentation.RefreshInterval };
        _meterTimer.Tick += MeterTick;
        SourceInitialized += (_, _) => QuickPanelPlacement.Position(this, _previousWindow, 460, 500);
        _workArea = new(this);
        Loaded += OnLoaded;
        Activated += (_, _) => _page?.RefreshRoutingOnActivation();
        IsVisibleChanged += (_, _) => { if (_ready && !_closed) { if (IsVisible && WindowState != WindowState.Minimized) ActivatePage(); else Suspend(); } };
        StateChanged += (_, _) => { if (_ready && !_closed) { if (WindowState == WindowState.Minimized) Suspend(); else if (!_meterTimer.IsEnabled) ActivatePage(); } };
        Closing += (_, _) => { _closed = true; Suspend(); };
        Closed += (_, _) =>
        {
            _meterTimer.Tick -= MeterTick;
            _layout.Dispose();
            _workArea.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
            _icons?.Dispose();
            foreach (var page in _pages.Values) page.Dispose();
            _pages.Clear();
            BindingOperations.ClearBinding(DeafenToggle, ToggleButton.IsCheckedProperty);
            SessionList.ItemsSource = null;
            BindInteractionPage(null);
            _page = null; _itemsHost = null; _scroll = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready || _closed) return;
        await _layout.PrepareAsync();
        if (_closed) return;
        Opacity = 1;
        _ = Activate();
        _ready = true;
        ActivatePage();
        SessionList.Focus();
    }

    private void ActivatePage()
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized) return;
        SaveViewState();
        // Acquire the new mode before releasing the old one; monitoring never
        // briefly disappears during a tab change.
        IDisposable? nextPresentation = null, nextMeter = null;
        try
        {
            nextPresentation = _acquirePresentation(Mode);
            try { nextMeter = _meters.AcquireVisibility(Mode); }
            catch (ObjectDisposedException) when (_meters.Completion.IsCompleted) { _ = _meters.Completion.Exception; }
            Suspend();
            _presentation = nextPresentation; nextPresentation = null;
            _meterLease = nextMeter; nextMeter = null;
            LocalizationContext.SetText(HelpText, TextBlock.TextProperty, new(Mode == AudioMixerMode.Input ? "QuickMixer_InputHelp" : "QuickMixer_Help"));
            if (!_pages.TryGetValue(Mode, out _page)) _pages.Add(Mode, _page = new(_getMixer(Mode), Dispatcher));
            _page.Synchronize();
            _restoring = true;
            try
            {
                BindInteractionPage(_page);
                SessionList.ItemsSource = _page.Rows;
            }
            finally { _restoring = false; }
            _itemsHost = null;
            _scroll = null;
            foreach (var row in _page.Rows) row.UpdateMeter(null, Mode);
            _meterTimer.Start();
            _ = RefreshAsync();
        }
        catch (ObjectDisposedException) { Close(); }
        catch (Exception ex)
        {
            Logger.Instance.Warning("QuickMixer", "quick-mixer-open-failed", nameof(ActivatePage), ex);
            Close();
        }
        finally { nextMeter?.Dispose(); nextPresentation?.Dispose(); }
    }

    private void BindInteractionPage(QuickMixerPage? page)
    {
        SessionList.DataContext = page;
        SelectAppsButton.DataContext = page;
        GroupSummary.DataContext = page;
    }

    private void SaveViewState()
    {
        if (_page == null) return;
        if (_scroll != null)
        {
            _page.ScrollOffset = _scroll.VerticalOffset;
            _page.WasAtBottom = _scroll.ScrollableHeight > 0 && _scroll.VerticalOffset >= _scroll.ScrollableHeight - 1;
        }
        if (!_restoring && SessionList.SelectedItem is QuickMixerRow selected) _page.SelectedIdentity = selected.Identity;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Layout changes are not a new navigation request. Keep deliberate scrolling
        // while a device read is pending, rather than restoring its old offset.
        if (_restoring || _page == null || !ReferenceEquals(e.OriginalSource, _scroll)) return;
        if (e.ViewportHeightChange != 0)
        {
            if (_page.WasAtBottom) _scroll?.ScrollToEnd();
            return;
        }
        if (e.VerticalChange == 0 || e.ExtentHeightChange != 0) return;
        SaveViewState();
    }

    private void Suspend()
    {
        SaveViewState();
        _meterTimer.Stop();
        if (_page != null)
        {
            _page.SuspendInteractions();
            foreach (var row in _page.Rows) row.ReleaseIcon();
        }
        _refreshRevision++;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _meterLease?.Dispose(); _meterLease = null;
        _presentation?.Dispose(); _presentation = null;
    }

    private async Task RefreshAsync()
    {
        if (_closed || _presentation == null || _refreshCancellation != null) return;
        int revision = ++_refreshRevision;
        AudioMixerMode mode = Mode;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        LocalizationContext.SetText(StatusText, TextBlock.TextProperty, new("QuickMixer_Loading"));
        Task? pending = null;
        try
        {
            pending = _refresh(mode, token);
            await pending.WaitAsync(token);
            if (_closed || revision != _refreshRevision) return;
            _page!.Synchronize();
            LocalizationContext.SetText(StatusText, TextBlock.TextProperty, new(_page.Rows.Count == 0 ? "QuickMixer_Empty" : "Common_Empty"));
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed || revision != _refreshRevision || _page == null) return;
                SessionList.UpdateLayout();
                _scroll = FindChild<ScrollViewer>(SessionList);
                _itemsHost = FindChild<VirtualizingStackPanel>(SessionList);
                if (_page.WasAtBottom) _scroll?.ScrollToEnd();
                else _scroll?.ScrollToVerticalOffset(_page.ScrollOffset);
                _restoring = true;
                try { SessionList.SelectedItem = _page.Rows.FirstOrDefault(row => row.Identity == _page.SelectedIdentity); }
                finally { _restoring = false; }
                if (SearchPanel.Visibility == Visibility.Visible) FindApplication(Math.Max(0, SessionList.SelectedIndex));
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null) _ = pending.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            if (_closed || revision != _refreshRevision) return;
            LocalizationContext.SetText(StatusText, TextBlock.TextProperty, new("QuickMixer_RefreshFailed"));
            Logger.Instance.Warning("QuickMixer", "quick-mixer-refresh-failed", nameof(RefreshAsync), ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private static void OnRowVirtualized(object sender, CleanUpVirtualizedItemEventArgs e)
    {
        if (e.Value is QuickMixerRow row) row.ReleaseIcon();
    }

    private async Task LoadIconAsync(QuickMixerRow row)
    {
        row.IconRequested = true;
        try
        {
            var pending = (_icons ??= new()).GetAsync(row.Source.ProcessId!.Value, row.Source.SessionInstanceId, row.Source.ProcessStartedTicks);
            if (pending == null) { row.DeferIconRequest(); return; }
            await row.WaitForIconAsync(pending);
        }
        catch (Exception ex) { Logger.Instance.Trace("QuickMixer", () => $"quick-mixer-icon-unavailable | error=0x{ex.HResult:X8}"); }
    }

    private void MeterTick(object? sender, EventArgs e)
    {
        if (_closed || !IsVisible || _page == null) return;
        _itemsHost ??= FindChild<VirtualizingStackPanel>(SessionList);
        if (_itemsHost == null) return;
        _meters.TryGetLatest(Mode, out AudioMeterSnapshot? snapshot);
        foreach (UIElement child in _itemsHost.Children)
        {
            if (child is not ListBoxItem { Content: QuickMixerRow row }) continue;
            row.UpdateMeter(snapshot, Mode);
            if (row.Source.IsApplication && row.CanRequestIcon) _ = LoadIconAsync(row);
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e) { if (_ready && ReferenceEquals(e.Source, MixerTabs)) ActivatePage(); }
    private void OnRowSelected(object sender, SelectionChangedEventArgs e) { if (!_restoring && _page != null && ReferenceEquals(e.Source, SessionList)) _page.SelectedIdentity = (SessionList.SelectedItem as QuickMixerRow)?.Identity; }
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshFromUserAsync();

    private async Task RefreshFromUserAsync()
    {
        SaveViewState();
        _meters.RequestRefresh();
        Task routing = _page?.RefreshRoutingAsync() ?? Task.CompletedTask;
        await Task.WhenAll(RefreshAsync(), routing);
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_dismissOnDeactivate || !_ready || _closed || IsPinned || _layout.IsChangingMode) return;
        if (_page?.IsChoosingDevice != true) { Close(); return; }
        // The ComboBox popup can close as a consequence of clicking outside the flyout.
        // Let WPF settle that transition before deciding whether the flyout lost focus.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_closed && !IsActive && !IsPinned && !_layout.IsChangingMode && _page?.IsChoosingDevice != true) Close();
        }));
    }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e) => _layout.Drag(e);
    private void OnWindowOptions(object sender, RoutedEventArgs e) => _layout.OpenOptions((Button)sender);
    private void OnMoveWindow(object sender, RoutedEventArgs e) => _layout.BeginMoveOrResize(false);
    private void OnResizeWindow(object sender, RoutedEventArgs e) => _layout.BeginMoveOrResize(true);
    private void OnResetWindowLayout(object sender, RoutedEventArgs e) => _layout.ResetLayout(
        () => QuickPanelPlacement.Position(this, new WindowInteropHelper(this).Handle, 460, 500));

    private void OnSelectAppsClick(object sender, RoutedEventArgs e) => _page?.ToggleSelectionMode();

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_page?.ClearSelection() == true) SessionList.Focus();
    }

    private void OnRowButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Name: "RoutingButton", DataContext: QuickMixerRow row } button || row.IsRoutingExpanded) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed && button.IsLoaded && ReferenceEquals(button.DataContext, row) && row.IsRoutingExpanded
                && ReferenceEquals(SessionList.ItemsSource, _page?.Rows))
                (ItemsControl.ContainerFromElement(SessionList, button) as FrameworkElement)?.BringIntoView();
        }));
    }

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || FindApplicationName(e.OriginalSource) is not { } row) return;
        row.SelectApplication(extend: true);
        e.Handled = true;
    }

    private static QuickMixerRow? FindApplicationName(object origin)
    {
        for (var element = origin as DependencyObject; element != null;)
        {
            if (element is Button { Name: "ApplicationNameButton", DataContext: QuickMixerRow row }) return row;
            if (element is ListBoxItem) return null;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_closed) return;
        SearchPanel.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && SearchPanel.Visibility == Visibility.Visible) FindApplication(startIndex: 0);
    }

    private void FindApplication(int startIndex)
    {
        if (_page == null) return;
        var rows = _page.Rows;
        string query = SearchBox.Text.Trim();
        if (query.Length != 0)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[(startIndex + i) % rows.Count];
                if (!row.Source.IsApplication || (!row.Source.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && row.Source.ProcessName?.Contains(query, StringComparison.OrdinalIgnoreCase) != true)) continue;
                SearchEmpty.Visibility = Visibility.Collapsed;
                SessionList.SelectedItem = row;
                SessionList.ScrollIntoView(row);
                return;
            }
        }
        SearchEmpty.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!e.IsRepeat && _page?.CloseDeviceChoice() != true)
            {
                if (SearchPanel.Visibility == Visibility.Visible)
                {
                    SearchPanel.Visibility = Visibility.Collapsed;
                    SearchBox.Clear();
                    SessionList.Focus();
                }
                else if (_page?.ClearSelection() == true) SessionList.Focus();
                else Dismiss();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is UIElement source && (ReferenceEquals(source, SearchBox) || SearchBox.IsAncestorOf(source)))
        {
            FindApplication(SessionList.SelectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && FindApplicationName(e.OriginalSource) is { } row)
        {
            if (!e.IsRepeat) row.SelectApplication(extend: true);
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        { if (!e.IsRepeat) KeepOpenToggle.IsChecked = KeepOpenToggle.IsChecked != true; e.Handled = true; }
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control && IsPinned)
        { if (!e.IsRepeat) _layout.ToggleTopmost(); e.Handled = true; }
        else if (e.Key == Key.F5) { _ = RefreshFromUserAsync(); e.Handled = true; }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { MixerTabs.SelectedIndex = 1 - MixerTabs.SelectedIndex; e.Handled = true; }
    }

    internal void Dismiss()
    {
        bool restore = !IsPinned && QuickDevicePickerWindow.GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        Close();
        if (restore && _previousWindow != 0 && IsWindow(_previousWindow)) _ = SetForegroundWindow(_previousWindow);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);
}
