using AudioPilot.Constants;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceServiceListenTests
{
    [Fact]
    public void TrySetCurrentInputListenState_WhenNoDefaultInputDevice_ReturnsExpectedError()
    {
        var resolver = new FakeInputListenAudioDeviceResolver();
        var reader = new QueueInputListenPropertyReader();
        var writer = new FakeInputListenPropertyWriter();
        using var service = new AudioDeviceService(writer, reader, resolver);

        bool success = service.TrySetCurrentInputListenState(enabled: true, out bool changed, out string? error);

        Assert.False(success);
        Assert.False(changed);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.NoDefaultInputDevice, error);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public void TrySetCurrentInputListenState_WhenVerificationMismatches_ReturnsFalse()
    {
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone"),
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("output-1", "Speakers")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, false, null));

        var writer = new FakeInputListenPropertyWriter { Result = true };
        using var service = new AudioDeviceService(writer, reader, resolver);

        bool success = service.TrySetCurrentInputListenState(enabled: true, out bool changed, out string? error);

        Assert.False(success);
        Assert.False(changed);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateVerifyMismatch, error);
        Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public void TryGetCurrentInputListenState_WhenReaderReturnsUnknownType_FailsWithReaderError()
    {
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((false, false, AppConstants.Audio.ErrorCodes.Listen.StateUnknownType));

        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = service.TryGetCurrentInputListenState(out bool enabled, out string? error);

        Assert.False(success);
        Assert.False(enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateUnknownType, error);
    }

    [Fact]
    public void TryGetCurrentInputListenTargetOutputDeviceName_WhenReaderFails_ReturnsReaderError()
    {
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((false, null, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed));

        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = service.TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error);

        Assert.False(success);
        Assert.Null(targetOutputDeviceName);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TryGetCurrentInputListenTargetOutputDeviceName_WhenListenDisabled_IgnoresStoredTarget()
    {
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.RenderTargetResults.Enqueue((true, "output-1", null));

        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = service.TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error);

        Assert.True(success);
        Assert.Null(targetOutputDeviceName);
        Assert.Null(error);
        Assert.Equal(0, reader.RenderTargetCalls);
    }
}
