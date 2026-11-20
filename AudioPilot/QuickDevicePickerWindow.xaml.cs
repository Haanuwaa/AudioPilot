using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot;

public partial class QuickDevicePickerWindow : Window
{
    private readonly QuickDevicePickerViewModel _model = new();
    private readonly Func<CancellationToken, Task<QuickDevicePickerSnapshot>> _readDevices;
    private readonly CancellationTokenSource _readLifetime = new();
    private readonly Func<QuickDevicePickerItem, Task<bool>> _switchDevice;
    private readonly Func<bool, Task<bool>>? _saveViewPreference;
    private readonly DispatcherTimer _refreshTimer;
    private readonly nint _previousWindow;
    private bool _closed;
    private bool _refreshing;
    private bool _ready;
    private bool _refreshFailed;
    private bool _outputReadFailed;
    private bool _inputReadFailed;
    private int _viewPreferenceRevision;
    private int _snapshotRevision;
    private double _desiredHeight = 330;
    private double _deviceRowHeight = 58;
    private readonly QuickPanelWindowController _layout;
    private readonly DialogWorkAreaLifetime _workArea;
    internal bool IsPinned => _layout.IsPinned;

    internal event Action<bool>? DeviceSwitched;

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);
    internal QuickDevicePickerWindow(Func<CancellationToken, Task<QuickDevicePickerSnapshot>> readDevices,
        Func<QuickDevicePickerItem, Task<bool>> switchDevice, nint previousWindow, bool output = true,
        bool showAllDevices = false, Func<bool, Task<bool>>? saveViewPreference = null, LocalizationService? localization = null,
        QuickPanelLayoutStore? layouts = null)
    {
        _readDevices = readDevices;
        _switchDevice = switchDevice;
        _previousWindow = previousWindow;
        _saveViewPreference = saveViewPreference;
        _model.ShowAllDevices = showAllDevices;
        InitializeComponent();
        LocalizationContext.SetService(this, localization ?? LocalizationService.Default);
        DataContext = _model;
        DeviceTabs.SelectedIndex = output ? 0 : 1;
        WindowThemeResolver.ApplyOwnerOrMainWindowTheme(this);
        Opacity = 0;
        _layout = new(this, KeepOpenToggle, AlwaysOnTopItem, ResizeWindowItem, PanelResizeGrip, QuickPanelKind.DevicePicker,
            () => PositionOnTargetMonitor(force: true), layouts);
        _workArea = new(this);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnRefreshTick, Dispatcher);
        _refreshTimer.Stop();
        SourceInitialized += (_, _) => PositionOnTargetMonitor();
        Loaded += OnLoaded;
        Closing += (_, _) => _closed = true;
        StateChanged += (_, _) => { if (_ready && !_closed) { if (WindowState == WindowState.Minimized) _refreshTimer.Stop(); else if (!_refreshTimer.IsEnabled) { _ = RefreshAsync(); _refreshTimer.Start(); } } };
        Closed += (_, _) =>
        {
            _closed = true;
            _layout.Dispose();
            _workArea.Dispose();
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTick;
            _readLifetime.Cancel();
            _readLifetime.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready || _closed) return;
        nint foreground = GetForegroundWindow();
        if (_previousWindow != 0 && foreground != _previousWindow && foreground != new WindowInteropHelper(this).Handle)
        {
            Dismiss(false);
            return;
        }
        await _layout.PrepareAsync();
        if (_closed) return;
        Opacity = 1;
        bool activated = Activate();
        nint handle = new WindowInteropHelper(this).Handle;
        nint activatedForeground = GetForegroundWindow();
        Logger.Instance.Trace("QuickDevicePicker", () => $"device-picker-activation | activated={activated} hwnd=0x{handle:X} foreground=0x{activatedForeground:X}");
        if (!activated || activatedForeground != handle)
            Logger.Instance.Debug("QuickDevicePicker", "device-picker-activation-deferred | reason=foreground-denied");
        SearchBox.Focus();
        _ready = true;
        await RefreshAsync();
        if (!_closed && SearchBox.IsKeyboardFocusWithin && _model.Search.Length == 0 && DeviceList.HasItems)
            DeviceList.Focus();
        if (!_closed && WindowState != WindowState.Minimized) _refreshTimer.Start();
    }

    private void PositionOnTargetMonitor(bool force = false, nint? referenceWindow = null)
    {
        if (!force && _layout?.IsPinned == true) return;
        QuickPanelPlacement.Position(this, referenceWindow ?? _previousWindow, 460, _desiredHeight);
    }

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (IsVisible && WindowState != WindowState.Minimized) await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing || _model.Busy) return;
        _refreshing = true;
        int revision = ++_snapshotRevision;
        try
        {
            CancellationToken token = _readLifetime.Token;
            var snapshot = await _readDevices(token).WaitAsync(token);
            if (!_closed && !_model.Busy && revision == _snapshotRevision)
            {
                _model.Update(snapshot.Items, force: _refreshFailed, outputFailed: snapshot.OutputError != null, inputFailed: snapshot.InputError != null,
                    outputIncomplete: snapshot.OutputIncomplete, inputIncomplete: snapshot.InputIncomplete);
                if (snapshot.OutputError is { } outputError && !_outputReadFailed)
                    Logger.Instance.Warning("QuickDevicePicker", "device-picker-refresh-failed | kind=output", nameof(RefreshAsync), outputError);
                if (snapshot.InputError is { } inputError && !_inputReadFailed)
                    Logger.Instance.Warning("QuickDevicePicker", "device-picker-refresh-failed | kind=input", nameof(RefreshAsync), inputError);
                _outputReadFailed = snapshot.OutputError != null;
                _inputReadFailed = snapshot.InputError != null;
                _refreshFailed = false;
                if (!DeviceList.HasItems && DeviceList.IsKeyboardFocusWithin) SearchBox.Focus();
                UpdatePickerHeight();
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (_closed || revision != _snapshotRevision) return;
            _model.SetMessage("Picker_RefreshFailed");
            if (!_refreshFailed)
                Logger.Instance.Warning("QuickDevicePicker", "device-picker-refresh-failed", nameof(RefreshAsync), ex);
            _refreshFailed = true;
        }
        finally { _refreshing = false; }
    }

    private void UpdatePickerHeight(bool reposition = true)
    {
        if (IsLoaded) UpdateLayout();
        for (int index = 0; index < DeviceList.Items.Count; index++)
        {
            if (DeviceList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem { DesiredSize.Height: > 0 } row)
            {
                _deviceRowHeight = row.DesiredSize.Height;
                break;
            }
        }
        double chromeHeight = IsLoaded ? ActualHeight - DeviceList.ActualHeight : 238;
        double height = Math.Ceiling(chromeHeight + Math.Min(4, _model.DeviceCount) * _deviceRowHeight
            + DeviceList.Padding.Top + DeviceList.Padding.Bottom
            + DeviceList.BorderThickness.Top + DeviceList.BorderThickness.Bottom);
        if (height != _desiredHeight) { _desiredHeight = height; if (reposition) PositionOnTargetMonitor(); }
    }

    private async void OnShowAllDevicesChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        bool showAll = ShowAllDevicesCheckBox.IsChecked == true;
        int revision = ++_viewPreferenceRevision;
        _model.ShowAllDevices = showAll;
        UpdatePickerHeight();
        if (_saveViewPreference != null)
        {
            try
            {
                bool saved = await _saveViewPreference(showAll).WaitAsync(_readLifetime.Token);
                if (!saved && !_closed && revision == _viewPreferenceRevision)
                    _model.SetMessage("Picker_PreferenceFailed");
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception ex)
            {
                if (!_closed && revision == _viewPreferenceRevision) _model.SetMessage("Picker_PreferenceFailed");
                Logger.Instance.Warning("QuickDevicePicker", "device-picker-preference-save-failed", nameof(OnShowAllDevicesChanged), ex);
            }
        }
    }

    private async Task SwitchAsync()
    {
        if (_closed || !_model.CanSwitch || _model.Selected is not { } selected) return;
        ++_snapshotRevision;
        _model.Busy = true;
        bool refreshAfterSwitch = false;
        _model.SetMessage(selected.Available ? "Picker_Switching" : "Picker_Connecting");
        try
        {
            CancellationToken token = _readLifetime.Token;
            QuickDevicePickerSnapshot snapshot = await _readDevices(token).WaitAsync(token);
            if (_closed) return;
            if ((selected.Output ? snapshot.OutputError : snapshot.InputError) != null)
            {
                _model.SetMessage(selected.Output ? "Picker_VerifyOutputFailed" : "Picker_VerifyInputFailed");
                return;
            }
            var current = snapshot.Items.FirstOrDefault(item => item.Output == selected.Output && string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            if (current == null || !current.CanSelect || (_model.ShowAllDevices ? !current.Available : !current.Configured))
            { _model.SetMessage("Picker_Unavailable"); return; }
            if (current.Current)
            {
                refreshAfterSwitch = KeepOpenToggle.IsChecked == true;
                if (!refreshAfterSwitch) Dismiss();
                return;
            }
            bool success = await _switchDevice(current);
            if (success) DeviceSwitched?.Invoke(current.Output);
            if (_closed) return;
            if (success && KeepOpenToggle.IsChecked == true)
            {
                refreshAfterSwitch = true;
                _model.SetMessage("Common_Empty");
            }
            else if (success) Dismiss();
            else _model.SetMessage("Picker_SwitchFailed");
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) _model.SetMessage("Picker_SwitchError");
            Logger.Instance.Warning("QuickDevicePicker", "device-picker-switch-failed", nameof(SwitchAsync), ex);
        }
        finally
        {
            _model.Busy = false;
            if (refreshAfterSwitch && !_closed) await RefreshAsync();
        }
    }

    internal void Dismiss(bool restoreFocus = true)
    {
        if (_closed) return;
        bool ownedFocus = !IsPinned && GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        Close();
        if (restoreFocus && ownedFocus && IsWindow(_previousWindow)) _ = SetForegroundWindow(_previousWindow);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) { if (_ready && KeepOpenToggle.IsChecked != true && _layout?.IsChangingMode != true) Dismiss(false); }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e) => _layout.Drag(e);
    private void OnWindowOptions(object sender, RoutedEventArgs e) => _layout.OpenOptions((Button)sender);
    private void OnMoveWindow(object sender, RoutedEventArgs e) => _layout.BeginMoveOrResize(false);
    private void OnResizeWindow(object sender, RoutedEventArgs e) => _layout.BeginMoveOrResize(true);
    private void OnResetWindowLayout(object sender, RoutedEventArgs e) => _layout.ResetLayout(() =>
    {
        nint reference = new WindowInteropHelper(this).Handle;
        PositionOnTargetMonitor(force: true, referenceWindow: reference);
        UpdatePickerHeight(reposition: false);
        PositionOnTargetMonitor(force: true, referenceWindow: reference);
    });
    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();
    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_closed || _model.Busy) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }
    private async void OnSwitchClick(object sender, RoutedEventArgs e) => await SwitchAsync();
    private async void OnDeviceClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DeviceList, e.OriginalSource as DependencyObject) is ListBoxItem item && item.DataContext is QuickDevicePickerItem device)
        {
            _model.Selected = device;
            await SwitchAsync();
        }
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem != null) DeviceList.ScrollIntoView(DeviceList.SelectedItem);
    }
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DeviceTabs) return;
        _model.Output = DeviceTabs.SelectedIndex == 0;
        if (_ready)
        {
            UpdatePickerHeight();
            if (DeviceList.HasItems) DeviceList.Focus();
            else SearchBox.Focus();
            _ = RefreshAsync();
        }
    }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        { if (!e.IsRepeat) KeepOpenToggle.IsChecked = KeepOpenToggle.IsChecked != true; e.Handled = true; }
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control && IsPinned)
        { if (!e.IsRepeat) _layout.ToggleTopmost(); e.Handled = true; }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control && !_model.Busy)
        { e.Handled = true; DeviceTabs.SelectedIndex = 1 - DeviceTabs.SelectedIndex; }
        else if (e.Key is Key.Up or Key.Down && !_model.Busy && Keyboard.Modifiers == ModifierKeys.None
            && IsSelectionSource(e.OriginalSource))
        { e.Handled = true; _model.Move(e.Key == Key.Up ? -1 : 1); }
        else if (e.Key == Key.Enter && IsSelectionSource(e.OriginalSource))
        { e.Handled = true; await SwitchAsync(); }
    }

    private bool IsSelectionSource(object origin) => origin is UIElement source
        && (ReferenceEquals(source, SearchBox) || ReferenceEquals(source, DeviceList)
            || SearchBox.IsAncestorOf(source) || DeviceList.IsAncestorOf(source));

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_model.Busy || SearchBox.IsKeyboardFocusWithin || (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase && e.Text == " ")
            || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        SearchBox.Focus();
        SearchBox.SelectedText = e.Text;
        SearchBox.CaretIndex = SearchBox.SelectionStart + SearchBox.SelectionLength;
        SearchBox.SelectionLength = 0;
        e.Handled = true;
    }
}
