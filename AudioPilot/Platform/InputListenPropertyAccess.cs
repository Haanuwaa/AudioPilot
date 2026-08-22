using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Platform
{
    internal interface IAudioEndpointInfo : IDisposable
    {
        string Id { get; }
        string Name { get; }
        MMDevice? Device { get; }
    }

    internal interface IInputListenPropertyReader
    {
        bool TryGetListenEnabled(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error);
        bool TryGetListenRenderTargetId(IAudioEndpointInfo inputEndpoint, out string? renderTargetId, out string? error);
    }

    internal sealed class MmDeviceAudioEndpointInfo(MMDevice device) : IAudioEndpointInfo
    {
        private MMDevice? _device = device;

        public string Id => _device?.ID ?? string.Empty;
        public string Name => _device?.FriendlyName ?? string.Empty;
        public MMDevice? Device => _device;

        public void Dispose()
        {
            _device?.Dispose();
            _device = null;
        }
    }

    internal sealed class InputListenPropertyReader(Logger logger, INativeAudioEndpointFactory? endpointFactory = null) : IInputListenPropertyReader
    {
        private const uint StgmRead = 0;
        private const int ErrorNotFound = unchecked((int)0x80070490);
        private readonly Logger _logger = logger;
        private readonly INativeAudioEndpointFactory _endpointFactory = endpointFactory ?? NativeAudioInteropHelper.EndpointFactory;

        private bool TryReadProperty<TResult>(
            IAudioEndpointInfo inputEndpoint,
            TResult defaultValue,
            string operationName,
            Func<INativePropertyStore, (bool Success, TResult Value, string? Error)> readProperty,
            out TResult value,
            out string? error)
        {
            value = defaultValue;
            error = null;

            try
            {
                if (!_endpointFactory.TryCreate(inputEndpoint, out INativeAudioEndpoint? nativeEndpoint))
                {
                    error = AppConstants.Audio.ErrorCodes.Listen.StateReadException;
                    return false;
                }

                using (nativeEndpoint)
                {
                    if (!nativeEndpoint.TryOpenPropertyStore(StgmRead, out INativePropertyStore? propertyStore, out _))
                    {
                        error = AppConstants.Audio.ErrorCodes.Listen.StateReadFailed;
                        return false;
                    }

                    using (propertyStore)
                    {
                        (bool success, TResult readValue, string? readError) = readProperty(propertyStore);
                        value = readValue;
                        error = readError;
                        return success;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("InputListenPropertyReader", AppConstants.Audio.LogEvents.Listen.StateReadPropertyStoreException, operationName, ex);
                error = AppConstants.Audio.ErrorCodes.Listen.StateReadFailed;
                return false;
            }
        }

        public bool TryGetListenEnabled(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error)
        {
            return TryReadProperty(
                inputEndpoint,
                defaultValue: false,
                operationName: nameof(TryGetListenEnabled),
                static propertyStore =>
                {
                    NativePropertyKey key = AudioEndpointListenPropertyKeys.ListenEnabledPropertyKey;
                    int readHr = propertyStore.GetValue(ref key, out NativePropVariant rawValue);
                    if (readHr == ErrorNotFound)
                    {
                        return (true, false, null);
                    }

                    if (readHr < 0)
                    {
                        return (false, false, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed);
                    }

                    using (rawValue)
                    {
                        if (rawValue.IsEmpty)
                        {
                            return (true, false, null);
                        }

                        if (!rawValue.TryGetBoolean(out bool parsed))
                        {
                            return (false, false, AppConstants.Audio.ErrorCodes.Listen.StateUnknownType);
                        }

                        return (true, parsed, null);
                    }
                },
                out enabled,
                out error);
        }

        public bool TryGetListenRenderTargetId(IAudioEndpointInfo inputEndpoint, out string? renderTargetId, out string? error)
        {
            return TryReadProperty(
                inputEndpoint,
                defaultValue: null,
                operationName: nameof(TryGetListenRenderTargetId),
                static propertyStore =>
                {
                    NativePropertyKey key = AudioEndpointListenPropertyKeys.ListenRenderDevicePropertyKey;
                    int readHr = propertyStore.GetValue(ref key, out NativePropVariant rawValue);
                    if (readHr == ErrorNotFound)
                    {
                        return (true, (string?)null, null);
                    }

                    if (readHr < 0)
                    {
                        return (false, (string?)null, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed);
                    }

                    using (rawValue)
                    {
                        if (!rawValue.TryGetString(out string? renderTargetId))
                        {
                            return (false, (string?)null, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed);
                        }

                        return (true, renderTargetId, null);
                    }
                },
                out renderTargetId,
                out error);
        }
    }
}
