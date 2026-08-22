using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AudioPilot.Tests.Platform;

public sealed class CoreAudioNativeInteropTests
{
    [Fact]
    public void PropertyVariant_ReservesFullNativeUnion()
    {
        int expectedSize = IntPtr.Size == 8 ? 24 : 16;
        Assert.Equal(expectedSize, Unsafe.SizeOf<NativePropVariant>());
        Assert.Equal(expectedSize, Marshal.SizeOf<NativePropVariant>());
        Assert.Equal(0, Marshal.OffsetOf<NativePropVariant>(nameof(NativePropVariant.vt)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativePropVariant>(nameof(NativePropVariant.pointerValue)).ToInt32());
    }

    [Fact]
    public void PropertyVariant_NativeClearPreservesAdjacentMemory()
    {
        const ulong sentinel = 0x123456789ABCDEF;
        var storage = new VariantStorage
        {
            Value = new NativePropVariant { vt = (ushort)VarEnum.VT_BOOL, shortValue = -1 },
            Sentinel = sentinel
        };

        storage.Value.Dispose();

        Assert.True(storage.Value.IsEmpty);
        Assert.Equal(sentinel, storage.Sentinel);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VariantStorage
    {
        public NativePropVariant Value;
        public ulong Sentinel;
    }
}
