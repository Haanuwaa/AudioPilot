using System.Buffers.Binary;
using System.Windows.Input;

namespace AudioPilot.Tests.Services.Hotkeys;

[Collection("WpfApplicationIsolation")]
public sealed class HotkeyHeldInputStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentReleaseMonitor_RegistersAndReleasesItsNativeResources(bool keyboard)
    {
        await Task.Run(() =>
        {
            using var monitor = new HotkeyRawInputReleaseMonitor(keyboard, (_, _) => { });
            monitor.SetEnabled(true);
            monitor.SetEnabled(false);
            monitor.SetEnabled(true);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void IndependentRelease_EndsTheHoldWithoutReactivatingAnEarlierPress()
    {
        Func<bool>? firstHold = null;
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None)
        {
            OnPressed = held => firstHold = held,
        };
        Assert.True(state.Press(100));
        state.CapturePressCallback()();
        Assert.True(firstHold!());
        state.ReleasePhysical(99);
        Assert.True(firstHold());
        Assert.False(state.Press(101));
        state.ReleasePhysical(102);
        Assert.False(firstHold());
        Assert.True(state.WasPhysicallyReleased);
        Assert.True(state.Press(103));
        Assert.True(state.IsHeld());
        Assert.False(firstHold());
        state.Release();
        Assert.False(state.IsHeld());
        Assert.False(state.WasPhysicallyReleased);
        state.ReleasePhysical(104);
        Assert.True(state.WasPhysicallyReleased);
    }

    [Fact]
    public void IndependentRelease_TimestampWrapDoesNotDiscardANewerRelease()
    {
        var state = new HotkeyHeldInputState(HotkeyModifierMask.None);
        state.Press(uint.MaxValue - 2);
        state.ReleasePhysical(1);
        Assert.False(state.IsHeld());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void RawKeyboardRelease_UsesNativeHeaderSizeAndIgnoresPresses(int headerSize)
    {
        var released = new List<(HotkeyMainInput, uint)>();
        byte[] packet = new byte[headerSize + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(headerSize + 6), 0x87);
        HotkeyRawInputReleaseMonitor.ProcessPacket(packet, headerSize, 100, false, (input, time) => released.Add((input, time)));
        Assert.Empty(released);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(headerSize + 2), 1);
        HotkeyRawInputReleaseMonitor.ProcessPacket(packet, headerSize, 101, false, (input, time) => released.Add((input, time)));
        Assert.Equal((HotkeyMainInput.FromKeyboard(Key.F24), 101u), Assert.Single(released));
        HotkeyRawInputReleaseMonitor.ProcessPacket(packet.AsSpan(0, headerSize + 7), headerSize, 102, false, (_, _) => Assert.Fail("Truncated input."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawMouseRelease_MapsAllButtonsIncludingSwappedLeftAndRight(bool swapped)
    {
        byte[] packet = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), 0x2AA);
        var released = new List<MouseButton>();
        HotkeyRawInputReleaseMonitor.ProcessPacket(packet, 24, 1, swapped, (input, _) => released.Add(input.MouseButton));
        Assert.Equal(swapped ? [MouseButton.Right, MouseButton.Left, MouseButton.Middle, MouseButton.XButton1, MouseButton.XButton2]
            : new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.XButton1, MouseButton.XButton2 }, released);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), 0x155);
        HotkeyRawInputReleaseMonitor.ProcessPacket(packet, 24, 1, swapped, (_, _) => Assert.Fail("Mouse down is not a release."));
    }
}
