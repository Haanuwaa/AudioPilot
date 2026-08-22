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

    internal sealed class InputListenPropertyWriter(Logger logger, INativeAudioEndpointFactory? endpointFactory = null) : IInputListenPropertyWriter
    {
        private const uint StgmReadWrite = 0x00000002;
        private readonly Logger _logger = logger;
        private readonly INativeAudioEndpointFactory _endpointFactory = endpointFactory ?? NativeAudioInteropHelper.EndpointFactory;

        public bool TrySetListenProperties(IAudioEndpointInfo inputEndpoint, string? renderDeviceId, bool enabled, out string? error)
        {
            error = null;

            try
            {
                if (!_endpointFactory.IsAvailable || !_endpointFactory.TryCreate(inputEndpoint, out INativeAudioEndpoint? nativeEndpoint))
                {
                    error = AppConstants.Audio.ErrorCodes.Listen.WriteMmDeviceInterfaceUnavailable;
                    return false;
                }

                using (nativeEndpoint)
                {
                    if (!nativeEndpoint.TryOpenPropertyStore(StgmReadWrite, out INativePropertyStore? nativeStore, out int openHr))
                    {
                        error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteOpenStoreHrPrefix}{openHr:X8}";
                        return false;
                    }

                    using (nativeStore)
                    {
                        NativePropertyKey renderKey = AudioEndpointListenPropertyKeys.ListenRenderDevicePropertyKey;
                        if (enabled)
                        {
                            if (string.IsNullOrWhiteSpace(renderDeviceId))
                            {
                                error = AppConstants.Audio.ErrorCodes.Listen.WriteRenderIdMissing;
                                return false;
                            }

                            IntPtr pointer = IntPtr.Zero;
                            try
                            {
                                pointer = Marshal.StringToCoTaskMemUni(renderDeviceId);
                                var renderVariant = new NativePropVariant
                                {
                                    vt = (ushort)VarEnum.VT_LPWSTR,
                                    pointerValue = pointer,
                                };

                                int setRenderHr = nativeStore.SetValue(ref renderKey, ref renderVariant);
                                if (setRenderHr < 0)
                                {
                                    error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteRenderSetHrPrefix}{setRenderHr:X8}";
                                    return false;
                                }
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

                            int clearRenderHr = nativeStore.SetValue(ref renderKey, ref clearedRenderVariant);
                            if (clearRenderHr < 0)
                            {
                                error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteRenderSetHrPrefix}{clearRenderHr:X8}";
                                return false;
                            }
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
                            error = $"{AppConstants.Audio.ErrorCodes.Listen.WriteEnabledSetHrPrefix}{setEnabledHr:X8}";
                            return false;
                        }

                        int commitHr = nativeStore.Commit();
                        if (commitHr < 0)
                        {
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
    }
}
