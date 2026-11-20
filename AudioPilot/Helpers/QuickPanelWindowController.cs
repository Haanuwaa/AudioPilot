using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Localization;

namespace AudioPilot.Helpers;

/// <summary>Shared pin, movement, sizing and saved-layout behavior; audio and inventory remain with their window.</summary>
internal sealed partial class QuickPanelWindowController : IDisposable
{
    private readonly Window _window;
    private readonly ToggleButton _pin;
    private readonly MenuItem _top;
    private readonly MenuItem _resize;
    private readonly ResizeGrip _grip;
    private readonly QuickPanelLayoutStore? _store;
    private readonly QuickPanelKind _kind;
    private readonly DispatcherTimer _save;
    private readonly Action _positionTransient;
    private readonly CancellationTokenSource _lifetime = new();
    private QuickPanelLayout _state = new(false, false, null);
    private bool _ready, _applying, _closed;
    private bool _dirty;
    internal bool IsPinned => _pin.IsChecked == true;
    internal bool IsChangingMode => _applying;

    internal QuickPanelWindowController(Window window, ToggleButton pin, MenuItem top, MenuItem resize, ResizeGrip grip,
        QuickPanelKind kind, Action positionTransient, QuickPanelLayoutStore? store)
    {
        _window = window; _pin = pin; _top = top; _resize = resize; _grip = grip; _kind = kind; _positionTransient = positionTransient; _store = store;
        _save = new(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _save.Tick += SaveAfterIdle;
        pin.Checked += PinChanged; pin.Unchecked += PinChanged;
        top.Click += TopChanged;
        window.LocationChanged += GeometryChanged; window.SizeChanged += GeometryChanged;
        window.Closing += Closing;
        ApplyMode();
    }

    internal async Task PrepareAsync()
    {
        if (_closed) return;
        ReleaseMainWindowReference();
        if (_store != null)
        {
            try { _state = await _store.LoadAsync(_kind, _lifetime.Token); }
            catch (OperationCanceledException) when (_closed) { return; }
        }
        if (_closed) return;
        _applying = true;
        try
        {
            _pin.IsChecked = _state.Pinned;
            _top.IsChecked = _state.AlwaysOnTop;
            ApplyMode();
            if (!IsPinned || _state.Placement == null || !MainWindowPlacementService.TryRestore(_window, _state.Placement)) _positionTransient();
        }
        finally { _applying = false; }
        _ready = true;
    }

    private void ApplyMode()
    {
        _window.Topmost = !IsPinned || _top.IsChecked;
        _window.ShowInTaskbar = IsPinned;
        _window.ResizeMode = IsPinned ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        _window.MinWidth = IsPinned ? 360 : 0;
        _window.MinHeight = IsPinned ? 300 : 0;
        _top.IsEnabled = IsPinned;
        _resize.IsEnabled = IsPinned;
        _grip.Visibility = IsPinned ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PinChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _applying || _closed) return;
        if (!IsPinned && MainWindowPlacementService.Capture(_window) is { } current) _state = _state with { Placement = current };
        _applying = true;
        try
        {
            ApplyMode();
            if (IsPinned && _state.Placement is { } saved) _ = MainWindowPlacementService.TryRestore(_window, saved);
            else if (!IsPinned) { _window.WindowState = WindowState.Normal; _positionTransient(); }
        }
        finally { _applying = false; }
        Save();
    }

    private void TopChanged(object sender, RoutedEventArgs e) { if (_ready && !_closed) { ApplyMode(); Save(); } }
    internal void ToggleTopmost() { if (IsPinned) { _top.IsChecked = !_top.IsChecked; ApplyMode(); Save(); } }
    internal void ResetLayout(Action positionDefault)
    {
        if (!_ready || _closed) return;
        _save.Stop();
        _applying = true;
        try
        {
            _window.WindowState = WindowState.Normal;
            positionDefault();
            // Forget the old pinned bounds even when resetting an unpinned panel.
            _state = _state with { Placement = null };
            _dirty = true;
        }
        finally { _applying = false; }
        Save();
    }
    internal void Drag(MouseButtonEventArgs e)
    {
        if (!_ready || _closed || e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || Mouse.Captured != null) return;
        e.Handled = true;
        try { _window.DragMove(); }
        catch (InvalidOperationException) when (Mouse.LeftButton != MouseButtonState.Pressed) { }
    }

    internal void OpenOptions(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        LocalizationContext.SetService(menu, LocalizationContext.GetService(_window));
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    internal void BeginMoveOrResize(bool resize)
    {
        if (_closed || resize && !IsPinned) return;
        _ = _window.Dispatcher.InvokeAsync(() =>
        {
            if (!_closed && _window.IsVisible && (!resize || IsPinned))
                _ = SendMessage(new WindowInteropHelper(_window).Handle, 0x0112, resize ? 0xF000 : 0xF010, 0);
        }, DispatcherPriority.Input, _lifetime.Token);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private void GeometryChanged(object? sender, EventArgs args)
    {
        if (!_ready || _applying || _closed || !IsPinned) return;
        _save.Stop(); _save.Start();
    }
    private void SaveAfterIdle(object? sender, EventArgs e) { _save.Stop(); Save(); }
    private void ReleaseMainWindowReference()
    {
        // WPF can assign the first tray-opened panel as MainWindow during Show.
        // Only the main-window manager owns that application-level reference.
        if (Application.Current is { } app && ReferenceEquals(app.MainWindow, _window)) app.MainWindow = null;
    }
    private void Closing(object? sender, System.ComponentModel.CancelEventArgs e) => Save();
    private void Save()
    {
        if (!_ready || _closed || _applying) return;
        SavedWindowPlacement? placement = IsPinned ? MainWindowPlacementService.Capture(_window) : _state.Placement;
        var next = new QuickPanelLayout(IsPinned, _top.IsChecked, placement);
        if (next != _state) { _state = next; _dirty = true; }
        if (_dirty && _store != null) _ = _store.SaveAsync(_kind, _state);
    }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        ReleaseMainWindowReference();
        _lifetime.Cancel(); _lifetime.Dispose();
        _save.Stop(); _save.Tick -= SaveAfterIdle;
        _pin.Checked -= PinChanged; _pin.Unchecked -= PinChanged;
        _top.Click -= TopChanged;
        _window.LocationChanged -= GeometryChanged; _window.SizeChanged -= GeometryChanged; _window.Closing -= Closing;
    }
}
