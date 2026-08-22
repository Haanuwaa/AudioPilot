using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed partial class OverlayWindowDisplayTests
{
    [VisualIntegrationFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    [Trait(TestCategories.Name, TestCategories.VisualWpf)]
    public void Overlay_ReusedAcrossConnectedMonitors_PreservesLogicalWidth()
    {
        TestExecutionGuards.RunSta(() =>
        {
            nint previousContext = SetThreadDpiAwarenessContext(new nint(-4));
            OverlayWindow? window = null;
            try
            {
                Assert.NotEqual(nint.Zero, previousContext);
                var monitors = new List<nint>();
                bool Callback(nint monitor, nint hdc, nint rect, nint data) { monitors.Add(monitor); return true; }
                Assert.True(EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero));
                Assert.NotEmpty(monitors);
                window = new OverlayWindow("Display scaling test");
                window.ApplyDisplayOptions(OverlayPosition.TopRight, 10, 0);
                window.UpdateContent("Current track", "CHALLENGER ADC IS BACK 😤 I AM THE BOSHY😤5v5 Vs Flyquest / Subathon next week", "humzh");
                window.ShowOverlay();
                nint handle = new WindowInteropHelper(window).Handle;
                foreach (nint monitor in monitors.Concat(monitors.AsEnumerable().Reverse()))
                {
                    TestPrivateAccess.SetField(window, "_targetMonitor", monitor);
                    TestPrivateAccess.SetField(window, "_targetMonitorSource", "display-regression-test");
                    typeof(OverlayWindow).GetMethod("PositionWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, null);
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle, CancellationToken.None, TimeSpan.FromSeconds(2));
                    Assert.Equal(monitor, MonitorFromWindow(handle, 2));
                    Assert.True(GetWindowRect(handle, out NativeRect rect));
                    double scale = GetDpiForWindow(handle) / 96d;
                    _ = GetScaleFactorForMonitor(monitor, out int monitorScale);
                    Console.WriteLine($"monitor=0x{monitor:X} scale={scale} monitorScale={monitorScale} windowPx={rect.Right - rect.Left}x{rect.Bottom - rect.Top} widthDip={window.Width} actualDip={window.ActualWidth}");
                    Assert.InRange((rect.Right - rect.Left) / scale, window.Width - 1, window.Width + 1);
                    double expectedWidth = Math.Min(360, window.MaxWidth);
                    Assert.Equal(expectedWidth, window.Width);
                    Assert.InRange(window.ActualWidth, expectedWidth - 1, expectedWidth + 1);
                    Assert.InRange((rect.Bottom - rect.Top) / scale, window.ActualHeight - 1, window.ActualHeight + 1);
                }
            }
            finally
            {
                window?.Close();
                _ = SetThreadDpiAwarenessContext(previousContext);
            }
        });
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint handle);

    [LibraryImport("Shcore.dll")]
    private static partial int GetScaleFactorForMonitor(nint monitor, out int scale);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint handle, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint handle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
