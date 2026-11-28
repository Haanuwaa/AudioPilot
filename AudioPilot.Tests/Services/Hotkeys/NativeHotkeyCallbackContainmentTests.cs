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
    public void NativeWindowShortcut_PassesThroughPressRepeatAndReleaseAndIgnoresInjectedFallback()
    {
        var observations = new List<KeyboardHotkeyEvent>();
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, input) => observations.Add(input), getForegroundWindow: () => 123);
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
            [new(1, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "Window", UseOsDelivery: true)]));
        TestPrivateAccess.SetField(host, "_hookId", (nint)1);
        nint key = AllocateKeyboardHookData(VirtualKeyF24, 0);
        try
        {
            Marshal.WriteInt32(key, 12, 456);
            // An injected key-down without a release cannot reserve the physical press.
            Marshal.WriteInt32(key, 8, 0x10);
            Assert.Equal(nint.Zero, host.InvokeHookCallbackForTests(0, WmKeyDown, key));
            Assert.Empty(observations);

            Marshal.WriteInt32(key, 8, 0);
            Assert.Equal(nint.Zero, host.InvokeHookCallbackForTests(0, WmKeyDown, key));
            Assert.Equal(456u, Assert.Single(observations).MessageTime);
            Assert.Equal((nint)123, observations[0].FallbackForeground);

            // An injected key-up cannot rearm the fallback while the physical key is held.
            Marshal.WriteInt32(key, 8, 0x10);
            Assert.Equal(nint.Zero, host.InvokeHookCallbackForTests(0, 0x0101, key));
            Marshal.WriteInt32(key, 8, 0);
            Assert.Equal(nint.Zero, host.InvokeHookCallbackForTests(0, WmKeyDown, key));
            Assert.Single(observations);

            Assert.Equal(nint.Zero, host.InvokeHookCallbackForTests(0, 0x0101, key));
            Assert.Equal(2, observations.Count);
            Assert.Equal(456u, observations[1].ReleasedPressTime);
            Marshal.WriteInt32(key, 12, 567);
            Assert.Equal(nint.Zero, host.InvokeHookCallbackForTests(0, WmKeyDown, key));
            Assert.Equal(3, observations.Count);
            Assert.Equal(567u, observations[2].MessageTime);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", nint.Zero);
            Marshal.FreeHGlobal(key);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedPhysicalRelease_RearmsFirstSubsequentPress(bool useMouse)
    {
        var holds = new List<Func<bool>>();
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None) { OnPressed = holds.Add };
        using var keyboard = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (binding, _) => binding.Callback!());
        using var mouse = new LowLevelMouseHotkeyThreadHost(Logger.Instance, (binding, _) => binding.Callback!());
        keyboard.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
            [new(1, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "Hold", HoldState: state)]));
        mouse.UpdateSnapshot(MouseHotkeySnapshot.Create(
            [new(2, HotkeyMainInput.FromMouseButton(MouseButton.XButton1), HotkeyModifierMask.None, null, "Hold", state)]));
        TestPrivateAccess.SetField(keyboard, "_hookId", (nint)1);
        TestPrivateAccess.SetField(mouse, "_hookId", (nint)1);
        nint data = AllocateKeyboardHookData(VirtualKeyF24, 0);
        nint mouseData = Marshal.AllocHGlobal(32);
        for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(mouseData, offset, 0);
        Marshal.WriteInt32(mouseData, 8, 1 << 16);
        nint Press() => useMouse ? mouse.InvokeHookCallbackForTests(0, (nint)0x020B, mouseData)
            : keyboard.InvokeHookCallbackForTests(0, (nint)WmKeyDown, data);
        try
        {
            Assert.Equal((nint)1, Press());
            Assert.True(Assert.Single(holds)());
            state.ReleasePhysical(0);
            Assert.False(holds[0]());
            Assert.Equal((nint)1, Press());
            Assert.Equal(2, holds.Count);
            Assert.True(holds[1]());
            Assert.False(holds[0]());
        }
        finally
        {
            TestPrivateAccess.SetField(keyboard, "_hookId", nint.Zero);
            TestPrivateAccess.SetField(mouse, "_hookId", nint.Zero);
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(mouseData);
        }
    }

    [Fact]
    public void Capture_NativeHooksPassNewInputThroughButStillReleaseExistingHolds()
    {
        SharedStaDispatcherHost.Run(() =>
        {
            var capture = new HotkeyCaptureSession(_ => false);
            var held = new HotkeyHeldInputState(HotkeyModifierMask.None);
            int calls = 0;
            using var keyboard = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
            using var mouse = new LowLevelMouseHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
            keyboard.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
                [new(1, HotkeyMainInput.FromKeyboard(Key.F24), HotkeyModifierMask.None, null, "Hold", HoldState: held)], capture));
            mouse.UpdateSnapshot(MouseHotkeySnapshot.Create(
                [new(2, HotkeyMainInput.FromMouseButton(MouseButton.XButton1), HotkeyModifierMask.None, null, "Mouse")], capture));
            TestPrivateAccess.SetField(keyboard, "_hookId", (IntPtr)1);
            TestPrivateAccess.SetField(mouse, "_hookId", (IntPtr)1);
            IntPtr keyData = AllocateKeyboardHookData(VirtualKeyF24, 0);
            IntPtr mouseData = Marshal.AllocHGlobal(32);
            for (int offset = 0; offset < 32; offset += 4) Marshal.WriteInt32(mouseData, offset, 0);
            Marshal.WriteInt32(mouseData, 8, 1 << 16);
            try
            {
                Assert.Equal((IntPtr)1, keyboard.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, keyData));
                Assert.True(held.IsHeld());
                using (capture.Acquire())
                {
                    Assert.Equal((IntPtr)1, keyboard.InvokeHookCallbackForTests(0, (IntPtr)0x0101, keyData));
                    Assert.False(held.IsHeld());
                    Assert.Equal(IntPtr.Zero, keyboard.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, keyData));
                    Assert.Equal(IntPtr.Zero, mouse.InvokeHookCallbackForTests(0, (IntPtr)0x020B, mouseData));
                    Assert.Equal(IntPtr.Zero, mouse.InvokeHookCallbackForTests(0, (IntPtr)0x020C, mouseData));
                    Assert.Equal(1, calls);
                }
                Assert.Equal((IntPtr)1, keyboard.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, keyData));
                Assert.Equal((IntPtr)1, mouse.InvokeHookCallbackForTests(0, (IntPtr)0x020B, mouseData));
                Assert.Equal(3, calls);
            }
            finally
            {
                TestPrivateAccess.SetField(keyboard, "_hookId", IntPtr.Zero);
                TestPrivateAccess.SetField(mouse, "_hookId", IntPtr.Zero);
                Marshal.FreeHGlobal(keyData);
                Marshal.FreeHGlobal(mouseData);
            }
        });
    }

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
        var observations = new List<KeyboardHotkeyEvent>();
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, input) => observations.Add(input), getForegroundWindow: () => 123);
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
            Assert.Equal(useOsDelivery ? nint.Zero : (nint)1, result);
            Assert.Equal(useOsDelivery ? (nint)123 : nint.Zero, Assert.Single(observations).FallbackForeground);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyboardRepeatPolicy_RequiresReleaseForDiscreteActions(bool allowRepeat)
    {
        int calls = 0;
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
        var input = HotkeyMainInput.FromKeyboard(Key.F24);
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(Enumerable.Range(0, 16).Select(mask =>
            new KeyboardHotkeyBindingSnapshot(1, input, (HotkeyModifierMask)mask, null, "Action", AllowRepeat: allowRepeat))));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr data = AllocateKeyboardHookData(VirtualKeyF24, 0);
        try
        {
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data));
            Assert.Equal(allowRepeat ? 2 : 1, calls);
            host.UpdateSnapshot(KeyboardHotkeySnapshot.Empty);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)0x0101, data));
            Assert.True(host.RecoverReleasedHeldInputs(_ => true));
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData(MouseButton.Left, 0x0201, 0x0202)]
    [InlineData(MouseButton.Right, 0x0204, 0x0205)]
    [InlineData(MouseButton.Middle, 0x0207, 0x0208)]
    public void StandardMouseButtonHook_ConsumesMatchedPressAndItsRelease_AndRecoversMissedRelease(MouseButton button, int down, int up)
    {
        int calls = 0;
        using var host = new LowLevelMouseHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
        host.UpdateSnapshot(MouseHotkeySnapshot.Create(
            Enumerable.Range(0, 16).Select(mask =>
                new MouseHotkeyBindingSnapshot(1, HotkeyMainInput.FromMouseButton(button), (HotkeyModifierMask)mask, null, "Mouse button"))));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        try
        {
            Assert.Equal(IntPtr.Zero, host.InvokeHookCallbackForTests(0, (IntPtr)up, IntPtr.Zero));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)down, IntPtr.Zero));
            Assert.Equal(1, calls);
            host.UpdateSnapshot(MouseHotkeySnapshot.Empty);
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)up, IntPtr.Zero));
            Assert.Equal(IntPtr.Zero, host.InvokeHookCallbackForTests(0, (IntPtr)up, IntPtr.Zero));

            // Missed release during desktop switch is cleared once the button is physically up.
            host.UpdateSnapshot(MouseHotkeySnapshot.Create(
                Enumerable.Range(0, 16).Select(mask =>
                    new MouseHotkeyBindingSnapshot(1, HotkeyMainInput.FromMouseButton(button), (HotkeyModifierMask)mask, null, "Mouse button"))));
            Assert.Equal((IntPtr)1, host.InvokeHookCallbackForTests(0, (IntPtr)down, IntPtr.Zero));
            Assert.False(host.RecoverReleasedHeldInputs(_ => true));
            Assert.True(host.RecoverReleasedHeldInputs(_ => false));
            Assert.Equal(IntPtr.Zero, host.InvokeHookCallbackForTests(0, (IntPtr)up, IntPtr.Zero));
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
        }
    }

    [Fact]
    public void DiscreteKeyboardPress_SurvivesBindingChangesAndRecoversMissedRelease()
    {
        int calls = 0;
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
        var input = HotkeyMainInput.FromKeyboard(Key.F24);
        var bindings = Enumerable.Range(0, 16).Select(mask =>
            new KeyboardHotkeyBindingSnapshot(1, input, (HotkeyModifierMask)mask, null, "Action"));
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(bindings));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr data = AllocateKeyboardHookData(VirtualKeyF24, 0);
        try
        {
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data);
            host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(bindings.Select(binding => binding with { Id = 2 })));
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data);
            Assert.Equal(1, calls);
            host.ReleaseHeldInputsForDesktopSwitch();
            Assert.False(host.RecoverReleasedHeldInputs(_ => true));
            Assert.True(host.RecoverReleasedHeldInputs(_ => false));
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data);
            Assert.Equal(2, calls);
            host.InvokeHookCallbackForTests(0, (IntPtr)0x0101, data);
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data);
            Assert.Equal(3, calls);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Theory]
    [InlineData(Key.Pause)]
    [InlineData(Key.PrintScreen)]
    public void PulseKey_DoesNotRequireAKeyUpBeforeTheNextPress(Key key)
    {
        int calls = 0;
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, _) => calls++);
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(Enumerable.Range(0, 16).Select(mask =>
            new KeyboardHotkeyBindingSnapshot(1, HotkeyMainInput.FromKeyboard(key), (HotkeyModifierMask)mask, null, "Action"))));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr data = AllocateKeyboardHookData(KeyInterop.VirtualKeyFromKey(key), 0);
        try
        {
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data);
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, data);
            Assert.Equal(2, calls);
            Assert.True(host.RecoverReleasedHeldInputs(_ => true));
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void KeyboardHotkeySnapshot_TryMatchLazy_DoesNotReadModifiersForUnboundKey()
    {
        int modifierReads = 0;
        KeyboardHotkeySnapshot snapshot = KeyboardHotkeySnapshot.Create(
        [
            new KeyboardHotkeyBindingSnapshot(1, HotkeyMainInput.FromKeyboard(Key.F10), HotkeyModifierMask.Ctrl, null, "Action"),
        ]);

        bool matchedUnbound = snapshot.TryMatch(HotkeyMainInput.FromKeyboard(Key.A), () =>
        {
            modifierReads++;
            return HotkeyModifierMask.Ctrl;
        }, out _);

        bool matchedBound = snapshot.TryMatch(HotkeyMainInput.FromKeyboard(Key.F10), () =>
        {
            modifierReads++;
            return HotkeyModifierMask.Ctrl;
        }, out KeyboardHotkeyBindingSnapshot binding);

        Assert.False(matchedUnbound);
        Assert.True(matchedBound);
        Assert.Equal(1, binding.Id);
        Assert.Equal(1, modifierReads);
    }

    [Fact]
    public void RawInputRelease_ClearsPressedAndForwardedInputsWhenKeyUpIsDropped()
    {
        int pressDispatches = 0;
        uint? releasedPressTime = null;
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, evt) =>
        {
            if (evt.ReleasedPressTime.HasValue)
            {
                releasedPressTime = evt.ReleasedPressTime;
            }
            else
            {
                pressDispatches++;
            }
        });

        HotkeyMainInput pressedKey = HotkeyMainInput.FromKeyboard(Key.MediaPlayPause);
        HotkeyMainInput forwardedKey = HotkeyMainInput.FromKeyboard(Key.F9);
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
            Enumerable.Range(0, 16).SelectMany(mask => new[]
            {
                new KeyboardHotkeyBindingSnapshot(1, pressedKey, (HotkeyModifierMask)mask, null, "Media"),
                new KeyboardHotkeyBindingSnapshot(2, forwardedKey, (HotkeyModifierMask)mask, null, "Window", UseOsDelivery: true),
            })));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr pressedData = AllocateKeyboardHookData(KeyInterop.VirtualKeyFromKey(Key.MediaPlayPause), 0);
        IntPtr forwardedData = AllocateKeyboardHookData(KeyInterop.VirtualKeyFromKey(Key.F9), 0);
        try
        {
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, pressedData);
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, forwardedData);
            Assert.True(host.HasTrackedReleaseInputs);
            Assert.Equal(2, pressDispatches);

            // Second keydown before release is suppressed for pressed key.
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, pressedData);
            Assert.Equal(2, pressDispatches);

            // Raw input release recovers both without needing WM_KEYUP.
            host.HandleRawInputRelease(pressedKey, 100);
            host.HandleRawInputRelease(forwardedKey, 101);
            Assert.False(host.HasTrackedReleaseInputs);
            Assert.NotNull(releasedPressTime);

            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, pressedData);
            Assert.Equal(3, pressDispatches);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(pressedData);
            Marshal.FreeHGlobal(forwardedData);
        }
    }

    [Fact]
    public void RawInputRelease_IgnoresStaleReleaseTimestampsForPressedAndForwardedInputs()
    {
        int pressDispatches = 0;
        int forwardedReleases = 0;
        using var host = new LowLevelKeyboardHotkeyThreadHost(Logger.Instance, (_, evt) =>
        {
            if (evt.ReleasedPressTime.HasValue) forwardedReleases++;
            else pressDispatches++;
        });

        HotkeyMainInput pressedKey = HotkeyMainInput.FromKeyboard(Key.MediaPlayPause);
        HotkeyMainInput forwardedKey = HotkeyMainInput.FromKeyboard(Key.F9);
        host.UpdateSnapshot(KeyboardHotkeySnapshot.Create(
            Enumerable.Range(0, 16).SelectMany(mask => new[]
            {
                new KeyboardHotkeyBindingSnapshot(1, pressedKey, (HotkeyModifierMask)mask, null, "Media"),
                new KeyboardHotkeyBindingSnapshot(2, forwardedKey, (HotkeyModifierMask)mask, null, "Window", UseOsDelivery: true),
            })));
        TestPrivateAccess.SetField(host, "_hookId", (IntPtr)1);
        IntPtr pressedData = AllocateKeyboardHookData(KeyInterop.VirtualKeyFromKey(Key.MediaPlayPause), 0);
        IntPtr forwardedData = AllocateKeyboardHookData(KeyInterop.VirtualKeyFromKey(Key.F9), 0);
        Marshal.WriteInt32(pressedData, 12, 200);
        Marshal.WriteInt32(forwardedData, 12, 200);
        try
        {
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, pressedData);
            host.InvokeHookCallbackForTests(0, (IntPtr)WmKeyDown, forwardedData);
            Assert.Equal(2, pressDispatches);

            // Stale raw-input releases older than press timestamp 200 must be ignored.
            host.HandleRawInputRelease(pressedKey, 150);
            host.HandleRawInputRelease(forwardedKey, 150);
            Assert.True(host.HasTrackedReleaseInputs);
            Assert.Equal(0, forwardedReleases);

            // Current or newer raw-input releases clear both entries.
            host.HandleRawInputRelease(pressedKey, 200);
            host.HandleRawInputRelease(forwardedKey, 205);
            Assert.False(host.HasTrackedReleaseInputs);
            Assert.Equal(1, forwardedReleases);
        }
        finally
        {
            TestPrivateAccess.SetField(host, "_hookId", IntPtr.Zero);
            Marshal.FreeHGlobal(pressedData);
            Marshal.FreeHGlobal(forwardedData);
        }
    }

    [Theory]
    [InlineData(MouseButton.Left, false, 1)]
    [InlineData(MouseButton.Right, false, 2)]
    [InlineData(MouseButton.Left, true, 2)]
    [InlineData(MouseButton.Right, true, 1)]
    [InlineData(MouseButton.Middle, true, 4)]
    [InlineData(MouseButton.XButton1, true, 5)]
    [InlineData(MouseButton.XButton2, true, 6)]
    public void MouseHookRecovery_RespectsSwappedPrimaryButtons(MouseButton button, bool buttonsSwapped, int expectedVk)
    {
        Assert.Equal(expectedVk, LowLevelMouseHotkeyThreadHost.ResolvePhysicalMouseVirtualKey(button, buttonsSwapped));
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
