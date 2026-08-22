using System.Diagnostics.CodeAnalysis;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeAudioEndpointInfo(string id = "input-1", string name = "Microphone") : IAudioEndpointInfo
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public MMDevice? Device => null;

    public void Dispose()
    {
    }
}

internal sealed class FakeNativeAudioEndpointFactory(FakeNativeAudioEndpoint endpoint) : INativeAudioEndpointFactory
{
    public bool IsAvailable { get; set; } = true;
    public Exception? ExceptionToThrow { get; set; }
    public int CreateCalls { get; private set; }

    public bool TryCreate(IAudioEndpointInfo endpointInfo, [NotNullWhen(true)] out INativeAudioEndpoint? createdEndpoint)
    {
        CreateCalls++;
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        createdEndpoint = IsAvailable ? endpoint : null;
        return IsAvailable;
    }
}

internal sealed class FakeNativeAudioEndpoint(FakeNativePropertyStore propertyStore) : INativeAudioEndpoint
{
    public int OpenPropertyStoreResult { get; set; }
    public int OpenPropertyStoreCalls { get; private set; }

    public bool TryOpenPropertyStore(uint stgmAccess, [NotNullWhen(true)] out INativePropertyStore? openedPropertyStore, out int hresult)
    {
        OpenPropertyStoreCalls++;
        hresult = OpenPropertyStoreResult;
        openedPropertyStore = hresult < 0 ? null : propertyStore;
        return hresult >= 0;
    }

    public bool TryActivate<TInterface>(Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
        where TInterface : class
    {
        activatedObject = null;
        hresult = unchecked((int)0x80004002);
        return false;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeNativePropertyStore : INativePropertyStore
{
    public int GetValueResult { get; set; }
    public NativePropVariant ValueToReturn { get; set; }
    public Exception? GetValueException { get; set; }
    public int CommitResult { get; set; }
    public int SetRenderResult { get; set; }
    public int SetEnabledResult { get; set; }
    public int CommitCalls { get; private set; }
    public int SetRenderCalls { get; private set; }
    public int SetEnabledCalls { get; private set; }
    public ushort LastRenderVariantType { get; private set; }

    public int GetValue(ref NativePropertyKey key, out NativePropVariant value)
    {
        if (GetValueException != null)
        {
            throw GetValueException;
        }

        value = ValueToReturn;
        return GetValueResult;
    }

    public int SetValue(ref NativePropertyKey key, ref NativePropVariant value)
    {
        if (key.Equals(AudioEndpointListenPropertyKeys.ListenRenderDevicePropertyKey))
        {
            SetRenderCalls++;
            LastRenderVariantType = value.vt;
            return SetRenderResult;
        }

        if (key.Equals(AudioEndpointListenPropertyKeys.ListenEnabledPropertyKey))
        {
            SetEnabledCalls++;
            return SetEnabledResult;
        }

        return 0;
    }

    public int Commit()
    {
        CommitCalls++;
        return CommitResult;
    }

    public void Dispose()
    {
    }
}

internal sealed class InputListenNativeReaderHarness
{
    public FakeNativePropertyStore PropertyStore { get; }
    public FakeNativeAudioEndpoint Endpoint { get; }
    public FakeNativeAudioEndpointFactory Factory { get; }
    public InputListenPropertyReader Reader { get; }

    public InputListenNativeReaderHarness(FakeNativePropertyStore? propertyStore = null)
    {
        PropertyStore = propertyStore ?? new FakeNativePropertyStore();
        Endpoint = new FakeNativeAudioEndpoint(PropertyStore);
        Factory = new FakeNativeAudioEndpointFactory(Endpoint);
        Reader = new InputListenPropertyReader(Logger.Instance, Factory);
    }
}

internal sealed class InputListenNativeWriterHarness
{
    public FakeNativePropertyStore PropertyStore { get; }
    public FakeNativeAudioEndpoint Endpoint { get; }
    public FakeNativeAudioEndpointFactory Factory { get; }
    public InputListenPropertyWriter Writer { get; }

    public InputListenNativeWriterHarness(FakeNativePropertyStore? propertyStore = null)
    {
        PropertyStore = propertyStore ?? new FakeNativePropertyStore();
        Endpoint = new FakeNativeAudioEndpoint(PropertyStore);
        Factory = new FakeNativeAudioEndpointFactory(Endpoint);
        Writer = new InputListenPropertyWriter(Logger.Instance, Factory);
    }
}
