using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AudioPilot.Helpers;

internal static partial class QuickPanelPlacement
{
    internal static void Position(Window window, nint previousWindow, double width, double height)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(previousWindow, 2), ref info)) return;
        nint handle = new WindowInteropHelper(window).Handle;
        for (int pass = 0; pass < 2; pass++)
        {
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            int workWidth = info.Work.Right - info.Work.Left, workHeight = info.Work.Bottom - info.Work.Top;
            window.Width = Math.Min(width, Math.Max(1, workWidth / scale - 24));
            window.Height = Math.Min(height, Math.Max(1, workHeight / scale - 24));
            int pixelsWide = (int)Math.Round(window.Width * scale), pixelsHigh = (int)Math.Round(window.Height * scale);
            _ = SetWindowPos(handle, 0, info.Work.Left + (workWidth - pixelsWide) / 2,
                info.Work.Top + (workHeight - pixelsHigh) / 2, pixelsWide, pixelsHigh, 0x0014);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
