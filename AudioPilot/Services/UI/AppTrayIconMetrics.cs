using System.Runtime.InteropServices;

namespace AudioPilot.Services.UI;

internal static partial class AppTrayIconMetrics
{
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string? title);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    internal static int GetPixelSize()
    {
        // The notification area belongs to the primary taskbar, not the app's current monitor.
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar == 0 ? 0 : GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = GetDpiForSystem();
        return Math.Clamp(GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96u : dpi), 16, 256);
    }
}
