using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Services.UI.Interop
{
    internal static partial class ColorEmojiComRuntime
    {
        private const uint ClsCtxInProcServer = 1;
        private static readonly StrategyBasedComWrappers _comWrappers = new();

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

            object? managed = null;
            try
            {
                managed = _comWrappers.GetOrCreateObjectForComInstance(
                    interfacePointer, CreateObjectFlags.UniqueInstance);
                if (managed is not TInterface typedInterface)
                {
                    throw new COMException("Failed to marshal native COM interface.", unchecked((int)0x80004002));
                }

                return new ColorEmojiComHandle<TInterface>(typedInterface, interfacePointer);
            }
            catch
            {
                try
                {
                    (managed as ComObject)?.FinalRelease();
                }
                finally
                {
                    Marshal.Release(interfacePointer);
                }

                throw;
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

    /// <summary>Owns both the activation reference and the references held by its unique generated COM wrapper.</summary>
    internal sealed class ColorEmojiComHandle<TInterface>(TInterface managed, IntPtr pointer) : IDisposable
        where TInterface : class
    {
        private IntPtr _pointer = pointer;

        public TInterface Interface { get; } = managed;
        public IntPtr Pointer => _pointer;

        public void Dispose()
        {
            IntPtr ownedPointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (ownedPointer == IntPtr.Zero)
            {
                return;
            }

            try
            {
                (Interface as ComObject)?.FinalRelease();
            }
            finally
            {
                Marshal.Release(ownedPointer);
            }
        }
    }
}
