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

    internal readonly record struct InputListenPropertiesSnapshot(
        bool EnabledRead,
        bool Enabled,
        string? EnabledError,
        bool RenderTargetRead,
        string? RenderTargetId,
        string? RenderTargetError);

    internal interface IInputListenPropertyReader
    {
        bool TryGetListenEnabled(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error);
        bool TryGetListenRenderTargetId(IAudioEndpointInfo inputEndpoint, out string? renderTargetId, out string? error);

        InputListenPropertiesSnapshot ReadListenSnapshot(IAudioEndpointInfo inputEndpoint)
        {
            bool enabledRead = TryGetListenEnabled(inputEndpoint, out bool enabled, out string? enabledError);
            bool renderTargetRead = TryGetListenRenderTargetId(inputEndpoint, out string? renderTargetId, out string? renderTargetError);
            return new InputListenPropertiesSnapshot(enabledRead, enabled, enabledError, renderTargetRead, renderTargetId, renderTargetError);
        }
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

    internal sealed class InputListenPropertyReader(
        Logger logger,
        INativeAudioEndpointFactory? endpointFactory = null,
        Func<bool>? ensureComInitialized = null) : IInputListenPropertyReader
    {
        private const uint StgmRead = 0;
        private const int ErrorNotFound = unchecked((int)0x80070490);
        private readonly Logger _logger = logger;
        private readonly INativeAudioEndpointFactory _endpointFactory = endpointFactory ?? NativeAudioInteropHelper.EndpointFactory;
        private readonly Func<bool> _ensureComInitialized = ensureComInitialized ?? ComThreadingHelper.EnsureComInitialized;

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
                _ = _ensureComInitialized();

                if (!_endpointFactory.TryCreate(inputEndpoint.Id, out INativeAudioEndpoint? nativeEndpoint))
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

        public InputListenPropertiesSnapshot ReadListenSnapshot(IAudioEndpointInfo inputEndpoint)
        {
            try
            {
                _ = _ensureComInitialized();

                if (!_endpointFactory.TryCreate(inputEndpoint.Id, out INativeAudioEndpoint? nativeEndpoint))
                {
                    string err = AppConstants.Audio.ErrorCodes.Listen.StateReadException;
                    return new InputListenPropertiesSnapshot(false, false, err, false, null, err);
                }

                using (nativeEndpoint)
                {
                    if (!nativeEndpoint.TryOpenPropertyStore(StgmRead, out INativePropertyStore? propertyStore, out _))
                    {
                        string err = AppConstants.Audio.ErrorCodes.Listen.StateReadFailed;
                        return new InputListenPropertiesSnapshot(false, false, err, false, null, err);
                    }

                    using (propertyStore)
                    {
                        (bool enabledRead, bool enabled, string? enabledError) = ReadEnabledFromStore(propertyStore);
                        (bool renderRead, string? renderTargetId, string? renderError) = ReadRenderTargetFromStore(propertyStore);
                        return new InputListenPropertiesSnapshot(enabledRead, enabled, enabledError, renderRead, renderTargetId, renderError);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("InputListenPropertyReader", AppConstants.Audio.LogEvents.Listen.StateReadPropertyStoreException, nameof(ReadListenSnapshot), ex);
                string err = AppConstants.Audio.ErrorCodes.Listen.StateReadFailed;
                return new InputListenPropertiesSnapshot(false, false, err, false, null, err);
            }
        }

        public bool TryGetListenEnabled(IAudioEndpointInfo inputEndpoint, out bool enabled, out string? error)
        {
            return TryReadProperty(
                inputEndpoint,
                defaultValue: false,
                operationName: nameof(TryGetListenEnabled),
                ReadEnabledFromStore,
                out enabled,
                out error);
        }

        public bool TryGetListenRenderTargetId(IAudioEndpointInfo inputEndpoint, out string? renderTargetId, out string? error)
        {
            return TryReadProperty(
                inputEndpoint,
                defaultValue: null,
                operationName: nameof(TryGetListenRenderTargetId),
                ReadRenderTargetFromStore,
                out renderTargetId,
                out error);
        }

        private static (bool Success, bool Value, string? Error) ReadEnabledFromStore(INativePropertyStore propertyStore)
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
        }

        private static (bool Success, string? Value, string? Error) ReadRenderTargetFromStore(INativePropertyStore propertyStore)
        {
            NativePropertyKey key = AudioEndpointListenPropertyKeys.ListenRenderDevicePropertyKey;
            int readHr = propertyStore.GetValue(ref key, out NativePropVariant rawValue);
            if (readHr == ErrorNotFound)
            {
                return (true, null, null);
            }

            if (readHr < 0)
            {
                return (false, null, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed);
            }

            using (rawValue)
            {
                if (!rawValue.TryGetString(out string? renderTargetId))
                {
                    return (false, null, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed);
                }

                return (true, renderTargetId, null);
            }
        }
    }
}
