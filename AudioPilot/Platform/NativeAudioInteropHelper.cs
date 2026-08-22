using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPilot.Platform
{
    internal interface INativeComActivator
    {
        bool TryCreateTyped<TInterface>(Guid classId, Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
            where TInterface : class;

        bool TryWrapTyped<TInterface>(IntPtr interfacePointer, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
            where TInterface : class;
    }

    internal interface INativeAudioEndpointFactory
    {
        bool IsAvailable { get; }
        bool TryCreate(IAudioEndpointInfo endpointInfo, [NotNullWhen(true)] out INativeAudioEndpoint? endpoint);
    }

    internal interface INativeAudioEndpoint : IDisposable
    {
        bool TryOpenPropertyStore(uint stgmAccess, [NotNullWhen(true)] out INativePropertyStore? propertyStore, out int hresult);
        bool TryActivate<TInterface>(Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
            where TInterface : class;
    }

    internal interface INativePropertyStore : IDisposable
    {
        int GetValue(ref NativePropertyKey key, out NativePropVariant value);
        int SetValue(ref NativePropertyKey key, ref NativePropVariant value);
        int Commit();
    }

    internal interface IActivatedNativeComObject<out TInterface> : IDisposable
        where TInterface : class
    {
        TInterface Interface { get; }
    }

    internal static partial class NativeAudioInteropHelper
    {
        private static readonly Guid MMDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private const uint ClsCtxInProcServer = 1;
        private static readonly INativeComActivator SharedActivator = new Ole32NativeComActivator();
        private static readonly INativeAudioEndpointFactory SharedFactory = new NativeAudioEndpointFactory(SharedActivator);

        internal static INativeComActivator ComActivator => SharedActivator;
        internal static INativeAudioEndpointFactory EndpointFactory => SharedFactory;

        private static bool TryActivateRaw(Guid classId, Guid interfaceId, uint clsCtx, out IntPtr interfacePointer, out int hresult)
        {
            interfacePointer = IntPtr.Zero;
            hresult = CoCreateInstance(ref classId, IntPtr.Zero, clsCtx, ref interfaceId, out IntPtr pointer);
            if (hresult < 0 || pointer == IntPtr.Zero)
            {
                return false;
            }

            interfacePointer = pointer;
            return true;
        }

        private sealed class Ole32NativeComActivator : INativeComActivator
        {
            public bool TryCreateTyped<TInterface>(Guid classId, Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
                where TInterface : class
            {
                activatedObject = null;
                if (!TryActivateRaw(classId, interfaceId, clsCtx, out IntPtr interfacePointer, out hresult))
                {
                    return false;
                }

                return TryWrapTyped(interfacePointer, out activatedObject, out hresult);
            }

            public bool TryWrapTyped<TInterface>(IntPtr interfacePointer, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
                where TInterface : class
            {
                activatedObject = null;
                hresult = 0;

                if (interfacePointer == IntPtr.Zero)
                {
                    hresult = unchecked((int)0x80004003);
                    return false;
                }

                try
                {
                    TInterface? typedInterface;
                    unsafe
                    {
                        typedInterface = ComInterfaceMarshaller<TInterface>.ConvertToManaged((void*)interfacePointer);
                    }

                    if (typedInterface == null)
                    {
                        Marshal.Release(interfacePointer);
                        hresult = unchecked((int)0x80004002);
                        return false;
                    }

                    activatedObject = new NativeComObjectHandle<TInterface>(typedInterface, interfacePointer);
                    return true;
                }
                catch
                {
                    Marshal.Release(interfacePointer);
                    throw;
                }
            }
        }

        private sealed class NativeAudioEndpointFactory(INativeComActivator activator) : INativeAudioEndpointFactory
        {
            private readonly INativeComActivator _activator = activator;

            public bool IsAvailable => true;

            public bool TryCreate(IAudioEndpointInfo endpointInfo, [NotNullWhen(true)] out INativeAudioEndpoint? endpoint)
            {
                endpoint = null;
                if (endpointInfo == null || string.IsNullOrWhiteSpace(endpointInfo.Id))
                {
                    return false;
                }

                if (!_activator.TryCreateTyped(MMDeviceEnumeratorClassId, typeof(IMMDeviceEnumeratorNativeInterop).GUID, ClsCtxInProcServer, out IActivatedNativeComObject<IMMDeviceEnumeratorNativeInterop>? enumeratorObject, out _))
                {
                    return false;
                }

                try
                {
                    int getDeviceHr = enumeratorObject.Interface.GetDevice(endpointInfo.Id, out IntPtr endpointPointer);
                    if (getDeviceHr < 0 || endpointPointer == IntPtr.Zero)
                    {
                        enumeratorObject.Dispose();
                        return false;
                    }

                    if (!_activator.TryWrapTyped(endpointPointer, out IActivatedNativeComObject<IMMDeviceNativeInterop>? endpointObject, out _))
                    {
                        enumeratorObject.Dispose();
                        return false;
                    }

                    endpoint = new NativeAudioEndpoint(endpointObject, enumeratorObject, _activator);
                    return true;
                }
                catch
                {
                    enumeratorObject.Dispose();
                    throw;
                }
            }
        }

        private sealed class NativeAudioEndpoint(
            IActivatedNativeComObject<IMMDeviceNativeInterop> endpointObject,
            IActivatedNativeComObject<IMMDeviceEnumeratorNativeInterop> enumeratorObject,
            INativeComActivator activator) : INativeAudioEndpoint
        {
            private IActivatedNativeComObject<IMMDeviceNativeInterop>? _endpointObject = endpointObject;
            private IActivatedNativeComObject<IMMDeviceEnumeratorNativeInterop>? _enumeratorObject = enumeratorObject;
            private readonly INativeComActivator _activator = activator;

            public void Dispose()
            {
                _endpointObject?.Dispose();
                _endpointObject = null;

                _enumeratorObject?.Dispose();
                _enumeratorObject = null;
            }

            public bool TryOpenPropertyStore(uint stgmAccess, [NotNullWhen(true)] out INativePropertyStore? propertyStore, out int hresult)
            {
                propertyStore = null;
                ObjectDisposedException.ThrowIf(_endpointObject == null, this);
                hresult = _endpointObject!.Interface.OpenPropertyStore(stgmAccess, out IntPtr propertyStorePointer);
                if (hresult < 0 || propertyStorePointer == IntPtr.Zero)
                {
                    return false;
                }

                if (!_activator.TryWrapTyped(propertyStorePointer, out IActivatedNativeComObject<IPropertyStoreNativeInterop>? propertyStoreObject, out hresult))
                {
                    return false;
                }

                propertyStore = new NativePropertyStoreHandle(propertyStoreObject);
                return true;
            }

            public bool TryActivate<TInterface>(Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
                where TInterface : class
            {
                activatedObject = null;
                ObjectDisposedException.ThrowIf(_endpointObject == null, this);
                hresult = _endpointObject!.Interface.Activate(ref interfaceId, clsCtx, IntPtr.Zero, out IntPtr interfacePointer);
                if (hresult < 0 || interfacePointer == IntPtr.Zero)
                {
                    return false;
                }

                return _activator.TryWrapTyped(interfacePointer, out activatedObject, out hresult);
            }
        }

        private sealed class NativePropertyStoreHandle(IActivatedNativeComObject<IPropertyStoreNativeInterop> propertyStoreObject) : INativePropertyStore
        {
            private IActivatedNativeComObject<IPropertyStoreNativeInterop>? _propertyStoreObject = propertyStoreObject;

            public int GetValue(ref NativePropertyKey key, out NativePropVariant value)
            {
                ObjectDisposedException.ThrowIf(_propertyStoreObject == null, this);
                return _propertyStoreObject!.Interface.GetValue(ref key, out value);
            }

            public int SetValue(ref NativePropertyKey key, ref NativePropVariant value)
            {
                ObjectDisposedException.ThrowIf(_propertyStoreObject == null, this);
                return _propertyStoreObject!.Interface.SetValue(ref key, ref value);
            }

            public int Commit()
            {
                ObjectDisposedException.ThrowIf(_propertyStoreObject == null, this);
                return _propertyStoreObject!.Interface.Commit();
            }

            public void Dispose()
            {
                _propertyStoreObject?.Dispose();
                _propertyStoreObject = null;
            }
        }

        private sealed class NativeComObjectHandle<TInterface>(TInterface typedInterface, IntPtr interfacePointer) : IActivatedNativeComObject<TInterface>
            where TInterface : class
        {
            private IntPtr _interfacePointer = interfacePointer;

            public TInterface Interface { get; } = typedInterface;

            public void Dispose()
            {
                if (_interfacePointer == IntPtr.Zero)
                {
                    return;
                }

                Marshal.Release(_interfacePointer);
                _interfacePointer = IntPtr.Zero;
            }
        }

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(ref Guid clsid, IntPtr inner, uint context, ref Guid uuid, out IntPtr obj);
    }
}
