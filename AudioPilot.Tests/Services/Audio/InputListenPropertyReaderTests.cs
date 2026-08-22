using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class InputListenPropertyReaderTests
{
    [Fact]
    public void TryGetListenEnabled_WhenEndpointFactoryUnavailable_ReturnsReadException()
    {
        var harness = new InputListenNativeReaderHarness();
        harness.Factory.IsAvailable = false;

        bool success = harness.Reader.TryGetListenEnabled(new FakeAudioEndpointInfo(), out bool enabled, out string? error);

        Assert.False(success);
        Assert.False(enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadException, error);
    }

    [Fact]
    public void TryGetListenEnabled_WhenPropertyMissing_ReturnsSuccessWithDefaultFalse()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            GetValueResult = unchecked((int)0x80070490)
        });

        bool success = harness.Reader.TryGetListenEnabled(new FakeAudioEndpointInfo(), out bool enabled, out string? error);

        Assert.True(success);
        Assert.False(enabled);
        Assert.Null(error);
    }

    [Fact]
    public void TryGetListenEnabled_WhenValueEmpty_ReturnsSuccessWithDefaultFalse()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            ValueToReturn = new NativePropVariant { vt = (ushort)VarEnum.VT_EMPTY }
        });

        bool success = harness.Reader.TryGetListenEnabled(new FakeAudioEndpointInfo(), out bool enabled, out string? error);

        Assert.True(success);
        Assert.False(enabled);
        Assert.Null(error);
    }

    [Fact]
    public void TryGetListenEnabled_WhenOpenPropertyStoreFails_ReturnsReadFailed()
    {
        var harness = new InputListenNativeReaderHarness();
        harness.Endpoint.OpenPropertyStoreResult = unchecked((int)0x80004005);

        bool success = harness.Reader.TryGetListenEnabled(new FakeAudioEndpointInfo(), out bool enabled, out string? error);

        Assert.False(success);
        Assert.False(enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TryGetListenEnabled_WhenVariantTypeUnsupported_ReturnsUnknownType()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            ValueToReturn = new NativePropVariant { vt = (ushort)VarEnum.VT_R4 }
        });

        bool success = harness.Reader.TryGetListenEnabled(new FakeAudioEndpointInfo(), out bool enabled, out string? error);

        Assert.False(success);
        Assert.False(enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateUnknownType, error);
    }

    [Fact]
    public void TryGetListenEnabled_WhenPropertyStoreThrows_ReturnsReadFailed()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            GetValueException = new InvalidOperationException("boom")
        });

        bool success = harness.Reader.TryGetListenEnabled(new FakeAudioEndpointInfo(), out bool enabled, out string? error);

        Assert.False(success);
        Assert.False(enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TryGetListenRenderTargetId_WhenReadFails_ReturnsReadFailed()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            GetValueResult = unchecked((int)0x80004005)
        });

        bool success = harness.Reader.TryGetListenRenderTargetId(new FakeAudioEndpointInfo(), out string? renderTargetId, out string? error);

        Assert.False(success);
        Assert.Null(renderTargetId);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TryGetListenRenderTargetId_WhenPropertyMissing_ReturnsSuccessWithNullTarget()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            GetValueResult = unchecked((int)0x80070490)
        });

        bool success = harness.Reader.TryGetListenRenderTargetId(new FakeAudioEndpointInfo(), out string? renderTargetId, out string? error);

        Assert.True(success);
        Assert.Null(renderTargetId);
        Assert.Null(error);
    }

    [Fact]
    public void TryGetListenRenderTargetId_WhenEndpointFactoryUnavailable_ReturnsReadException()
    {
        var harness = new InputListenNativeReaderHarness();
        harness.Factory.IsAvailable = false;

        bool success = harness.Reader.TryGetListenRenderTargetId(new FakeAudioEndpointInfo(), out string? renderTargetId, out string? error);

        Assert.False(success);
        Assert.Null(renderTargetId);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadException, error);
    }

    [Fact]
    public void TryGetListenRenderTargetId_WhenEndpointFactoryThrows_ReturnsReadFailed()
    {
        var harness = new InputListenNativeReaderHarness();
        harness.Factory.ExceptionToThrow = new InvalidOperationException("factory boom");

        bool success = harness.Reader.TryGetListenRenderTargetId(new FakeAudioEndpointInfo(), out string? renderTargetId, out string? error);

        Assert.False(success);
        Assert.Null(renderTargetId);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TryGetListenRenderTargetId_WhenVariantTypeUnsupported_ReturnsReadFailed()
    {
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            ValueToReturn = new NativePropVariant { vt = (ushort)VarEnum.VT_BOOL }
        });

        bool success = harness.Reader.TryGetListenRenderTargetId(new FakeAudioEndpointInfo(), out string? renderTargetId, out string? error);

        Assert.False(success);
        Assert.Null(renderTargetId);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TryGetListenRenderTargetId_WhenStringPresent_ReturnsRenderTargetId()
    {
        IntPtr pointer = Marshal.StringToCoTaskMemUni("output-1");
        var harness = new InputListenNativeReaderHarness(new FakeNativePropertyStore
        {
            ValueToReturn = new NativePropVariant
            {
                vt = (ushort)VarEnum.VT_LPWSTR,
                pointerValue = pointer,
            }
        });

        bool success = harness.Reader.TryGetListenRenderTargetId(new FakeAudioEndpointInfo(), out string? renderTargetId, out string? error);

        Assert.True(success);
        Assert.Equal("output-1", renderTargetId);
        Assert.Null(error);
    }
}
