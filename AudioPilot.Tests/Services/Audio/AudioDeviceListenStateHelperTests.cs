using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceListenStateHelperTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExplicitUnavailableOrAmbiguousOutput_DoesNotFallBackToDefaultSpeakers(bool toggle, bool ambiguous)
    {
        var speakers = new FakeAudioEndpointInfo("speakers", "Speakers");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("mic", "Microphone"),
            DefaultPlaybackEndpoint = speakers,
        };
        if (ambiguous)
        {
            resolver.ActivePlaybackDeviceInfos.Add(new() { Id = "a", Name = "Headphones", StableId = "shared" });
            resolver.ActivePlaybackDeviceInfos.Add(new() { Id = "b", Name = "Headphones", StableId = "shared" });
        }
        var writer = new FakeInputListenPropertyWriter();
        var reader = new QueueInputListenPropertyReader();
        var helper = new AudioDeviceListenStateHelper(Logger.Instance, writer, reader, resolver);

        InputListenStateChangeResult result = toggle
            ? helper.ToggleCurrentInputListenState("missing", "Headphones", ambiguous ? "shared" : "missing-stable")
            : helper.SetCurrentInputListenState(true, "missing", "Headphones", ambiguous ? "shared" : "missing-stable");

        Assert.False(result.Applied);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.MonitorOutputUnavailable, result.Error);
        Assert.Equal("mic", result.InputDeviceId);
        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, resolver.GetDefaultPlaybackEndpointCalls);
        Assert.Equal(0, speakers.DisposeCalls);
    }

    [Fact]
    public void Disabling_DoesNotRequireTheConfiguredOrDefaultOutput()
    {
        var resolver = new FakeInputListenAudioDeviceResolver { DefaultRecordingEndpoint = new FakeAudioEndpointInfo("mic", "Microphone") };
        var writer = new FakeInputListenPropertyWriter();
        var helper = new AudioDeviceListenStateHelper(Logger.Instance, writer, new QueueInputListenPropertyReader(), resolver);

        InputListenStateChangeResult result = helper.SetCurrentInputListenState(false, "missing", "Headphones", "missing-stable");

        Assert.True(result.Verified);
        Assert.False(result.Enabled);
        Assert.Equal("mic", result.InputDeviceId);
        Assert.Null(result.MonitorOutputDeviceId);
        Assert.Null(writer.LastRenderDeviceId);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(0, resolver.GetDefaultPlaybackEndpointCalls);
    }

    [Theory]
    [InlineData(false, "verified")]
    [InlineData(true, "verified")]
    [InlineData(false, "unverified")]
    [InlineData(true, "unverified")]
    [InlineData(false, "write-failed")]
    [InlineData(true, "write-failed")]
    public void EndpointCleanupFailures_PreserveWriteResultAndReleaseBothEndpoints(bool toggle, string outcome)
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-cleanup.log", LogLevel.Warning);
        var input = new FakeAudioEndpointInfo("mic", "Microphone") { DisposeException = new InvalidOperationException("Input cleanup failed") };
        var output = new FakeAudioEndpointInfo("headset", "Headphones") { DisposeException = new InvalidOperationException("Output cleanup failed") };
        var resolver = new FakeInputListenAudioDeviceResolver { DefaultRecordingEndpoint = input, DefaultPlaybackEndpoint = output };
        var writer = new FakeInputListenPropertyWriter { Result = outcome != "write-failed", Error = outcome == "write-failed" ? "write-denied" : null };
        var reader = new QueueInputListenPropertyReader();
        if (toggle) reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.ListenEnabledResults.Enqueue((outcome != "unverified", true, null));
        reader.RenderTargetResults.Enqueue((true, "headset", null));
        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, writer, reader, resolver);

        InputListenStateChangeResult result = toggle ? helper.ToggleCurrentInputListenState(null, null) : helper.SetCurrentInputListenState(true, null, null);

        Assert.Equal(outcome != "write-failed", result.Applied);
        Assert.Equal(outcome == "verified", result.Verified);
        Assert.Equal(outcome switch { "write-failed" => "write-denied", "unverified" => AppConstants.Audio.ErrorCodes.Listen.StateVerifyUnknown, _ => null }, result.Error);
        Assert.Equal("mic", result.InputDeviceId);
        Assert.Equal("Microphone", result.InputDeviceName);
        Assert.Equal("headset", result.MonitorOutputDeviceId);
        Assert.Equal("Headphones", result.MonitorOutputDeviceName);
        Assert.Equal(1, input.DisposeCalls);
        Assert.Equal(1, output.DisposeCalls);
        Assert.Equal(2, loggerScope.DisposeAndReadLogText().Split("listen-endpoint-dispose-failed").Length - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndpointCleanupFailures_PreserveStatusResultAndReleaseBothEndpoints(bool nameFails)
    {
        var input = new FakeAudioEndpointInfo("mic", "Microphone") { DisposeException = new InvalidOperationException("Input cleanup failed") };
        var output = new FakeAudioEndpointInfo("headset", "Headphones")
        {
            DisposeException = new InvalidOperationException("Output cleanup failed"),
            NameException = nameFails ? new InvalidOperationException("Name unavailable") : null,
        };
        var resolver = new FakeInputListenAudioDeviceResolver { DefaultRecordingEndpoint = input };
        resolver.PlaybackEndpointsById["headset"] = output;
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "headset", null));
        var helper = new AudioDeviceListenStateHelper(Logger.Instance, new FakeInputListenPropertyWriter(), reader, resolver);

        bool success = helper.TryGetCurrentInputListenTargetOutputDeviceName(out string? name, out string? error);

        Assert.Equal(!nameFails, success);
        Assert.Equal(nameFails ? null : "Headphones", name);
        Assert.Equal(nameFails ? AppConstants.Audio.ErrorCodes.Listen.StateReadException : null, error);
        Assert.Equal(1, input.DisposeCalls);
        Assert.Equal(1, output.DisposeCalls);
    }

    [Fact]
    public void OptionalNameReadFailure_DoesNotPreventMonitoringOrInventDeviceNames()
    {
        var input = new FakeAudioEndpointInfo("mic") { NameException = new InvalidOperationException("Name unavailable") };
        var output = new FakeAudioEndpointInfo("headset") { NameException = new InvalidOperationException("Name unavailable") };
        var resolver = new FakeInputListenAudioDeviceResolver { DefaultRecordingEndpoint = input, DefaultPlaybackEndpoint = output };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "headset", null));
        var helper = new AudioDeviceListenStateHelper(Logger.Instance, new FakeInputListenPropertyWriter(), reader, resolver);

        InputListenStateChangeResult result = helper.SetCurrentInputListenState(true, null, null);

        Assert.True(result.Verified);
        Assert.Equal("mic", result.InputDeviceId);
        Assert.Null(result.InputDeviceName);
        Assert.Equal("headset", result.MonitorOutputDeviceId);
        Assert.Null(result.MonitorOutputDeviceName);
    }

    [Fact]
    public void ListenRuntimePreservesNameOnlyPreference()
    {
        var resolver = new FakeInputListenAudioDeviceResolver { DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input", "Microphone") };
        resolver.ActivePlaybackDeviceInfos.Add(new() { Id = "output", Name = "Preferred output" });
        resolver.PlaybackEndpointsById["output"] = new FakeAudioEndpointInfo("output", "Preferred output");
        var writer = new FakeInputListenPropertyWriter();
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "output", null));
        var helper = new AudioDeviceListenStateHelper(Logger.Instance, writer, reader, resolver);
        Assert.True(helper.SetCurrentInputListenState(true, null, "Preferred output").Applied);
        Assert.Equal("output", writer.LastRenderDeviceId);
    }

    [Theory]
    [InlineData(false, "old")]
    [InlineData(true, "old")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void ListenRuntimeResolvesStableIdentityDespiteDuplicateNames(bool ambiguous, string? preferredId)
    {
        var resolver = new FakeInputListenAudioDeviceResolver { DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input", "Microphone") };
        resolver.ActivePlaybackDeviceInfos.Add(new() { Id = "first", Name = "Same", StableId = "opaque" });
        resolver.ActivePlaybackDeviceInfos.Add(new() { Id = "second", Name = "Same", StableId = ambiguous ? "opaque" : "other" });
        resolver.PlaybackEndpointsById["first"] = new FakeAudioEndpointInfo("first", "Same");
        var writer = new FakeInputListenPropertyWriter();
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "first", null));
        var helper = new AudioDeviceListenStateHelper(Logger.Instance, writer, reader, resolver);
        InputListenStateChangeResult result = helper.SetCurrentInputListenState(true, preferredId, "Same", "opaque");
        Assert.Equal(!ambiguous, result.Applied);
        Assert.Equal(ambiguous ? null : "first", writer.LastRenderDeviceId);
    }

    [Fact]
    public void TryGetCurrentInputListenTargetOutputDeviceName_ReturnsFailure_WhenTargetEndpointUnavailable()
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

        Assert.False(success);
        Assert.Null(targetOutputDeviceName);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateReadFailed, error);
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

    [Theory]
    [InlineData(true, true, false, false, null)]
    [InlineData(true, false, false, true, "wrong-output")]
    [InlineData(false, true, true, false, null)]
    [InlineData(false, false, false, true, "stale-output")]
    public void SetCurrentInputListenState_RejectsKnownMismatch_WhenOtherPropertyCannotBeRead(
        bool enabled, bool stateReadable, bool postEnabled, bool targetReadable, string? postTargetId)
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceListenStateHelperTests), "listen-helper-partial-mismatch.log");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone"),
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("output-1", "Speakers")
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((stateReadable, postEnabled, null));
        reader.RenderTargetResults.Enqueue((targetReadable, postTargetId, null));
        var helper = new AudioDeviceListenStateHelper(loggerScope.Logger, new FakeInputListenPropertyWriter(), reader, resolver);

        InputListenStateChangeResult result = helper.SetCurrentInputListenState(enabled, null, null);

        Assert.False(result.Applied);
        Assert.Equal(AppConstants.Audio.ErrorCodes.Listen.StateVerifyMismatch, result.Error);
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
