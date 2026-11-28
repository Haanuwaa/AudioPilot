using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

/// <summary>Restores before first presentation and coalesces settled window changes into atomic background writes.</summary>
internal sealed partial class MainWindowPlacementService(string path, Logger logger, Action<string, string>? writeFile = null)
    : WindowPlacementStore(path, logger, writeFile)
{
    private bool _recording;
    private Window? _window;
    private DispatcherTimer? _timer;

    internal bool TryRestore(Window window)
    {
        if (Loaded is not { } saved) return false;
        return TryRestore(window, saved);
    }

    internal static bool TryRestore(Window window, SavedWindowPlacement saved)
    {
        if (!IsValid(saved)) return false;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        var placement = new NativePlacement
        {
            Length = (uint)Marshal.SizeOf<NativePlacement>(),
            ShowCommand = saved.Maximized ? 3u : 4u,
            Normal = new() { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
        };
        if (!SetWindowPlacement(handle, in placement)) return false;
        uint dpi = GetDpiForWindow(handle);
        if (dpi != 0 && dpi != saved.Dpi)
        {
            placement.Normal.Right = saved.Left + (int)Math.Clamp(Math.Round((saved.Right - saved.Left) * (double)dpi / saved.Dpi), 64, 32768);
            placement.Normal.Bottom = saved.Top + (int)Math.Clamp(Math.Round((saved.Bottom - saved.Top) * (double)dpi / saved.Dpi), 64, 32768);
            if (!SetWindowPlacement(handle, in placement)) return false;
        }
        WindowPlacementHelper.EnsureAccessible(handle);
        if (!saved.Maximized) _ = DialogWindowHelper.TryFitToWorkArea(window, null, center: false, margin: 0);
        return true;
    }

    internal void Start(Window window)
    {
        if (!ReferenceEquals(_window, window))
        {
            Detach();
            _window = window;
            window.LocationChanged += WindowChanged;
            window.SizeChanged += WindowChanged;
            window.StateChanged += WindowChanged;
            _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += SaveAfterIdle;
        }
        _recording = true;
        if (Capture(window) is { } snapshot)
        {
            lock (Sync)
            {
                Latest = snapshot;
            }
        }
    }

    internal void Pause()
    {
        if (_recording && _window is { } window) CaptureLatest(window);
        _recording = false;
        _timer?.Stop();
        _ = PersistLatestAsync();
    }

    internal Task FlushAsync()
    {
        if (_recording && _window is { } window) CaptureLatest(window);
        _timer?.Stop();
        return PersistLatestAsync();
    }

    internal Task StopAsync()
    {
        Pause();
        Detach();
        return PersistLatestAsync();
    }

    private void WindowChanged(object? sender, EventArgs args)
    {
        if (!_recording || _window is not { IsVisible: true } window) return;
        CaptureLatest(window);
        _timer?.Stop();
        _timer?.Start();
    }

    private void CaptureLatest(Window window)
    {
        if (Capture(window) is not { } snapshot) return;
        lock (Sync) Latest = snapshot;
    }

    internal static bool ShouldRestoreMaximized(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        return handle != 0 && GetWindowPlacement(handle, ref placement)
            && (placement.ShowCommand == 3 || (placement.ShowCommand == 2 && (placement.Flags & 2) != 0));
    }

    internal static SavedWindowPlacement? Capture(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        if (handle == 0 || !GetWindowPlacement(handle, ref placement)) return null;
        var snapshot = new SavedWindowPlacement(placement.Normal.Left, placement.Normal.Top, placement.Normal.Right,
            placement.Normal.Bottom, GetDpiForWindow(handle), placement.ShowCommand == 3 || (placement.ShowCommand == 2 && (placement.Flags & 2) != 0));
        return IsValid(snapshot) ? snapshot : null;
    }

    private void SaveAfterIdle(object? sender, EventArgs args)
    {
        _timer?.Stop();
        _ = PersistLatestAsync();
    }

    private void Detach()
    {
        _timer?.Stop();
        _timer?.Tick -= SaveAfterIdle;
        _timer = null;
        if (_window != null)
        {
            _window.LocationChanged -= WindowChanged;
            _window.SizeChanged -= WindowChanged;
            _window.StateChanged -= WindowChanged;
        }
        _window = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public uint Length; public uint Flags; public uint ShowCommand;
        public NativePoint MinPosition; public NativePoint MaxPosition; public NativeRect Normal;
    }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint handle, ref NativePlacement placement);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint handle, in NativePlacement placement);
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint handle);
}
