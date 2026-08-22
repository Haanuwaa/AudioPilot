using System.Runtime.InteropServices;
using System.Windows.Input;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Hotkeys;

public sealed class NativeHotkeyCallbackContainmentTests
{
    private const int WmKeyDown = 0x0100;
    private const int WmLeftButtonDown = 0x0201;
    private const int VirtualKeyF24 = 0x87;

    [Fact]
    public void KeyboardHookCallback_WhenDispatchThrows_DoesNotCrossNativeBoundary()
    {
        using var loggerScope = new TestLoggerScope(nameof(KeyboardHookCallback_WhenDispatchThrows_DoesNotCrossNativeBoundary), "keyboard-hook-callback-throws.log", LogLevel.Info);
        using var host = new LowLevelKeyboardHotkeyThreadHost(
            loggerScope.Logger,
            static (_, _) => throw new InvalidOperationException("injected keyboard dispatch failure"));
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
        [
            new KeyboardHotkeyBindingSnapshot(
                1,
                HotkeyMainInput.FromKeyboard(Key.F24),
                HotkeyModifierMask.None,
                null,
                "Test")
        ]));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr hookData = AllocateKeyboardHookData(VirtualKeyF24, extraInfo: 0);

        try
        {
            _ = host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, hookData);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(hookData);
        }

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("keyboard-hook-callback-failed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("injected keyboard dispatch failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyboardHookParser_PassesThroughAudioPilotSyntheticMediaInput()
    {
        IntPtr hookData = AllocateKeyboardHookData(
            VirtualKeyF24,
            AppConstants.Hotkeys.SyntheticMediaInputMarker);

        try
        {
            bool parsed = LowLevelKeyboardHotkeyThreadHost.TryParseKeyboardHookInput(
                (IntPtr)WmKeyDown,
                hookData,
                out HotkeyMainInput input);

            Assert.False(parsed);
            Assert.False(input.HasValue);
        }
        finally
        {
            Marshal.FreeHGlobal(hookData);
        }
    }

    [Fact]
    public void MouseHookCallback_WhenDispatchThrows_DoesNotCrossNativeBoundary()
    {
        using var loggerScope = new TestLoggerScope(nameof(MouseHookCallback_WhenDispatchThrows_DoesNotCrossNativeBoundary), "mouse-hook-callback-throws.log", LogLevel.Info);
        using var host = new LowLevelMouseHotkeyThreadHost(
            loggerScope.Logger,
            static (_, _) => throw new InvalidOperationException("injected mouse dispatch failure"));
        host.UpdateSnapshot(MouseHotkeySnapshot.Create(
        [
            new MouseHotkeyBindingSnapshot(
                1,
                HotkeyMainInput.FromMouseButton(MouseButton.Left),
                HotkeyModifierMask.None,
                null,
                "Test")
        ]));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);

        try
        {
            _ = host.InvokeHookCallbackForTests(0, (IntPtr)WmLeftButtonDown, IntPtr.Zero);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
        }

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("mouse-hook-callback-failed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("injected mouse dispatch failure", logText, StringComparison.Ordinal);
    }

    private static IntPtr AllocateKeyboardHookData(int virtualKey, nuint extraInfo)
    {
        int size = 16 + IntPtr.Size;
        IntPtr hookData = Marshal.AllocHGlobal(size);
        for (int offset = 0; offset < size; offset += sizeof(int))
        {
            Marshal.WriteInt32(hookData, offset, 0);
        }

        Marshal.WriteInt32(hookData, virtualKey);
        Marshal.WriteIntPtr(hookData, 16, (IntPtr)extraInfo);
        return hookData;
    }
}
