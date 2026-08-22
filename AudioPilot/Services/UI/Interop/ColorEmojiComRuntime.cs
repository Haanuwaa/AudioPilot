using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Services.UI.Interop
{
    internal static partial class ColorEmojiComRuntime
    {
        private const uint ClsCtxInProcServer = 1;

        public static ColorEmojiComHandle<TInterface> CoCreate<TInterface>(Guid classId)
            where TInterface : class
        {
            Guid interfaceId = typeof(TInterface).GUID;
            int hresult = CoCreateInstance(ref classId, IntPtr.Zero, ClsCtxInProcServer, ref interfaceId, out IntPtr interfacePointer);
            if (hresult < 0)
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            return Wrap<TInterface>(interfacePointer);
        }

        public static ColorEmojiComHandle<TInterface> Wrap<TInterface>(IntPtr interfacePointer)
            where TInterface : class
        {
            if (interfacePointer == IntPtr.Zero)
            {
                throw new COMException("Native COM activation returned a null interface pointer.", unchecked((int)0x80004003));
            }

            unsafe
            {
                TInterface? managed = ComInterfaceMarshaller<TInterface>.ConvertToManaged((void*)interfacePointer);
                if (managed is null)
                {
                    Marshal.Release(interfacePointer);
                    throw new COMException("Failed to marshal native COM interface.", unchecked((int)0x80004002));
                }

                return new ColorEmojiComHandle<TInterface>(managed, interfacePointer);
            }
        }

        public static void ReleasePointer(IntPtr pointer)
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }

        [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
        private static partial int CoCreateInstance(ref Guid clsid, IntPtr inner, uint context, ref Guid uuid, out IntPtr obj);
    }

    internal sealed class ColorEmojiComHandle<TInterface>(TInterface managed, IntPtr pointer) : IDisposable
        where TInterface : class
    {
        private IntPtr _pointer = pointer;

        public TInterface Interface { get; } = managed;
        public IntPtr Pointer => _pointer;

        public void Dispose()
        {
            if (_pointer == IntPtr.Zero)
            {
                return;
            }

            Marshal.Release(_pointer);
            _pointer = IntPtr.Zero;
        }
    }
}
