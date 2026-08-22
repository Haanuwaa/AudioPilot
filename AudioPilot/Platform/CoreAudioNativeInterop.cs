using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NAudio.CoreAudioApi;

namespace AudioPilot.Platform
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativePropertyKey(Guid formatId, uint propertyId) : IEquatable<NativePropertyKey>
    {
        public Guid FormatId { get; } = formatId;
        public uint PropertyId { get; } = propertyId;

        public bool Equals(NativePropertyKey other) => FormatId == other.FormatId && PropertyId == other.PropertyId;
        public override bool Equals(object? obj) => obj is NativePropertyKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(FormatId, PropertyId);
        public static bool operator ==(NativePropertyKey left, NativePropertyKey right) => left.Equals(right);
        public static bool operator !=(NativePropertyKey left, NativePropertyKey right) => !left.Equals(right);
    }

    [StructLayout(LayoutKind.Explicit)]
    internal partial struct NativePropVariant : IDisposable
    {
        private const ushort VT_EMPTY = 0;
        private const ushort VT_NULL = 1;
        private const ushort VT_I2 = 2;
        private const ushort VT_I4 = 3;
        private const ushort VT_BSTR = 8;
        private const ushort VT_BOOL = 11;
        private const ushort VT_UI1 = 17;
        private const ushort VT_UI2 = 18;
        private const ushort VT_UI4 = 19;
        private const ushort VT_I8 = 20;
        private const ushort VT_UI8 = 21;
        private const ushort VT_LPWSTR = 31;

        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] public byte byteValue;
        [FieldOffset(8)] public short shortValue;
        [FieldOffset(8)] public ushort ushortValue;
        [FieldOffset(8)] public int intValue;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public long longValue;
        [FieldOffset(8)] public ulong ulongValue;

        public readonly bool IsEmpty => vt is VT_EMPTY or VT_NULL;

        public readonly bool TryGetBoolean(out bool value)
        {
            switch (vt)
            {
                case VT_BOOL:
                case VT_I2:
                    value = shortValue != 0;
                    return true;
                case VT_UI1:
                    value = byteValue != 0;
                    return true;
                case VT_UI2:
                    value = ushortValue != 0;
                    return true;
                case VT_I4:
                    value = intValue != 0;
                    return true;
                case VT_UI4:
                    value = ulVal != 0;
                    return true;
                case VT_I8:
                    value = longValue != 0;
                    return true;
                case VT_UI8:
                    value = ulongValue != 0;
                    return true;
                case VT_LPWSTR:
                    return bool.TryParse(Marshal.PtrToStringUni(pointerValue), out value);
                case VT_BSTR:
                    return bool.TryParse(Marshal.PtrToStringBSTR(pointerValue), out value);
                default:
                    value = false;
                    return false;
            }
        }

        public readonly bool TryGetString(out string? value)
        {
            switch (vt)
            {
                case VT_EMPTY:
                case VT_NULL:
                    value = null;
                    return true;
                case VT_LPWSTR:
                    value = Marshal.PtrToStringUni(pointerValue);
                    return true;
                case VT_BSTR:
                    value = Marshal.PtrToStringBSTR(pointerValue);
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        public void Dispose()
        {
            if (vt == VT_EMPTY)
            {
                return;
            }

            int clearResult = PropVariantClear(ref this);
            if (clearResult == 0)
            {
                vt = VT_EMPTY;
                pointerValue = IntPtr.Zero;
            }
        }

        [LibraryImport("ole32.dll")]
        private static partial int PropVariantClear(ref NativePropVariant variant);
    }

    [GeneratedComInterface]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IMMDeviceEnumeratorNativeInterop
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, DeviceState stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IntPtr endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [GeneratedComInterface]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IMMDeviceNativeInterop
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr interfacePointer);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out DeviceState state);
    }

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPropertyStoreNativeInterop
    {
        [PreserveSig] int GetCount(out int propertyCount);
        [PreserveSig] int GetAt(int propertyIndex, out NativePropertyKey key);
        [PreserveSig] int GetValue(ref NativePropertyKey key, out NativePropVariant value);
        [PreserveSig] int SetValue(ref NativePropertyKey key, ref NativePropVariant value);
        [PreserveSig] int Commit();
    }

    [GeneratedComInterface]
    [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IDeviceTopologyNativeInterop
    {
        [PreserveSig] int GetConnectorCount(out uint connectorCount);
        [PreserveSig] int GetConnector(uint index, out IntPtr connector);
    }

    [GeneratedComInterface]
    [Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IConnectorNativeInterop
    {
        [PreserveSig] int GetType(out int connectorType);
        [PreserveSig] int GetDataFlow(out int dataFlow);
        [PreserveSig] int ConnectTo(IConnectorNativeInterop connectedTo);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool isConnected);
        [PreserveSig] int GetConnectedTo(out IntPtr connectedTo);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string connectorId);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
    }
}
