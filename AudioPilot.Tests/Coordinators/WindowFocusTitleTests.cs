using System.Runtime.InteropServices;
using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class WindowFocusTitleTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(255)]
    [InlineData(4096)]
    [InlineData(40000)]
    public void HiddenWindow_TitleReadPreservesLongSuffixesAndBoundsUnusualTitles(int characters)
    {
        string title = new string('x', characters) + "\U0001F3B5 suffix";
        nint window = CreateWindowEx(0, "STATIC", title, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            string captured = WinEventHookWindowFocusMonitor.ReadWindowTitle(window);
            Assert.Equal(title[..Math.Min(title.Length, 32767)], captured);
        }
        finally { Assert.True(DestroyWindow(window)); }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
}
