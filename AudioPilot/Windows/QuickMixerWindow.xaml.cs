using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    private Func<bool>? _canPresent;
    private readonly bool _dismissOnDeactivate;
    private readonly Dictionary<AudioMixerMode, QuickMixerPage> _pages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private MixerAppIconProvider? _icons;
    private readonly DispatcherTimer _meterTimer;
    private readonly DialogWorkAreaLifetime _workArea;
    private readonly QuickPanelWindowController _layout;
    private readonly QuickPanelLayoutStore? _layouts;
    private readonly IAppDialogService? _dialogs;
    private QuickMixerPreferences _preferences = QuickMixerPreferences.Default;
    private QuickMixerRow? _pinMenuRow;
    private IDisposable? _presentation, _meterLease;
    private CancellationTokenSource? _refreshCancellation;
    private QuickMixerPage? _page;
    private VirtualizingStackPanel? _itemsHost;
    private ScrollViewer? _scroll;
    private bool _ready, _closed, _restoring, _confirmingReset;
    private int _refreshRevision;
    private long _holdOrderUntil;
    internal AudioMixerMode Mode => MixerTabs.SelectedIndex == 1 ? AudioMixerMode.Input : AudioMixerMode.Output;
    internal bool IsMeterPolling => _meterTimer.IsEnabled;
    internal bool IsPinned => _layout.IsPinned;

    internal QuickMixerWindow(Func<AudioMixerMode, MixerViewModel> getMixer, Func<AudioMixerMode, IDisposable> acquirePresentation,
        Func<AudioMixerMode, CancellationToken, Task> refresh, AudioMeterSampler meters, nint previousWindow,
        AudioMixerMode mode = AudioMixerMode.Output, LocalizationService? localization = null, bool dismissOnDeactivate = true,
        QuickPanelLayoutStore? layouts = null, AppViewModel? muteControls = null, IAppDialogService? dialogs = null, Func<bool>? canPresent = null)
    {
        _getMixer = getMixer;
        _acquirePresentation = acquirePresentation;
        _refresh = refresh;
        _meters = meters;
        _previousWindow = previousWindow;
        _dismissOnDeactivate = dismissOnDeactivate;
        _layouts = layouts;
        _dialogs = dialogs;
        _canPresent = canPresent;
        InitializeComponent();
        if (muteControls != null)
            DeafenToggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(AppViewModel.Deafen)) { Source = muteControls, Mode = BindingMode.TwoWay });
        else DeafenToggle.IsEnabled = false;
        SessionList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        SessionList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRowButtonClick));
        VirtualizingStackPanel.AddCleanUpVirtualizedItemHandler(SessionList, OnRowVirtualized);
        LocalizationContext.SetService(this, localization ?? LocalizationService.Default);
        MixerTabs.SelectedIndex = mode == AudioMixerMode.Input ? 1 : 0;
        UpdateSortMenu();
        LocalizationContext.SetText(HelpText, TextBlock.TextProperty, new(mode == AudioMixerMode.Input ? "QuickMixer_InputHelp" : "QuickMixer_Help"));
        WindowThemeResolver.ApplyOwnerOrMainWindowTheme(this);
        Opacity = 0;
        if (canPresent != null) ShowActivated = false;
        _layout = new(this, KeepOpenToggle, AlwaysOnTopItem, ResizeWindowItem, QuickPanelKind.Mixer,
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
        try
        {
            await _layout.PrepareAsync();
            if (_closed) return;
            if (_layouts != null)
            {
                try { _preferences = await _layouts.LoadMixerPreferencesAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_closed) { return; }
            }
            if (_closed) return;
            UpdateSortMenu();
            Func<bool>? canPresent = _canPresent;
            _canPresent = null;
            if (canPresent?.Invoke() == false)
            {
                Close();
                return;
            }
            Opacity = 1;
            _ = Activate();
            _ready = true;
            ActivatePage();
            SessionList.Focus();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            Logger.Instance.Warning("QuickMixer", "quick-mixer-load-failed", nameof(OnLoaded), ex);
            if (!_closed) Close();
        }
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
            if (!_pages.TryGetValue(Mode, out _page))
                _pages.Add(Mode, _page = new(_getMixer(Mode), Dispatcher, _preferences, IsOrderHeld));
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
        AppOptionsMenu.IsOpen = false;
        _pinMenuRow = null;
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
        if (_closed || !IsVisible || _page is not { } page) return;
        _itemsHost ??= FindChild<VirtualizingStackPanel>(SessionList);
        _meters.TryGetLatest(Mode, out AudioMeterSnapshot? snapshot);
        if (_itemsHost != null)
        {
            foreach (UIElement child in _itemsHost.Children)
            {
                if (child is not ListBoxItem { Content: QuickMixerRow row }) continue;
                row.UpdateMeter(snapshot, Mode);
                if (row.Source.IsApplication && row.CanRequestIcon) _ = LoadIconAsync(row);
            }
        }
        page.UpdatePlaybackOrder(snapshot);
        page.ApplyPendingOrder();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !ReferenceEquals(e.Source, MixerTabs)) return;
        UpdateSortMenu();
        ActivatePage();
    }
    private void OnRowSelected(object sender, SelectionChangedEventArgs e) { if (!_restoring && _page != null && ReferenceEquals(e.Source, SessionList)) _page.SelectedIdentity = (SessionList.SelectedItem as QuickMixerRow)?.Identity; }
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        try { await RefreshFromUserAsync(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { Logger.Instance.Warning("QuickMixer", "quick-mixer-refresh-click-failed", nameof(OnRefreshClick), ex); }
    }

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
        if (!_dismissOnDeactivate || !_ready || _closed || _confirmingReset || IsPinned || _layout.IsChangingMode) return;
        if (!HasOpenMenu()) { Close(); return; }
        // A popup can close as a consequence of clicking outside the flyout.
        // Let WPF settle that transition before deciding whether the flyout lost focus.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_closed && !_confirmingReset && !IsActive && !IsPinned && !_layout.IsChangingMode && !HasOpenMenu()) Close();
        }));
    }

    private bool HasOpenMenu() => _page?.IsChoosingDevice == true || AppOptionsMenu.IsOpen || MixerOptionsMenu.IsOpen;

    private bool IsOrderHeld() => IsOrderHeldForInput(Mouse.DirectlyOver as DependencyObject, Keyboard.FocusedElement as DependencyObject);

    internal bool IsOrderHeldForInput(DependencyObject? pointer, DependencyObject? focus) =>
        _confirmingReset || HasOpenMenu() || Mouse.Captured != null || Environment.TickCount64 < _holdOrderUntil || IsAppControl(pointer) || IsAppControl(focus);

    private bool IsAppControl(DependencyObject? element) => element != null
        && ItemsControl.ContainerFromElement(SessionList, element) is ListBoxItem { Content: QuickMixerRow { Source.IsApplication: true } } row
        && !ReferenceEquals(row, element);

    private void OnListPointerInput(object sender, MouseEventArgs e) => HoldOrderBriefly();
    private void HoldOrderBriefly() => _holdOrderUntil = Environment.TickCount64 + 500;

    private void UpdateSortMenu()
    {
        var visibility = Mode == AudioMixerMode.Output ? Visibility.Visible : Visibility.Collapsed;
        SortAppsMenu.Visibility = SortOptionsSeparator.Visibility = visibility;
        SortByNameItem.IsChecked = !_preferences.PlayingFirst;
        SortPlayingFirstItem.IsChecked = _preferences.PlayingFirst;
        var selectedSort = new LocalizedText(_preferences.PlayingFirst ? "QuickMixer_PlayingFirst" : "QuickMixer_SortByName");
        LocalizationContext.SetText(SortAppsMenu, HeaderedItemsControl.HeaderProperty, new("QuickMixer_SortApps", selectedSort));
    }

    private void OnSortApps(object sender, RoutedEventArgs e)
    {
        if (Mode != AudioMixerMode.Output) return;
        bool playingFirst = ReferenceEquals(sender, SortPlayingFirstItem);
        if (_preferences.PlayingFirst != playingFirst) SavePreferences(_preferences with { PlayingFirst = playingFirst });
        UpdateSortMenu();
    }

    private void OnAppMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool keyboard = e.CursorLeft < 0;
        var origin = e.OriginalSource as DependencyObject;
        var container = origin == null ? null : ItemsControl.ContainerFromElement(SessionList, origin) as ListBoxItem;
        var row = container?.Content as QuickMixerRow ?? (keyboard ? SessionList.SelectedItem as QuickMixerRow : null);
        if (row?.Source.MixerPinKey is not { } key || (!keyboard && HasSliderThumbAncestor(origin)))
        {
            e.Handled = true;
            return;
        }
        _pinMenuRow = row;
        LocalizationContext.SetService(AppOptionsMenu, LocalizationContext.GetService(this));
        LocalizationContext.SetText(PinAppItem, HeaderedItemsControl.HeaderProperty,
            new(row.IsPinnedApp ? "QuickMixer_UnpinApp" : "QuickMixer_PinApp"));
        PinAppItem.IsEnabled = _preferences.PinnedApps.Contains(key) || _preferences.PinnedApps.Count < QuickMixerPreferences.MaxPinnedApps;
    }

    private static bool HasSliderThumbAncestor(DependencyObject? origin)
    {
        for (var element = origin; element != null && element is not ListBoxItem;)
        {
            if (element is Thumb) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnAppMenuClosed(object sender, RoutedEventArgs e) => _pinMenuRow = null;

    private void OnPinApp(object sender, RoutedEventArgs e)
    {
        var row = _pinMenuRow;
        AppOptionsMenu.IsOpen = false;
        if (row?.Source.MixerPinKey is not { } key || _page?.Rows.Contains(row) != true) return;
        var pins = _preferences.PinnedApps;
        var updated = pins.Remove(key);
        if (updated.Count == pins.Count)
        {
            if (pins.Count >= QuickMixerPreferences.MaxPinnedApps) return;
            updated = pins.Add(key);
        }
        SavePreferences(_preferences with { PinnedApps = updated });
    }

    private void SavePreferences(QuickMixerPreferences preferences, QuickPanelLayout? layout = null)
    {
        _preferences = preferences;
        foreach (var page in _pages.Values) page.SetPreferences(preferences);
        if (_layouts != null) _ = _layouts.SaveMixerPreferencesAsync(preferences, layout);
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e) => _layout.Drag(e);
    private void OnWindowOptions(object sender, RoutedEventArgs e) => _layout.OpenOptions((Button)sender);
    private void OnMoveWindow(object sender, RoutedEventArgs e) => _layout.BeginMoveOrResize(false);
    private void OnResizeWindow(object sender, RoutedEventArgs e) => _layout.BeginMoveOrResize(true);
    private void OnResetWindowLayout(object sender, RoutedEventArgs e) => _layout.ResetLayout(
        () => QuickPanelPlacement.Position(this, new WindowInteropHelper(this).Handle, 460, 500));

    private async void OnResetMixerPreferences(object sender, RoutedEventArgs e) => await ResetMixerPreferencesAsync();

    private async Task ResetMixerPreferencesAsync()
    {
        if (!_ready || _closed || _confirmingReset) return;
        _confirmingReset = true;
        CancellationToken token = _lifetime.Token;
        try
        {
            var english = LocalizationService.English;
            var request = AppDialogRequest.Confirm(english["QuickMixer_ResetPreferencesMessage"], english["QuickMixer_ResetPreferencesTitle"],
                AppDialogKind.Warning, "_Reset", "_Cancel", AppDialogActionStyle.Destructive, owner: this) with
            {
                MessageResource = new("QuickMixer_ResetPreferencesMessage"),
                CaptionResource = new("QuickMixer_ResetPreferencesTitle"),
            };
            AppDialogResult result = await (_dialogs ?? AppDialogServiceProvider.Current).ShowAsync(request, token);
            if (_closed || result != AppDialogResult.Confirmed || _layout.ResetWindowOptions() is not { } layout) return;
            SavePreferences(QuickMixerPreferences.Default, layout);
            UpdateSortMenu();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Instance.Warning("QuickMixer", "quick-mixer-preferences-reset-failed", nameof(ResetMixerPreferencesAsync), ex);
        }
        finally { _confirmingReset = false; }
    }

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
        HoldOrderBriefly();
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
