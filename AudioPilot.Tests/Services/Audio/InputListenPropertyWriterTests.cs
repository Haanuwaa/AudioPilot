using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class InputListenPropertyWriterTests
{
    [Fact]
    public void TrySetListenProperties_WhenFactoryUnavailable_ReturnsInterfaceUnavailableError()
    {
        var harness = new InputListenNativeWriterHarness();
        harness.Factory.IsAvailable = false;

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.WriteMmDeviceInterfaceUnavailable, error);
        Assert.Equal(0, harness.Factory.CreateCalls);
        Assert.Equal(0, harness.Endpoint.OpenPropertyStoreCalls);
    }

    [Fact]
    public void TrySetListenProperties_WhenEnabledWithNullRenderTarget_WritesEmptyVariantForDynamicDefaultOutput()
    {
        var harness = new InputListenNativeWriterHarness();

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: null, enabled: true, out string? error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(1, harness.Endpoint.OpenPropertyStoreCalls);
        Assert.Equal(1, harness.PropertyStore.SetRenderCalls);
        Assert.Equal((ushort)VarEnum.VT_EMPTY, harness.PropertyStore.LastRenderVariantType);
        Assert.Equal(1, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(1, harness.PropertyStore.CommitCalls);
    }

    [Theory]
    [InlineData("output-1", (ushort)VarEnum.VT_LPWSTR)]
    [InlineData(null, (ushort)VarEnum.VT_EMPTY)]
    public void TrySetListenProperties_WhenOpenPropertyStoreReturnsAccessDenied_FallsBackToPolicyConfig(string? renderDeviceId, ushort expectedRenderVt)
    {
        var propertyStore = new FakeNativePropertyStore();
        var endpoint = new FakeNativeAudioEndpoint(propertyStore) { OpenPropertyStoreResult = unchecked((int)0x80070005) };
        var factory = new FakeNativeAudioEndpointFactory(endpoint);
        List<(string DeviceId, uint Pid, ushort Vt, string? StringValue, uint UlVal)> calls = [];
        var writer = new InputListenPropertyWriter(
            AudioPilot.Logging.Logger.Instance,
            factory,
            (deviceId, ref key, ref value) =>
            {
                string? stringValue = value.vt == (ushort)VarEnum.VT_LPWSTR ? Marshal.PtrToStringUni(value.pointerValue) : null;
                uint ulVal = value.vt == (ushort)VarEnum.VT_UI4 ? value.ulVal : 0u;
                calls.Add((deviceId, key.PropertyId, value.vt, stringValue, ulVal));
                return 0;
            },
            static () => true);

        bool success = writer.TrySetListenProperties(new FakeAudioEndpointInfo("mic-1"), renderDeviceId, enabled: true, out string? error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(2, calls.Count);
        Assert.Equal(("mic-1", 0u, expectedRenderVt, renderDeviceId, 0u), calls[0]);
        Assert.Equal(("mic-1", 1u, (ushort)VarEnum.VT_UI4, null, 1u), calls[1]);
    }

    [Fact]
    public void TrySetListenProperties_WhenPolicyConfigFallbackFails_ReturnsOriginalAccessDeniedError()
    {
        var propertyStore = new FakeNativePropertyStore();
        var endpoint = new FakeNativeAudioEndpoint(propertyStore) { OpenPropertyStoreResult = unchecked((int)0x80070005) };
        var factory = new FakeNativeAudioEndpointFactory(endpoint);
        var writer = new InputListenPropertyWriter(
            AudioPilot.Logging.Logger.Instance,
            factory,
            (_, ref _, ref _) => unchecked((int)0x80004005),
            static () => true);

        bool success = writer.TrySetListenProperties(new FakeAudioEndpointInfo("mic-1"), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteOpenStoreHrPrefix}80070005", error);
    }

    [Fact]
    public void PolicyConfigInterface_PropertyValueMethodsIncludeFxStoreParameter()
    {
        var getParams = typeof(IPolicyConfig).GetMethod(nameof(IPolicyConfig.GetPropertyValue))!.GetParameters();
        var setParams = typeof(IPolicyConfig).GetMethod(nameof(IPolicyConfig.SetPropertyValue))!.GetParameters();

        Assert.Equal(4, getParams.Length);
        Assert.Equal(typeof(string), getParams[0].ParameterType);
        Assert.Equal(typeof(bool), getParams[1].ParameterType);
        Assert.Equal(typeof(NativePropertyKey).MakeByRefType(), getParams[2].ParameterType);
        Assert.Equal(typeof(NativePropVariant).MakeByRefType(), getParams[3].ParameterType);

        Assert.Equal(4, setParams.Length);
        Assert.Equal(typeof(string), setParams[0].ParameterType);
        Assert.Equal(typeof(bool), setParams[1].ParameterType);
        Assert.Equal(typeof(NativePropertyKey).MakeByRefType(), setParams[2].ParameterType);
        Assert.Equal(typeof(NativePropVariant).MakeByRefType(), setParams[3].ParameterType);
    }

    [Fact]
    public void TrySetListenProperties_WhenCommitFails_ReturnsCommitError()
    {
        var harness = new InputListenNativeWriterHarness(new FakeNativePropertyStore
        {
            CommitResult = unchecked((int)0x80004005)
        });

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteCommitHrPrefix}{unchecked((int)0x80004005):X8}", error);
        Assert.Equal(1, harness.PropertyStore.SetRenderCalls);
        Assert.Equal(1, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(1, harness.PropertyStore.CommitCalls);
    }

    [Fact]
    public void TrySetListenProperties_WhenSettingRenderTargetFails_ReturnsRenderSetError()
    {
        var harness = new InputListenNativeWriterHarness(new FakeNativePropertyStore
        {
            SetRenderResult = unchecked((int)0x80004005)
        });

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteRenderSetHrPrefix}{unchecked((int)0x80004005):X8}", error);
        Assert.Equal(1, harness.PropertyStore.SetRenderCalls);
        Assert.Equal(0, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(0, harness.PropertyStore.CommitCalls);
    }

    [Fact]
    public void TrySetListenProperties_WhenSettingEnabledFails_ReturnsEnabledSetError()
    {
        var harness = new InputListenNativeWriterHarness(new FakeNativePropertyStore
        {
            SetEnabledResult = unchecked((int)0x80004005)
        });

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteEnabledSetHrPrefix}{unchecked((int)0x80004005):X8}", error);
        Assert.Equal(1, harness.PropertyStore.SetRenderCalls);
        Assert.Equal(1, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(0, harness.PropertyStore.CommitCalls);
    }

    [Fact]
    public void TrySetListenProperties_WhenOpenPropertyStoreFails_ReturnsOpenStoreError()
    {
        var harness = new InputListenNativeWriterHarness();
        harness.Endpoint.OpenPropertyStoreResult = unchecked((int)0x80004005);

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteOpenStoreHrPrefix}{unchecked((int)0x80004005):X8}", error);
        Assert.Equal(1, harness.Endpoint.OpenPropertyStoreCalls);
        Assert.Equal(0, harness.PropertyStore.SetRenderCalls);
        Assert.Equal(0, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(0, harness.PropertyStore.CommitCalls);
    }

    [Fact]
    public void TrySetListenProperties_WhenFactoryThrowsComException_ReturnsFailedHrError()
    {
        var harness = new InputListenNativeWriterHarness();
        harness.Factory.ExceptionToThrow = new COMException("factory failed", unchecked((int)0x80004005));

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteFailedHrPrefix}{unchecked((int)0x80004005):X8}", error);
    }

    [Fact]
    public void TrySetListenProperties_WhenFactoryThrowsNonComException_ReturnsExceptionTypeError()
    {
        var harness = new InputListenNativeWriterHarness();
        harness.Factory.ExceptionToThrow = new InvalidOperationException("factory failed");

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: "output-1", enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal($"{AppConstants.Audio.ErrorCodes.Listen.WriteFailedExceptionPrefix}{nameof(InvalidOperationException)}", error);
    }

    [Fact]
    public void TrySetListenProperties_WhenDisabling_ClearsRenderTargetBeforeCommit()
    {
        var harness = new InputListenNativeWriterHarness();

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: null, enabled: false, out string? error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(1, harness.PropertyStore.SetRenderCalls);
        Assert.Equal(0, harness.PropertyStore.LastRenderVariantType);
        Assert.Equal(1, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(1, harness.PropertyStore.CommitCalls);
    }
}
