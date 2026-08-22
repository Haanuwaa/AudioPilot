using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.ViewModels;

namespace AudioPilot;

public partial class QuickDevicePickerWindow : Window
{
    private readonly QuickDevicePickerViewModel _model = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<QuickDevicePickerItem>>> _readDevices;
    private readonly CancellationTokenSource _readLifetime = new();
    private readonly Func<QuickDevicePickerItem, Task<bool>> _switchDevice;
    private readonly DispatcherTimer _refreshTimer;
    private readonly nint _previousWindow;
    private bool _closed;
    private bool _refreshing;
    private bool _ready;
    private bool _refreshFailed;
    private double _desiredHeight = 330;
    private double _deviceRowHeight = 58;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    internal QuickDevicePickerWindow(Func<CancellationToken, Task<IReadOnlyList<QuickDevicePickerItem>>> readDevices,
        Func<QuickDevicePickerItem, Task<bool>> switchDevice, nint previousWindow)
    {
        _readDevices = readDevices;
        _switchDevice = switchDevice;
        _previousWindow = previousWindow;
        InitializeComponent();
        DataContext = _model;
        WindowThemeResolver.ApplyOwnerOrMainWindowTheme(this);
        Opacity = 0;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnRefreshTick, Dispatcher);
        _refreshTimer.Stop();
        SourceInitialized += (_, _) => PositionOnTargetMonitor();
        Loaded += OnLoaded;
        Closing += (_, _) => _closed = true;
        Closed += (_, _) =>
        {
            _closed = true;
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTick;
            _readLifetime.Cancel();
            _readLifetime.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        nint foreground = GetForegroundWindow();
        if (_previousWindow != 0 && foreground != _previousWindow && foreground != new WindowInteropHelper(this).Handle)
        {
            Dismiss(false);
            return;
        }
        PositionOnTargetMonitor();
        Opacity = 1;
        bool activated = Activate();
        nint handle = new WindowInteropHelper(this).Handle;
        nint activatedForeground = GetForegroundWindow();
        Logger.Instance.Trace("QuickDevicePicker", () => $"device-picker-activation | activated={activated} hwnd=0x{handle:X} foreground=0x{activatedForeground:X}");
        if (!activated || activatedForeground != handle)
            Logger.Instance.Debug("QuickDevicePicker", "device-picker-activation-deferred | reason=foreground-denied");
        DeviceList.Focus();
        _ready = true;
        await RefreshAsync();
        if (!_closed) _refreshTimer.Start();
    }

    private void PositionOnTargetMonitor()
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(_previousWindow, 2), ref info)) return;
        var work = info.Work;
        int workWidth = work.Right - work.Left;
        int workHeight = work.Bottom - work.Top;
        nint handle = new WindowInteropHelper(this).Handle;
        for (int pass = 0; pass < 2; pass++)
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = Math.Min(460, Math.Max(1, workWidth / scale - 24));
            Height = Math.Min(_desiredHeight, Math.Max(1, workHeight / scale - 24));
            int width = (int)Math.Round(Width * scale);
            int height = (int)Math.Round(Height * scale);
            _ = SetWindowPos(handle, 0, work.Left + (workWidth - width) / 2, work.Top + (workHeight - height) / 2, width, height, 0x0014);
        }
    }

    private async void OnRefreshTick(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing || _model.Busy) return;
        _refreshing = true;
        try
        {
            CancellationToken token = _readLifetime.Token;
            var snapshot = await _readDevices(token).WaitAsync(token);
            if (!_closed && !_model.Busy)
            {
                _model.Update(snapshot, force: _refreshFailed);
                _refreshFailed = false;
                if (IsLoaded) UpdateLayout();
                if (DeviceList.SelectedItem is { } selectedItem
                    && DeviceList.ItemContainerGenerator.ContainerFromItem(selectedItem) is ListBoxItem { DesiredSize.Height: > 0 } row)
                    _deviceRowHeight = row.DesiredSize.Height;
                double chromeHeight = IsLoaded ? ActualHeight - DeviceList.ActualHeight : 238;
                double height = Math.Ceiling(chromeHeight
                    + Math.Max(1, Math.Min(4, snapshot.Count(item => item.Output == _model.Output))) * _deviceRowHeight
                    + DeviceList.Padding.Top + DeviceList.Padding.Bottom
                    + DeviceList.BorderThickness.Top + DeviceList.BorderThickness.Bottom);
                if (height != _desiredHeight) { _desiredHeight = height; PositionOnTargetMonitor(); }
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) _model.Message = "Could not refresh devices. Retrying…";
            if (!_refreshFailed && !_closed)
                Logger.Instance.Warning("QuickDevicePicker", "device-picker-refresh-failed", nameof(RefreshAsync), ex);
            _refreshFailed = true;
        }
        finally { _refreshing = false; }
    }

    private async Task SwitchAsync()
    {
        if (_closed || !_model.CanSwitch || _model.Selected is not { } selected) return;
        _model.Busy = true;
        _model.Message = selected.Available ? "Switching…" : "Connecting…";
        try
        {
            CancellationToken token = _readLifetime.Token;
            var current = (await _readDevices(token).WaitAsync(token)).FirstOrDefault(item => item.Output == selected.Output && string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            if (_closed) return;
            if (current == null || !current.CanSelect) { _model.Message = "This device is no longer available."; return; }
            if (current.Current) { Dismiss(); return; }
            bool success = await _switchDevice(current);
            if (_closed) return;
            if (success) Dismiss();
            else _model.Message = "Could not switch. Check the device connection and try again.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) _model.Message = "Could not switch. Please try again.";
            Logger.Instance.Warning("QuickDevicePicker", "device-picker-switch-failed", nameof(SwitchAsync), ex);
        }
        finally { _model.Busy = false; }
    }

    internal void Dismiss(bool restoreFocus = true)
    {
        if (_closed) return;
        bool ownedFocus = GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        Close();
        if (restoreFocus && ownedFocus && IsWindow(_previousWindow)) _ = SetForegroundWindow(_previousWindow);
    }

    private void OnDeactivated(object? sender, EventArgs e) { if (_ready) Dismiss(false); }
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
            DeviceList.Focus();
            _ = RefreshAsync();
        }
    }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control && !_model.Busy)
        { e.Handled = true; DeviceTabs.SelectedIndex = 1 - DeviceTabs.SelectedIndex; }
        else if (e.Key is Key.Up or Key.Down && !_model.Busy)
        { e.Handled = true; _model.Move(e.Key == Key.Up ? -1 : 1); }
        else if (e.Key == Key.Enter) { e.Handled = true; await SwitchAsync(); }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_model.Busy || SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        SearchBox.Focus();
        SearchBox.SelectedText = e.Text;
        SearchBox.CaretIndex = SearchBox.SelectionStart + SearchBox.SelectionLength;
        SearchBox.SelectionLength = 0;
        e.Handled = true;
    }
}
