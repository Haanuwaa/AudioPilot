using System.ComponentModel;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Helpers;

internal readonly record struct WindowLayoutMonitor(int Left, int Top, int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight, int Dpi);

internal static partial class WindowPlacementHelper
{
    internal static WindowLayoutMonitor GetLayoutMonitor(WindowLayoutSnapshot? bounds = null, nint window = 0)
    {
        nint monitor;
        if (bounds != null)
        {
            var rect = new NativeRect { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
            monitor = MonitorFromRect(in rect, MonitorDefaultToNearest);
        }
        else if (window != 0) monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        else
        {
            monitor = GetLayoutCursorPos(out LayoutPoint point) ? LayoutMonitorFromPoint(point, MonitorDefaultToNearest) : MonitorFromWindow(0, 1);
        }
        return ReadLayoutMonitor(monitor, window);
    }

    internal static WindowLayoutMonitor GetLayoutMonitorAt(int x, int y, nint window = 0) =>
        ReadLayoutMonitor(LayoutMonitorFromPoint(new LayoutPoint { X = x, Y = y }, MonitorDefaultToNearest), window);

    private static WindowLayoutMonitor ReadLayoutMonitor(nint monitor, nint window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        uint dpi = window != 0 && monitor == MonitorFromWindow(window, MonitorDefaultToNearest) ? GetDpiForWindow(window) : 0;
        if (dpi == 0 && GetLayoutMonitorDpi(monitor, 0, out uint monitorDpi, out _) >= 0) dpi = monitorDpi;
        int effectiveDpi = dpi is >= 48 and <= 960 ? (int)dpi : 96;
        return new(info.Monitor.Left, info.Monitor.Top, info.Work.Left, info.Work.Top,
            info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, effectiveDpi);
    }

    internal static void ValidateLayoutChange(WindowLayoutChange change)
    {
        if (change.Reset && (change.X != null || change.Y != null || change.Width != null || change.Height != null))
            throw new ArgumentException("Reset cannot include position or size options.", nameof(change));
        if (!change.Reset && change.X == null && change.Y == null && change.Width == null && change.Height == null)
            throw new ArgumentException("Provide at least one position or size option.", nameof(change));
        if (change.X is < -1000000 or > 1000000 || change.Y is < -1000000 or > 1000000)
            throw new ArgumentOutOfRangeException(nameof(change), "Coordinates must be between -1000000 and 1000000 pixels.");
        if (change.Width is < 1 or > 32768 || change.Height is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(change), "Dimensions must be between 1 and 32768 pixels.");
    }

    /// <summary>Resolves partial screen-pixel edits while retaining logical size across monitor DPI changes.</summary>
    internal static WindowLayoutSnapshot ResolveLayout(WindowLayoutSnapshot? current, WindowLayoutChange change, WindowLayoutMonitor monitor)
    {
        ValidateLayoutChange(change);
        if (monitor.WorkWidth <= 0 || monitor.WorkHeight <= 0 || monitor.Dpi is < 48 or > 960)
            throw new ArgumentException("The monitor has no usable work area.", nameof(monitor));
        double scale = monitor.Dpi / 96d;
        double currentScale = monitor.Dpi / (double)(current?.Dpi ?? monitor.Dpi);
        int width = change.Reset ? Pixels(AppConstants.WindowLayout.DefaultWidth * scale)
            : change.Width ?? (current != null ? Pixels(current.Width * currentScale) : Pixels(AppConstants.WindowLayout.DefaultWidth * scale));
        int height = change.Reset ? Pixels(AppConstants.WindowLayout.DefaultHeight * scale)
            : change.Height ?? (current != null ? Pixels(current.Height * currentScale) : Pixels(AppConstants.WindowLayout.DefaultHeight * scale));
        width = Math.Clamp(width, Math.Min(monitor.WorkWidth, Pixels(AppConstants.WindowLayout.MinimumWidth * scale)), monitor.WorkWidth);
        height = Math.Clamp(height, Math.Min(monitor.WorkHeight, Pixels(AppConstants.WindowLayout.MinimumHeight * scale)), monitor.WorkHeight);
        int x = change.Reset ? monitor.WorkLeft + (monitor.WorkWidth - width) / 2 : change.X ?? current?.X ?? monitor.WorkLeft + (monitor.WorkWidth - width) / 2;
        int y = change.Reset ? monitor.WorkTop + (monitor.WorkHeight - height) / 2 : change.Y ?? current?.Y ?? monitor.WorkTop + (monitor.WorkHeight - height) / 2;
        return new(Math.Clamp(x, monitor.WorkLeft, monitor.WorkLeft + monitor.WorkWidth - width),
            Math.Clamp(y, monitor.WorkTop, monitor.WorkTop + monitor.WorkHeight - height), width, height, monitor.Dpi,
            "normal", current?.Visible ?? false, current?.Source ?? "saved", false);
    }

    internal static void ApplyLayout(nint window, WindowLayoutSnapshot layout)
    {
        if (!SetWindowPos(window, 0, layout.X, layout.Y, 0, 0, PreserveSizeZOrderAndActivation))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        const uint resizeFlags = 0x0004 | 0x0010 | 0x0200;
        uint dpiBeforeResize = GetDpiForWindow(window);
        if (!SetWindowPos(window, 0, layout.X, layout.Y, layout.Width, layout.Height, resizeFlags))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        // A narrower rectangle can cross monitors only when resized, and WM_DPICHANGED can scale it during that call.
        if (GetDpiForWindow(window) != dpiBeforeResize
            && !SetWindowPos(window, 0, layout.X, layout.Y, layout.Width, layout.Height, resizeFlags))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static int Pixels(double value) => (int)Math.Clamp(Math.Round(value), 1, 32768);

    [StructLayout(LayoutKind.Sequential)]
    private struct LayoutPoint { public int X; public int Y; }
    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static partial nint LayoutMonitorFromPoint(LayoutPoint point, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLayoutCursorPos(out LayoutPoint point);
    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static partial int GetLayoutMonitorDpi(nint monitor, int type, out uint x, out uint y);
}
