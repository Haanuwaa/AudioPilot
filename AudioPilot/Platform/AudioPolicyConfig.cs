using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Platform
{
    [GeneratedComInterface]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool @default, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool @default, out long period, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out DeviceShareMode shareMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceShareMode shareMode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref NativePropertyKey key, out NativePropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref NativePropertyKey key, ref NativePropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    public enum DeviceShareMode
    {
        Shared = 0,
        Exclusive = 1
    }

    internal interface IPolicyConfigHandle : IDisposable
    {
        int SetDefaultEndpoint(string deviceId, Role role);
    }

    internal interface IPolicyConfigActivator
    {
        bool TryCreate(Guid clsid, [NotNullWhen(true)] out IPolicyConfigHandle? handle, out int hresult);
    }

    internal interface IAudioRoutingPolicyHandle : IDisposable
    {
        int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, string deviceId);
    }

    internal interface IAudioRoutingPolicyActivator
    {
        bool TryCreate([NotNullWhen(true)] out IAudioRoutingPolicyHandle? handle, out int hresult);
    }

    internal sealed class AudioPolicyConfigClient(IPolicyConfigActivator activator, Logger logger, Action<string>? ensureComInitialized = null)
    {
        internal static readonly Guid Win10Clsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        internal static readonly Guid Win11Clsid = new("294e1068-c349-43c2-aa97-3774628a4274");
        internal static readonly Guid PolicyConfigInterfaceId = new("f8679f50-850a-41cf-9c72-430f290290c8");

        private readonly IPolicyConfigActivator _activator = activator;
        private readonly Logger _logger = logger;
        private readonly Action<string> _ensureComInitialized = ensureComInitialized ?? ComThreadingHelper.ThrowIfComInitializationFailed;
        private readonly Lock _lock = new();
        private Guid? _activeClsid;

        public void SetDefaultDevice(string deviceId, Role role)
        {
            using IPolicyConfigHandle policy = CreatePolicyConfig();

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.Trace("AudioPolicyConfig", () => $"SetDefaultDevice {LogPrivacy.Id(deviceId)} role={role}");

            int hr = policy.SetDefaultEndpoint(deviceId, role);
            if (hr < 0)
            {
                InvalidateCachedClsid();
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        /// <summary>
        /// Detects the registered PolicyConfig CLSID supported by the current Windows version.
        /// </summary>
        /// <remarks>
        /// The policy-config COM server has shipped under different CLSIDs across Windows releases. Detection probes
        /// the supported known CLSIDs in order and returns the first activatable one so later audio policy calls can
        /// cache a working binding.
        /// </remarks>
        internal Guid DetectClsid()
        {
            _ensureComInitialized(nameof(DetectClsid));

            if (TryCreate(Win10Clsid)) return Win10Clsid;
            if (TryCreate(Win11Clsid)) return Win11Clsid;
            throw new COMException("IPolicyConfig not registered", unchecked((int)0x80040154));
        }

        internal void InvalidateCachedClsid()
        {
            lock (_lock)
            {
                _activeClsid = null;
            }
        }

        private IPolicyConfigHandle CreatePolicyConfig()
        {
            Guid clsid;
            lock (_lock)
            {
                if (_activeClsid == null)
                {
                    _activeClsid = DetectClsid();
                }

                clsid = _activeClsid.Value;
            }

            try
            {
                return CreateInstance(clsid);
            }
            catch
            {
                lock (_lock)
                {
                    if (_activeClsid == clsid)
                    {
                        _activeClsid = null;
                    }
                }

                throw;
            }
        }

        private bool TryCreate(Guid clsid)
        {
            if (_activator.TryCreate(clsid, out IPolicyConfigHandle? handle, out _))
            {
                handle.Dispose();
                return true;
            }

            return false;
        }

        private IPolicyConfigHandle CreateInstance(Guid clsid)
        {
            if (!_activator.TryCreate(clsid, out IPolicyConfigHandle? handle, out int creationResult))
            {
                Marshal.ThrowExceptionForHR(creationResult);
            }

            return handle!;
        }
    }

    internal enum ProcessAudioRoutingResult
    {
        Failed = 0,
        Applied,
        DeferredNoAudio,
    }

    internal sealed class AudioRoutingPolicyClient(IAudioRoutingPolicyActivator activator, Logger logger, Action<string>? ensureComInitialized = null)
    {
        internal const int MinimumSupportedBuild = 16299;
        internal const int ProcessNoAudioHResult = unchecked((int)0x80070057);
        private const string RenderDeviceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        private const string CaptureDeviceSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        private const string MmDevApiPrefix = @"\\?\SWD#MMDEVAPI#";

        private readonly IAudioRoutingPolicyActivator _activator = activator;
        private readonly Logger _logger = logger;
        private readonly Action<string> _ensureComInitialized = ensureComInitialized ?? ComThreadingHelper.ThrowIfComInitializationFailed;

        public ProcessAudioRoutingResult TrySetProcessDefaultDevice(uint processId, DataFlow flow, IReadOnlyList<Role> roles, string deviceId)
        {
            return TryUpdateProcessDefaultDevice(processId, flow, roles, deviceId, clearAssignment: false);
        }

        public ProcessAudioRoutingResult TryClearProcessDefaultDevice(uint processId, DataFlow flow, IReadOnlyList<Role> roles)
        {
            return TryUpdateProcessDefaultDevice(processId, flow, roles, string.Empty, clearAssignment: true);
        }

        private ProcessAudioRoutingResult TryUpdateProcessDefaultDevice(
            uint processId,
            DataFlow flow,
            IReadOnlyList<Role> roles,
            string deviceId,
            bool clearAssignment)
        {
            if (processId == 0 || roles.Count == 0 || !IsSupportedOnCurrentOs() || (!clearAssignment && string.IsNullOrWhiteSpace(deviceId)))
            {
                return ProcessAudioRoutingResult.Failed;
            }

            try
            {
                _ensureComInitialized(clearAssignment ? nameof(TryClearProcessDefaultDevice) : nameof(TrySetProcessDefaultDevice));

                using IAudioRoutingPolicyHandle policy = CreateRoutingPolicy();
                string persistedDeviceId = clearAssignment ? string.Empty : PackPersistedDeviceId(deviceId, flow);
                bool deferredNoAudio = false;
                foreach (Role role in roles.Distinct())
                {
                    int hr = policy.SetPersistedDefaultAudioEndpoint(processId, flow, role, persistedDeviceId);
                    if (hr == ProcessNoAudioHResult)
                    {
                        deferredNoAudio = true;
                        continue;
                    }

                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                }

                return deferredNoAudio
                    ? ProcessAudioRoutingResult.DeferredNoAudio
                    : ProcessAudioRoutingResult.Applied;
            }
            catch (Exception ex)
            {
                string operationName = clearAssignment ? nameof(TryClearProcessDefaultDevice) : nameof(TrySetProcessDefaultDevice);
                string action = clearAssignment ? "ClearProcessDefaultDevice" : "SetProcessDefaultDevice";
                _logger.Warning("AudioPolicyConfig", () => $"{action} failed pid={LogPrivacy.Id(processId.ToString(System.Globalization.CultureInfo.InvariantCulture))} flow={flow}", operationName, ex);
                return ProcessAudioRoutingResult.Failed;
            }
        }

        internal static bool IsSupportedOnCurrentOs()
        {
            Version version = Environment.OSVersion.Version;
            return version.Major >= 10 && version.Build > MinimumSupportedBuild;
        }

        internal static string PackPersistedDeviceId(string deviceId, DataFlow flow)
        {
            string suffix = flow == DataFlow.Capture ? CaptureDeviceSuffix : RenderDeviceSuffix;
            return $"{MmDevApiPrefix}{deviceId}{suffix}";
        }

        private IAudioRoutingPolicyHandle CreateRoutingPolicy()
        {
            if (!_activator.TryCreate(out IAudioRoutingPolicyHandle? handle, out int hresult))
            {
                Marshal.ThrowExceptionForHR(hresult);
            }

            return handle!;
        }
    }

    public static partial class AudioPolicyConfig
    {
        private const uint ClsCtxInProcServer = 1;
        private static readonly AudioPolicyConfigClient SharedClient = new(new NativePolicyConfigActivator(), Logger.Instance);
        private static readonly AudioRoutingPolicyClient SharedRoutingClient = new(new NativeAudioRoutingPolicyActivator(), Logger.Instance);

        internal static void SetDefaultDeviceOnCurrentThread(string deviceId, Role role)
        {
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(SetDefaultDeviceOnCurrentThread));
            SharedClient.SetDefaultDevice(deviceId, role);
        }

        public static void SetDefaultDevice(string deviceId, Role role)
        {
            ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                SharedClient.SetDefaultDevice(deviceId, role);
            });
        }

        internal static ProcessAudioRoutingResult TrySetProcessDefaultDevice(uint processId, DataFlow flow, IReadOnlyList<Role> roles, string deviceId)
        {
            ProcessAudioRoutingResult result = ProcessAudioRoutingResult.Failed;

            ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                result = SharedRoutingClient.TrySetProcessDefaultDevice(processId, flow, roles, deviceId);
            });

            return result;
        }

        internal static ProcessAudioRoutingResult TryClearProcessDefaultDevice(uint processId, DataFlow flow, IReadOnlyList<Role> roles)
        {
            ProcessAudioRoutingResult result = ProcessAudioRoutingResult.Failed;

            ComThreadingHelper.RunOnCoreAudioThread(() =>
            {
                result = SharedRoutingClient.TryClearProcessDefaultDevice(processId, flow, roles);
            });

            return result;
        }

        private sealed class NativePolicyConfigActivator : IPolicyConfigActivator
        {
            public bool TryCreate(Guid clsid, [NotNullWhen(true)] out IPolicyConfigHandle? handle, out int hresult)
            {
                handle = null;
                if (!NativeAudioInteropHelper.ComActivator.TryCreateTyped(clsid, AudioPolicyConfigClient.PolicyConfigInterfaceId, ClsCtxInProcServer, out IActivatedNativeComObject<IPolicyConfig>? activatedObject, out hresult))
                {
                    return false;
                }
                handle = new PolicyConfigHandle(activatedObject);
                return true;
            }
        }

        private sealed class NativeAudioRoutingPolicyActivator : IAudioRoutingPolicyActivator
        {
            public bool TryCreate([NotNullWhen(true)] out IAudioRoutingPolicyHandle? handle, out int hresult)
            {
                return AudioRoutingPolicyHandle.TryCreate(out handle, out hresult);
            }
        }

        private sealed class PolicyConfigHandle(IActivatedNativeComObject<IPolicyConfig> activatedObject) : IPolicyConfigHandle
        {
            private IActivatedNativeComObject<IPolicyConfig>? _activatedObject = activatedObject;

            public int SetDefaultEndpoint(string deviceId, Role role)
            {
                ObjectDisposedException.ThrowIf(_activatedObject == null, this);
                return _activatedObject!.Interface.SetDefaultEndpoint(deviceId, role);
            }

            public void Dispose()
            {
                _activatedObject?.Dispose();
                _activatedObject = null;
            }
        }

        private sealed partial class AudioRoutingPolicyHandle(nint interfacePtr) : IAudioRoutingPolicyHandle
        {
            private static readonly Guid[] KnownInterfaceIds =
            [
                new("2a59116d-6c4f-45e0-a74f-707e3fef9258"),
                new("ab3d4648-e242-459f-b02f-541c70306324"),
                new("32aa8e18-6496-4e24-9f94-b800e7eccc45")
            ];
            private const string ActivatableClassName = "Windows.Media.Internal.AudioPolicyConfig";

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int SetPersistedDefaultAudioEndpointDelegate(nint audioPolicyConfigFactory, uint processId, DataFlow flow, Role role, nint deviceId);

            [LibraryImport("AudioSes.dll", EntryPoint = "DllGetActivationFactory")]
            private static partial int DllGetActivationFactory(nint activatableClassId, out nint factoryPtr);

            [LibraryImport("combase.dll", EntryPoint = "WindowsCreateString", StringMarshalling = StringMarshalling.Utf16)]
            private static partial int WindowsCreateString(string source, int length, out nint hstring);

            [LibraryImport("combase.dll", EntryPoint = "WindowsDeleteString")]
            private static partial int WindowsDeleteString(nint hstring);

            private nint _interfacePtr = interfacePtr;
            private readonly nint _vfTable = Marshal.ReadIntPtr(interfacePtr);
            private readonly int _ptrSize = IntPtr.Size;

            public int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, string deviceId)
            {
                ObjectDisposedException.ThrowIf(_interfacePtr == nint.Zero, this);

                nint deviceIdHString = nint.Zero;
                try
                {
                    int createHr = WindowsCreateString(deviceId, deviceId.Length, out deviceIdHString);
                    if (createHr < 0)
                    {
                        return createHr;
                    }

                    nint methodPtr = Marshal.ReadIntPtr(_vfTable, _ptrSize * 25);
                    var method = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointDelegate>(methodPtr);
                    return method(_interfacePtr, processId, flow, role, deviceIdHString);
                }
                finally
                {
                    if (deviceIdHString != nint.Zero)
                    {
                        _ = WindowsDeleteString(deviceIdHString);
                    }
                }
            }

            public void Dispose()
            {
                nint interfacePtr = Interlocked.Exchange(ref _interfacePtr, nint.Zero);
                if (interfacePtr != nint.Zero)
                {
                    Marshal.Release(interfacePtr);
                }
            }

            internal static bool TryCreate([NotNullWhen(true)] out IAudioRoutingPolicyHandle? handle, out int hresult)
            {
                handle = null;
                nint className = nint.Zero;
                nint factoryPtr = nint.Zero;

                try
                {
                    hresult = WindowsCreateString(ActivatableClassName, ActivatableClassName.Length, out className);
                    if (hresult < 0)
                    {
                        return false;
                    }

                    hresult = DllGetActivationFactory(className, out factoryPtr);
                    if (hresult < 0)
                    {
                        return false;
                    }

                    foreach (Guid interfaceId in KnownInterfaceIds)
                    {
                        int queryHr = Marshal.QueryInterface(factoryPtr, in interfaceId, out nint queriedInterfacePtr);
                        if (queryHr >= 0 && queriedInterfacePtr != nint.Zero)
                        {
                            handle = new AudioRoutingPolicyHandle(queriedInterfacePtr);
                            hresult = 0;
                            return true;
                        }

                        hresult = queryHr;
                    }

                    hresult = unchecked((int)0x80004002);
                    return false;
                }
                finally
                {
                    if (factoryPtr != nint.Zero)
                    {
                        Marshal.Release(factoryPtr);
                    }

                    if (className != nint.Zero)
                    {
                        _ = WindowsDeleteString(className);
                    }
                }
            }
        }
    }
}
