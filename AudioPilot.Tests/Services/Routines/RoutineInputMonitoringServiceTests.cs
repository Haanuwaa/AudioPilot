using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineInputMonitoringServiceTests
{
    [Fact]
    public void Apply_WhenAlreadyInTargetState_IsSuccessfulNoOpWithoutCapture()
    {
        using var logger = TestLoggerScope.CreateInMemory("routine-input-monitoring-noop.log");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("input-1", "Microphone"),
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("output-1", "Headphones"),
        };
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, true, null));
        var writer = new FakeInputListenPropertyWriter();
        using var audio = new AudioDeviceService(writer, reader, resolver);

        RoutineInputMonitoringApplicationResult result = RoutineInputMonitoringService.Apply(
            audio, logger.Logger, enable: true, captureRestore: true, "test-noop");

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Null(result.Change);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public void Apply_WhenSetFails_ReturnsFailureDetailAndNoChange()
    {
        using var logger = TestLoggerScope.CreateInMemory("routine-input-monitoring-fail.log");
        var resolver = new FakeInputListenAudioDeviceResolver();
        var reader = new QueueInputListenPropertyReader();
        var writer = new FakeInputListenPropertyWriter();
        using var audio = new AudioDeviceService(writer, reader, resolver);

        RoutineInputMonitoringApplicationResult result = RoutineInputMonitoringService.Apply(
            audio, logger.Logger, enable: true, captureRestore: true, "test-fail");

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Null(result.Change);
        Assert.Contains("Input monitoring could not be changed", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAndRestore_RestoresOriginalStateWhenUnchangedExternally()
    {
        using var logger = TestLoggerScope.CreateInMemory("routine-input-monitoring-restore.log");
        var input = new FakeAudioEndpointInfo("input-1", "Microphone");
        var output = new FakeAudioEndpointInfo("output-1", "Headphones");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = input,
            DefaultPlaybackEndpoint = output,
        };
        resolver.RecordingEndpointsById["input-1"] = input;
        resolver.PlaybackEndpointsById["output-1"] = output;
        var reader = new QueueInputListenPropertyReader();
        // Initial read -> disabled (original = false)
        reader.ListenEnabledResults.Enqueue((true, false, null));
        // Verification read after enabling -> enabled
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "output-1", null));
        // Restore check read -> still enabled (matches applied = true)
        reader.ListenEnabledResults.Enqueue((true, true, null));
        // Verification read after restoring -> disabled
        reader.ListenEnabledResults.Enqueue((true, false, null));

        var writer = new FakeInputListenPropertyWriter { Result = true };
        using var audio = new AudioDeviceService(writer, reader, resolver);

        RoutineInputMonitoringApplicationResult result = RoutineInputMonitoringService.Apply(
            audio, logger.Logger, enable: true, captureRestore: true, "test-restore",
            () => ("output-1", "Headphones", null));

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.NotNull(result.Change);
        Assert.Equal(1, writer.Calls);
        Assert.True(writer.LastEnabled);

        result.Change!.Restore();
        Assert.Equal(2, writer.Calls);
        Assert.False(writer.LastEnabled);
    }

    [Fact]
    public void Restore_SkipsWhenStateWasModifiedExternally()
    {
        using var logger = TestLoggerScope.CreateInMemory("routine-input-monitoring-skip-external.log");
        var input = new FakeAudioEndpointInfo("input-1", "Microphone");
        var output = new FakeAudioEndpointInfo("output-1", "Headphones");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = input,
            DefaultPlaybackEndpoint = output,
        };
        resolver.RecordingEndpointsById["input-1"] = input;
        resolver.PlaybackEndpointsById["output-1"] = output;
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "output-1", null));
        // External user already disabled monitoring before restore runs
        reader.ListenEnabledResults.Enqueue((true, false, null));

        var writer = new FakeInputListenPropertyWriter { Result = true };
        using var audio = new AudioDeviceService(writer, reader, resolver);

        RoutineInputMonitoringApplicationResult result = RoutineInputMonitoringService.Apply(
            audio, logger.Logger, enable: true, captureRestore: true, "test-external",
            () => ("output-1", "Headphones", null));

        Assert.True(result.Success);
        Assert.NotNull(result.Change);
        Assert.Equal(1, writer.Calls);

        result.Change!.Restore();
        Assert.Equal(1, writer.Calls);
        Assert.Equal(3, reader.ListenEnabledCalls);
    }

    [Fact]
    public void Restore_SkipsWhenRecordingEndpointIsUnavailable()
    {
        using var logger = TestLoggerScope.CreateInMemory("routine-input-monitoring-skip-missing.log");
        var input = new FakeAudioEndpointInfo("input-1", "Microphone");
        var output = new FakeAudioEndpointInfo("output-1", "Headphones");
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = input,
            DefaultPlaybackEndpoint = output,
        };
        resolver.PlaybackEndpointsById["output-1"] = output;
        var reader = new QueueInputListenPropertyReader();
        reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.ListenEnabledResults.Enqueue((true, true, null));
        reader.RenderTargetResults.Enqueue((true, "output-1", null));

        var writer = new FakeInputListenPropertyWriter { Result = true };
        using var audio = new AudioDeviceService(writer, reader, resolver);

        RoutineInputMonitoringApplicationResult result = RoutineInputMonitoringService.Apply(
            audio, logger.Logger, enable: true, captureRestore: true, "test-missing",
            () => ("output-1", "Headphones", null));

        Assert.True(result.Success);
        Assert.NotNull(result.Change);
        Assert.Equal(1, writer.Calls);

        // input-1 is absent from RecordingEndpointsById, simulating a disconnected microphone.
        result.Change!.Restore();
        Assert.Equal(1, writer.Calls);
        Assert.Equal(2, reader.ListenEnabledCalls);
    }
}
