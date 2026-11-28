using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPilot.Services.UI.Interop;

namespace AudioPilot.Tests.Platform;

public sealed unsafe class NativeComOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_ReleasesOwnedReferences_WithoutInvalidatingAnotherOwner(bool audio)
    {
        using var native = new NativeUnknown();
        using IDisposable first = Wrap<IColorEmojiWicBitmap>(native.Pointer, audio);
        Marshal.AddRef(native.Pointer);
        using IDisposable second = Wrap<IColorEmojiWicBitmap>(native.Pointer, audio);

        first.Dispose();
        int remaining = native.References;
        Assert.True(remaining > 0);
        first.Dispose();
        Assert.Equal(remaining, native.References);
        second.Dispose();

        Assert.Equal(0, native.References);
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedInterface_ReleasesActivationAndWrapperReferences(bool audio)
    {
        using var native = new NativeUnknown();
        if (audio)
        {
            Assert.False(NativeAudioInteropHelper.ComActivator.TryWrapTyped<IColorEmojiWicImagingFactory>(
                native.Pointer, out var handle, out int hresult));
            Assert.Null(handle);
            Assert.Equal(unchecked((int)0x80004002), hresult);
        }
        else
        {
            Assert.Throws<COMException>(() => ColorEmojiComRuntime.Wrap<IColorEmojiWicImagingFactory>(native.Pointer));
        }

        Assert.Equal(0, native.References);
    }

    private static IDisposable Wrap<TInterface>(nint pointer, bool audio) where TInterface : class
    {
        if (!audio)
        {
            return ColorEmojiComRuntime.Wrap<TInterface>(pointer);
        }

        Assert.True(NativeAudioInteropHelper.ComActivator.TryWrapTyped<TInterface>(pointer, out var handle, out _));
        return handle;
    }

    /// <summary>A native IUnknown with observable ownership; it deliberately implements no bitmap methods.</summary>
    private sealed class NativeUnknown : IDisposable
    {
        private readonly NativeState* _state;
        private readonly void** _vtable;
        private static readonly Guid _unknownId = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid _bitmapId = typeof(IColorEmojiWicBitmap).GUID;

        public NativeUnknown()
        {
            _vtable = (void**)NativeMemory.Alloc((nuint)(3 * sizeof(void*)));
            _vtable[0] = (delegate* unmanaged[Stdcall]<NativeState*, Guid*, nint*, int>)&QueryInterface;
            _vtable[1] = (delegate* unmanaged[Stdcall]<NativeState*, uint>)&AddRef;
            _vtable[2] = (delegate* unmanaged[Stdcall]<NativeState*, uint>)&Release;
            _state = (NativeState*)NativeMemory.AllocZeroed((nuint)sizeof(NativeState));
            _state->Vtable = _vtable;
            _state->References = 1;
        }

        public nint Pointer => (nint)_state;
        public int References => Volatile.Read(ref _state->References);

        public void Dispose()
        {
            // A failed ownership assertion must not free memory still reachable by a COM finalizer.
            if (References == 0)
            {
                NativeMemory.Free(_state);
                NativeMemory.Free(_vtable);
            }
        }

        private struct NativeState
        {
            public void** Vtable;
            public int References;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(NativeState* self, Guid* id, nint* result)
        {
            *result = 0;
            if (*id != _unknownId && *id != _bitmapId)
            {
                return unchecked((int)0x80004002);
            }

            Interlocked.Increment(ref self->References);
            *result = (nint)self;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(NativeState* self) => (uint)Interlocked.Increment(ref self->References);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(NativeState* self) => (uint)Interlocked.Decrement(ref self->References);
    }
}
