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
    public void KeyboardHold_TracksEachPressAndReleaseAcrossSnapshotChanges()
    {
        var callbacks = new List<Action>();
        var holds = new List<Func<bool>>();
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None) { OnPressed = holds.Add };
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (binding, _) => callbacks.Add(binding.Callback!));
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
        [new KeyboardHotkeyBindingSnapshot(1, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "Hold", HoldState: state)]));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr data = AllocateKeyboardHookData(VirtualKeyF24, 0);
        try
        {
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            Action firstPress = Assert.Single(callbacks);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)0x0101, data));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            firstPress();
            callbacks[1]();
            Assert.False(holds[0]());
            Assert.True(holds[1]());
            state.Release();
            var replacement = new HotkeyHeldInputState(HotkeyModifierMask.None) { OnPressed = holds.Add };
            host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
                [new KeyboardHotkeyBindingSnapshot(2, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "New hold", HoldState: replacement)]));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            Assert.Equal(2, callbacks.Count);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)0x0101, data));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            callbacks[2]();
            Assert.True(holds[2]());
            host.UpdateSnapshot(KeyboardHotkeySnapshot.Empty);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)0x0101, data));
            Assert.False(holds[1]());
            Assert.False(holds[2]());
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData(MouseButton.Left, 0x0201, 0x0202, 0)]
    [InlineData(MouseButton.XButton1, 0x020B, 0x020C, 1)]
    public void MouseHold_ReleaseSurvivesBindingRemoval(MouseButton button, int down, int up, int nativeButton)
    {
        Func<bool>? held = null;
        int calls = 0;
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None) { OnPressed = value => { held = value; calls++; } };
        using var host = new LowLevelMouseHotkeyThreadHost(Logger.Instance, (binding, _) => binding.Callback!());
        host.UpdateSnapshot(MouseHotkeySnapshot.Create(
        [new MouseHotkeyBindingSnapshot(1, HotkeyMainInput.FromMouseButton(button), HotkeyModifierMask.None, null, "Hold", state)]));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr data = Marshal.AllocHGlobal(32);
        for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(data, offset, 0);
        Marshal.WriteInt32(data, 8, nativeButton << 16);
        try
        {
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)down, data));
            Assert.True(held!());
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)down, data));
            Assert.Equal(1, calls);
            state.Release();
            var replacement = new HotkeyHeldInputState(HotkeyModifierMask.None) { OnPressed = value => { held = value; calls++; } };
            host.UpdateSnapshot(MouseHotkeySnapshot.Create(
                [new MouseHotkeyBindingSnapshot(2, HotkeyMainInput.FromMouseButton(button), HotkeyModifierMask.None, null, "New hold", replacement)]));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)down, data));
            Assert.Equal(1, calls);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)up, data));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)down, data));
            Assert.Equal(2, calls);
            Assert.True(held());
            host.UpdateSnapshot(MouseHotkeySnapshot.Empty);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)up, data));
            Assert.False(held());
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData(1, MouseButton.XButton1)]
    [InlineData(2, MouseButton.XButton2)]
    public void SideButtonHook_ConsumesOnlyMatchedPressAndItsRelease(int nativeButton, MouseButton button)
    {
        int calls = 0;
        using var host = new LowLevelMouseHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
        host.UpdateSnapshot(MouseHotkeySnapshot.Create(
        [
            new MouseHotkeyBindingSnapshot(1, HotkeyMainInput.FromMouseButton(button), HotkeyModifierMask.None, null, "Side button")
        ]));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr data = Marshal.AllocHGlobal(32);
        for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(data, offset, 0);
        Marshal.WriteInt32(data, 8, nativeButton << 16);
        try
        {
            Assert.Equal(IntPtr.Zero, host.InvokeHookCallbackForTests(0, (IntPtr)0x020C, data));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)0x020B, data));
            Assert.Equal(1, calls);
            host.UpdateSnapshot(MouseHotkeySnapshot.Empty);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)0x020C, data));
            Assert.Equal(1, calls);
            Assert.Equal(IntPtr.Zero, host.InvokeHookCallbackForTests(0, (IntPtr)0x020B, data));
            Assert.Equal(IntPtr.Zero, host.InvokeHookCallbackForTests(0, (IntPtr)0x020C, data));
            Assert.Equal(1, calls);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyboardHookCallback_DoesNotConsumeOsDeliveredShortcut(bool useOsDelivery)
    {
        int calls = 0;
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
        [
            new KeyboardHotkeyBindingSnapshot(
                1, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "Test", useOsDelivery)
        ]));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr hookData = AllocateKeyboardHookData(VirtualKeyF24, extraInfo: 0);

        try
        {
            nint result = host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, hookData);
            Assert.Equal(useOsDelivery ? 0 : 1, calls);
            if (!useOsDelivery) Assert.Equal((nint)1, result);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(hookData);
        }
    }

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
