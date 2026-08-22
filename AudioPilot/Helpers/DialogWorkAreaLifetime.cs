using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AudioPilot.Helpers;

/// <summary>Refits an owned dialog after display or DPI changes, without moving it on ordinary layout updates.</summary>
internal sealed class DialogWorkAreaLifetime : IDisposable
{
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmSettingChange = 0x001A;
    private const int SpiSetWorkArea = 0x002F;
    private readonly Window _dialog;
    private HwndSource? _source;
    private DispatcherOperation? _pending;
    private bool _disposed;

    internal DialogWorkAreaLifetime(Window dialog)
    {
        _dialog = dialog;
        dialog.SourceInitialized += OnSourceInitialized;
        dialog.Closed += OnClosed;
        AttachSource();
    }

    private void OnSourceInitialized(object? sender, EventArgs args) => AttachSource();

    private void AttachSource()
    {
        if (_source != null) return;
        nint handle = new WindowInteropHelper(_dialog).Handle;
        if (handle == 0) return;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is WmDisplayChange or WmDpiChanged || (message == WmSettingChange && wParam == SpiSetWorkArea)) QueueFit();
        return 0;
    }

    private void QueueFit()
    {
        if (_disposed || _pending is { Status: DispatcherOperationStatus.Pending }) return;
        _pending = _dialog.Dispatcher.InvokeAsync(() =>
        {
            _pending = null;
            if (!_disposed && _dialog.IsVisible)
                _ = DialogWindowHelper.TryFitToWorkArea(_dialog, null, center: false);
        }, DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Abort();
        _pending = null;
        _dialog.SourceInitialized -= OnSourceInitialized;
        _dialog.Closed -= OnClosed;
        if (_source is { IsDisposed: false }) _source.RemoveHook(OnMessage);
        _source = null;
    }
}
