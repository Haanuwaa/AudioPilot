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
    public void TrySetListenProperties_WhenRenderTargetMissing_ReturnsExpectedError()
    {
        var harness = new InputListenNativeWriterHarness();

        bool success = harness.Writer.TrySetListenProperties(new FakeAudioEndpointInfo(), renderDeviceId: null, enabled: true, out string? error);

        Assert.False(success);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.WriteRenderIdMissing, error);
        Assert.Equal(1, harness.Endpoint.OpenPropertyStoreCalls);
        Assert.Equal(0, harness.PropertyStore.SetRenderCalls);
        Assert.Equal(0, harness.PropertyStore.SetEnabledCalls);
        Assert.Equal(0, harness.PropertyStore.CommitCalls);
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
