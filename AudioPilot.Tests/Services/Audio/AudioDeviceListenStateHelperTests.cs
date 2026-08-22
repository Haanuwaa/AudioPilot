using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceListenStateHelperTests
{
    [Fact]
    public void TryGetCurrentInputListenTargetOutputDeviceName_ReturnsTrue_WhenTargetEndpointUnavailable()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-target.log", LogLevel.Trace);
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "missing-output", null));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = helper.TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error);

        Assert.True(success);
        Assert.Null(targetOutputDeviceName);
        Assert.Null(error);
    }

    [Fact]
    public void TryGetCurrentInputListenTargetOutputDeviceName_ReturnsNull_WhenListenDisabled()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-disabled.log");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.RenderTargetResults.Enqueue((true, "stale-output", null));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = helper.TryGetCurrentInputListenTargetOutputDeviceName(out string? targetOutputDeviceName, out string? error);

        Assert.True(success);
        Assert.Null(targetOutputDeviceName);
        Assert.Null(error);
        Assert.Equal(1, reader.ListenEnabledCalls);
        Assert.Equal(0, reader.RenderTargetCalls);
    }

    [Fact]
    public void TrySetCurrentInputListenState_ReturnsNoDefaultOutputDevice_WhenEnabledWithoutPlaybackTarget()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-no-output.log");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, new FakeInputListenPropertyWriter(), new QueueInputListenPropertyReader(), resolver);

        bool success = helper.TrySetCurrentInputListenState(true, null, out bool changed, out string? error);

        Assert.False(success);
        Assert.False(changed);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.NoDefaultOutputDevice, error);
    }

    [Fact]
    public void TryToggleCurrentInputListenState_ReturnsReadError_WhenCurrentStateCannotBeRead()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-toggle.log");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((false, false, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = helper.TryToggleCurrentInputListenState(null, out bool enabled, out string? error);

        Assert.False(success);
        Assert.False(enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
    }

    [Fact]
    public void TrySetCurrentInputListenState_RemapsStalePreferredOutputId_ByUniqueNameMatch()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-remap.log", LogLevel.Trace);
        var writer = new FakeInputListenPropertyWriter();
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone")
        };
        resolver.PlaybackEndpointsById["fresh-output"] = new FakeAudioEndpointInfo("fresh-output", "Bluetooth Headset Stereo");
        resolver.ActivePlaybackDeviceInfos.Add(new CycleDevice { Id = "fresh-output", Name = "Bluetooth Headset Stereo" });
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "fresh-output", null));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, writer, reader, resolver);

        bool success = helper.TrySetCurrentInputListenState(
            true,
            "missing-output",
            "Bluetooth Headset",
            out bool changed,
            out string? error);

        Assert.True(success);
        Assert.True(changed);
        Assert.Null(error);
        Assert.Equal("fresh-output", writer.LastRenderDeviceId);
    }

    [Fact]
    public void TrySetCurrentInputListenState_ReturnsVerifyMismatch_WhenVerifiedRenderTargetDiffers()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-verify-mismatch.log");
        var writer = new FakeInputListenPropertyWriter();
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone"),
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("output-1", "Speakers")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "output-2", null));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, writer, reader, resolver);

        bool success = helper.TrySetCurrentInputListenState(true, null, out bool changed, out string? error);

        Assert.False(success);
        Assert.False(changed);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateVerifyMismatch, error);
        Assert.Equal("output-1", writer.LastRenderDeviceId);
    }

    [Fact]
    public void SetCurrentInputListenState_ReturnsAppliedUnverifiedAndLogsWarning_WhenVerificationCannotReadBack()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-verify-unknown.log", LogLevel.Warning);
        var writer = new FakeInputListenPropertyWriter();
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone"),
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("output-1", "Speakers")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((false, false, AppConstants.Audio.ErrorCodes.Listen.StateReadFailed));
        reader.RenderTargetResults.Enqueue((true, "output-1", null));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, writer, reader, resolver);

        InputListenStateChangeResult result = helper.SetCurrentInputListenState(true, null, string.Empty);
        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(InputListenApplyStatus.AppliedUnverified, result.Status);
        Assert.True(result.Applied);
        Assert.False(result.Verified);
        Assert.True(result.Enabled);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateVerifyUnknown, result.Error);
        Assert.Contains(AppConstants.Audio.LogEvents.Listen.SetVerifyUnknown, logText);
        Assert.Contains("stateReadVerified=False", logText);
        Assert.Contains("renderTargetReadVerified=True", logText);
    }

    [Fact]
    public void TryToggleCurrentInputListenState_UsesSameInputEndpointForReadAndWrite()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-stable-input.log");
        var writer = new FakeInputListenPropertyWriter();
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("output-1", "Speakers")
        };
        resolver.DefaultRecordingEndpoints.Enqueue(new FakeAudioEndpointInfo("input-1", "Microphone A"));
        resolver.DefaultRecordingEndpoints.Enqueue(new FakeAudioEndpointInfo("input-2", "Microphone B"));
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "output-1", null));

        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, writer, reader, resolver);

        bool success = helper.TryToggleCurrentInputListenState(null, out bool enabled, out string? error);

        Assert.True(success);
        Assert.True(enabled);
        Assert.Null(error);
        Assert.Equal("input-1", writer.LastInputDeviceId);
        Assert.Equal(1, resolver.GetDefaultRecordingEndpointCalls);
    }
}
