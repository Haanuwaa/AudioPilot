using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    internal interface IInputListenPropertyWriter
    {
        bool TrySetListenProperties(IAudioEndpointInfo inputEndpoint, string? renderDeviceId, bool enabled, out string? error);
    }

    internal static class AudioEndpointListenPropertyKeys
    {
        internal static readonly NativePropertyKey ListenEnabledPropertyKey = new(new Guid("24DBB0FC-9311-4B3D-9CF0-18FF155639D4"), 1);
        internal static readonly NativePropertyKey ListenRenderDevicePropertyKey = new(new Guid("24DBB0FC-9311-4B3D-9CF0-18FF155639D4"), 0);
    }

    internal delegate int PolicyConfigSetPropertyDelegate(string deviceId, ref NativePropertyKey key, ref NativePropVariant value);

    internal sealed class InputListenPropertyWriter(
        Logger logger,
        INativeAudioEndpointFactory? endpointFactory = null,
        PolicyConfigSetPropertyDelegate? policyConfigSetProperty = null,
        Func<bool>? ensureComInitialized = null) : IInputListenPropertyWriter
    {
        private const uint StgmReadWrite = 0x00000002;
        private const int ErrorAccessDenied = unchecked((int)0x80070005);
        private readonly Logger _logger = logger;
        private readonly INativeAudioEndpointFactory _endpointFactory = endpointFactory ?? NativeAudioInteropHelper.EndpointFactory;
        private readonly PolicyConfigSetPropertyDelegate? _policyConfigSetProperty = policyConfigSetProperty
            ?? (endpointFactory == null ? AudioPolicyConfig.SetPropertyValueOnCurrentThread : null);
        private readonly Func<bool> _ensureComInitialized = ensureComInitialized ?? ComThreadingHelper.EnsureComInitialized;

        public bool TrySetListenProperties(IAudioEndpointInfo inputEndpoint, string? renderDeviceId, bool enabled, out string? error)
        {
            error = null;

            try
            {
                _ = _ensureComInitialized();

                if (!_endpointFactory.IsAvailable || !_endpointFactory.TryCreate(inputEndpoint.Id, out INativeAudioEndpoint? nativeEndpoint))
                {
                    error = AppConstants.Audio.ErrorCodes.Listen.WriteMmDeviceInterfaceUnavailable;
                    return false;
                }

                using (nativeEndpoint)
                {
                    if (!nativeEndpoint.TryOpenPropertyStore(StgmReadWrite, out INativePropertyStore? nativeStore, out int openHr))
                    {
                        if (openHr == ErrorAccessDenied && TrySetViaPolicyConfig(inputEndpoint.Id, renderDeviceId, enabled))
                        {
                            return true;
                        }

                        error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteOpenStoreHrPrefix}{openHr:X8}";
                        return false;
                    }

                    using (nativeStore)
                    {
                        int setRenderHr = SetRenderProperty(nativeStore, renderDeviceId, enabled);
                        if (setRenderHr < 0)
                        {
                            if (setRenderHr == ErrorAccessDenied && TrySetViaPolicyConfig(inputEndpoint.Id, renderDeviceId, enabled))
                            {
                                return true;
                            }

                            error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteRenderSetHrPrefix}{setRenderHr:X8}";
                            return false;
                        }

                        var enabledVariant = new NativePropVariant
                        {
                            vt = (ushort)VarEnum.VT_UI4,
                            ulVal = enabled ? 1u : 0u,
                        };

                        NativePropertyKey enabledKey = AudioEndpointListenPropertyKeys.ListenEnabledPropertyKey;
                        int setEnabledHr = nativeStore.SetValue(ref enabledKey, ref enabledVariant);
                        if (setEnabledHr < 0)
                        {
                            if (setEnabledHr == ErrorAccessDenied && TrySetViaPolicyConfig(inputEndpoint.Id, renderDeviceId, enabled))
                            {
                                return true;
                            }

                            error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteEnabledSetHrPrefix}{setEnabledHr:X8}";
                            return false;
                        }

                        int commitHr = nativeStore.Commit();
                        if (commitHr < 0)
                        {
                            if (commitHr == ErrorAccessDenied && TrySetViaPolicyConfig(inputEndpoint.Id, renderDeviceId, enabled))
                            {
                                return true;
                            }

                            error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteCommitHrPrefix}{commitHr:X8}";
                            return false;
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("InputListenPropertyWriter", AppConstants.Audio.LogEvents.Listen.WritePropertyStoreException, nameof(TrySetListenProperties), ex);
                if (ex is COMException comException)
                {
                    error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteFailedHrPrefix}{comException.HResult:X8}";
                    return false;
                }

                error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteFailedExceptionPrefix}{ex.GetType().Name}";
                return false;
            }
        }

        private static int SetRenderProperty(INativePropertyStore nativeStore, string? renderDeviceId, bool enabled)
        {
            NativePropertyKey renderKey = AudioEndpointListenPropertyKeys.ListenRenderDevicePropertyKey;
            if (enabled && !string.IsNullOrWhiteSpace(renderDeviceId))
            {
                IntPtr pointer = IntPtr.Zero;
                try
                {
                    pointer = Marshal.StringToCoTaskMemUni(renderDeviceId);
                    var renderVariant = new NativePropVariant
                    {
                        vt = (ushort)VarEnum.VT_LPWSTR,
                        pointerValue = pointer,
                    };

                    return nativeStore.SetValue(ref renderKey, ref renderVariant);
                }
                finally
                {
                    if (pointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }

            // VT_EMPTY clears the explicit target so Windows follows the current default playback device.
            var clearedRenderVariant = new NativePropVariant
            {
                vt = (ushort)VarEnum.VT_EMPTY,
            };

            return nativeStore.SetValue(ref renderKey, ref clearedRenderVariant);
        }

        private bool TrySetViaPolicyConfig(string inputEndpointId, string? renderDeviceId, bool enabled)
        {
            if (_policyConfigSetProperty == null || string.IsNullOrWhiteSpace(inputEndpointId))
            {
                return false;
            }

            try
            {
                NativePropertyKey renderKey = AudioEndpointListenPropertyKeys.ListenRenderDevicePropertyKey;
                int setRenderHr;
                if (enabled && !string.IsNullOrWhiteSpace(renderDeviceId))
                {
                    IntPtr pointer = IntPtr.Zero;
                    try
                    {
                        pointer = Marshal.StringToCoTaskMemUni(renderDeviceId);
                        var renderVariant = new NativePropVariant
                        {
                            vt = (ushort)VarEnum.VT_LPWSTR,
                            pointerValue = pointer,
                        };
                        setRenderHr = _policyConfigSetProperty(inputEndpointId, ref renderKey, ref renderVariant);
                    }
                    finally
                    {
                        if (pointer != IntPtr.Zero)
                        {
                            Marshal.FreeCoTaskMem(pointer);
                        }
                    }
                }
                else
                {
                    var clearedRenderVariant = new NativePropVariant
                    {
                        vt = (ushort)VarEnum.VT_EMPTY,
                    };
                    setRenderHr = _policyConfigSetProperty(inputEndpointId, ref renderKey, ref clearedRenderVariant);
                }

                if (setRenderHr < 0)
                {
                    _logger.Debug("InputListenPropertyWriter", () => $"listen-policy-config-fallback-render-failed | id={LogPrivacy.Id(inputEndpointId)} hr=0x{setRenderHr:X8}");
                    return false;
                }

                NativePropertyKey enabledKey = AudioEndpointListenPropertyKeys.ListenEnabledPropertyKey;
                var enabledVariant = new NativePropVariant
                {
                    vt = (ushort)VarEnum.VT_UI4,
                    ulVal = enabled ? 1u : 0u,
                };
                int setEnabledHr = _policyConfigSetProperty(inputEndpointId, ref enabledKey, ref enabledVariant);
                if (setEnabledHr < 0)
                {
                    _logger.Debug("InputListenPropertyWriter", () => $"listen-policy-config-fallback-enabled-failed | id={LogPrivacy.Id(inputEndpointId)} hr=0x{setEnabledHr:X8}");
                    return false;
                }

                _logger.Debug("InputListenPropertyWriter", () => $"listen-policy-config-fallback-succeeded | id={LogPrivacy.Id(inputEndpointId)} enabled={enabled}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug("InputListenPropertyWriter", () => $"listen-policy-config-fallback-failed | id={LogPrivacy.Id(inputEndpointId)} error={ex.GetType().Name} hr=0x{ex.HResult:X8}");
                return false;
            }
        }
    }
}
