using System.Runtime.InteropServices;

namespace AudioPilot.Tests.Platform;

public sealed class NativeInteropLayoutTests
{
    [Fact]
    public void TestProcess_UsesRequestedArchitecture()
    {
        string? expected = Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_ARCHITECTURE");
        if (!string.IsNullOrWhiteSpace(expected))
        {
            Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString(), ignoreCase: true);
        }
    }

    [Theory]
    [InlineData("AudioPilot.Platform.MediaKeyHelper+INPUT", 28, 40, "U", 4, 8)]
    [InlineData("AudioPilot.Platform.MediaKeyHelper+KEYBDINPUT", 16, 24, "dwExtraInfo", 12, 16)]
    [InlineData("AudioPilot.Platform.MediaKeyHelper+MOUSEINPUT", 24, 32, "dwExtraInfo", 20, 24)]
    [InlineData("AudioPilot.Platform.AudioDeviceHelper+PROCESS_BASIC_INFORMATION", 24, 48, "InheritedFromUniqueProcessId", 20, 40)]
    [InlineData("AudioPilot.Platform.NativeWifiScanner+WLAN_NOTIFICATION_DATA", 32, 40, "pData", 28, 32)]
    [InlineData("AudioPilot.Services.Hotkeys.MessageOnlyKeyboardHotkeyHost+NativeMessage", 32, 48, "WParam", 8, 16)]
    [InlineData("AudioPilot.Services.Hotkeys.LowLevelKeyboardHotkeyThreadHost+NativeMessage", 32, 48, "WParam", 8, 16)]
    [InlineData("AudioPilot.Services.Hotkeys.LowLevelMouseHotkeyThreadHost+NativeMessage", 32, 48, "WParam", 8, 16)]
    [InlineData("AudioPilot.Platform.WinEventSteamBigPictureSignalMonitor+NativeMessage", 32, 48, "WParam", 8, 16)]
    [InlineData("AudioPilot.Services.Hotkeys.MessageOnlyKeyboardHotkeyHost+WndClassEx", 48, 80, "lpfnWndProc", 8, 8)]
    [InlineData("AudioPilot.Services.Hotkeys.MessageOnlyKeyboardHotkeyHost+CreateStruct", 48, 80, "lpszName", 36, 56)]
    [InlineData("AudioPilot.Services.Hotkeys.LowLevelKeyboardHotkeyThreadHost+KbdllHookStruct", 20, 24, "DwExtraInfo", 16, 16)]
    [InlineData("AudioPilot.Services.Hotkeys.LowLevelMouseHotkeyThreadHost+MsllHookStruct", 24, 32, "DwExtraInfo", 20, 24)]
    public void NativeStructures_MatchWindowsAbi(string typeName, int size32, int size64, string field, int offset32, int offset64)
    {
        Type type = typeof(AudioDeviceHelper).Assembly.GetType(typeName, throwOnError: true)!;
        Assert.Equal(IntPtr.Size == 8 ? size64 : size32, Marshal.SizeOf(type));
        Assert.Equal(IntPtr.Size == 8 ? offset64 : offset32, Marshal.OffsetOf(type, field).ToInt32());
    }
}
