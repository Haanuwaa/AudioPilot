using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using AudioPilot.Logging;

namespace AudioPilot.Helpers
{
    /// <summary>
    /// Recovers inaccessible windows after display changes using native screen pixels throughout.
    /// Windows with a usable title bar retain their position, including intentional monitor-spanning placement.
    /// </summary>
    internal static partial class WindowPlacementHelper
    {
        private const uint MonitorDefaultToNearest = 2;
        private const uint PreserveSizeZOrderAndActivation = 0x0001 | 0x0004 | 0x0010 | 0x0200;

        /// <summary>
        /// Centers a first presentation in physical pixels, measuring again after the move so
        /// a DPI change from the off-screen staging monitor cannot offset the final position.
        /// </summary>
        internal static bool TryCenterOnPrimaryWorkArea(nint windowHandle)
        {
            if (windowHandle == nint.Zero)
            {
                return false;
            }

            nint monitor = MonitorFromWindow(nint.Zero, 1);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                LogNativeFailure("get-primary-monitor-info");
                return false;
            }

            Rect workArea = info.Work.ToRect();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!GetWindowRect(windowHandle, out NativeRect bounds))
                {
                    LogNativeFailure("get-first-presentation-rect");
                    return false;
                }

                Rect windowRect = bounds.ToRect();
                int left = (int)Math.Round(workArea.Left + Math.Max(0, (workArea.Width - windowRect.Width) / 2));
                int top = (int)Math.Round(workArea.Top + Math.Max(0, (workArea.Height - windowRect.Height) / 2));
                if (!SetWindowPos(windowHandle, nint.Zero, left, top, 0, 0, PreserveSizeZOrderAndActivation))
                {
                    LogNativeFailure("center-first-presentation");
                    return false;
                }

                Logger.Instance.Trace("WindowPlacementHelper", () =>
                    $"window-first-presentation-center | hwnd=0x{windowHandle:X} monitor=0x{monitor:X} pass={attempt + 1} windowPx={windowRect} workPx={workArea} targetPx={left},{top}");
            }

            return true;
        }

        internal static void EnsureAccessible(nint windowHandle)
        {
            if (windowHandle == nint.Zero)
            {
                return;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!GetWindowRect(windowHandle, out NativeRect bounds))
                {
                    LogNativeFailure("get-window-rect");
                    return;
                }

                double scale = Math.Max(1d, GetDpiForWindow(windowHandle) / 96d);
                NativeRect caption = bounds;
                caption.Bottom = Math.Min(bounds.Bottom, bounds.Top + (int)Math.Ceiling(32d * scale));
                nint monitor = MonitorFromRect(in caption, MonitorDefaultToNearest);
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info))
                {
                    LogNativeFailure("get-monitor-info");
                    return;
                }

                Rect windowRect = bounds.ToRect();
                Rect workArea = info.Work.ToRect();
                Point position = GetAccessiblePosition(windowRect, workArea, scale);
                bool needsMove = position != windowRect.TopLeft;
                Logger.Instance.Trace("WindowPlacementHelper", () =>
                    $"window-placement-check | hwnd=0x{windowHandle:X} monitor=0x{monitor:X} dpiScale={scale:F2} windowPx={windowRect} workPx={workArea} recovery={needsMove}");
                if (!needsMove)
                {
                    return;
                }

                if (!SetWindowPos(windowHandle, nint.Zero, (int)Math.Round(position.X), (int)Math.Round(position.Y),
                    0, 0, PreserveSizeZOrderAndActivation))
                {
                    LogNativeFailure("set-window-pos");
                    return;
                }

                Logger.Instance.Info("WindowPlacementHelper", () =>
                    $"window-placement-recovered | hwnd=0x{windowHandle:X} fromPx={windowRect.Left:F0},{windowRect.Top:F0} toPx={position.X:F0},{position.Y:F0}");
            }
        }

        internal static Point GetAccessiblePosition(Rect window, Rect workArea, double scale)
        {
            double captionHeight = Math.Min(window.Height, 32d * scale);
            Rect caption = new(window.Left, window.Top, window.Width, captionHeight);
            Rect visibleCaption = Rect.Intersect(caption, workArea);
            if (!visibleCaption.IsEmpty
                && visibleCaption.Width >= Math.Min(window.Width, 160d * scale)
                && visibleCaption.Height >= Math.Min(captionHeight, 24d * scale))
            {
                return window.TopLeft;
            }

            return new Point(
                Math.Clamp(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width)),
                Math.Clamp(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height)));
        }

        private static void LogNativeFailure(string operation)
        {
            int error = Marshal.GetLastPInvokeError();
            Logger.Instance.Warning("WindowPlacementHelper", $"window-placement-failed | operation={operation} error={error}",
                nameof(EnsureAccessible), new Win32Exception(error));
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(nint windowHandle, out NativeRect rect);

        [LibraryImport("user32.dll")]
        private static partial uint GetDpiForWindow(nint windowHandle);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromRect(in NativeRect rect, uint flags);

        [LibraryImport("user32.dll")]
        private static partial nint MonitorFromWindow(nint windowHandle, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public uint Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }
    }
}
