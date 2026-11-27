using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceProcessRoutingHelperTests
{
    [Theory]
    [InlineData(false, "other", "device-1", false, 0)]
    [InlineData(true, "other", "device-1", true, 2)]
    [InlineData(true, "device-1", "device-1", false, 1)]
    [InlineData(true, "other", null, false, 2)]
    public void ApplyProcessDeviceRouting_OnlyReportsConfirmedNewPreferences(
        bool detectChange, string before, string? after, bool changed, int expectedReads)
    {
        using var loggerScope = TestLoggerScope.CreateInMemory("process-routing-change.log");
        var router = new FakeProcessAudioRouter { Preference = new(true, false, before) };
        router.OnSet = () => router.Preference = after == null ? default : new(true, false, after);
        var helper = new AudioDeviceProcessRoutingHelper(loggerScope.Logger, router, 10);
        var result = helper.ApplyProcessDeviceRouting(42, DataFlow.Render, "device-1", "Speakers",
            static () => [Role.Multimedia], "app-process-output", "test", "op-1",
            static (_, _) => null, static (_, _) => { }, detectChange);

        Assert.Equal(ProcessAudioRoutingResult.Applied, result.Result);
        Assert.Equal(changed, result.Changed);
        Assert.Equal(expectedReads, router.PreferenceReads);
    }

    [Fact]
    public void ApplyProcessDeviceRouting_ResetsDeferredLogCount_WhenApplied()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceProcessRoutingHelperTests), "process-routing-applied.log");
        var router = new FakeProcessAudioRouter { SetResult = ProcessAudioRoutingResult.Applied };
        var helper = new AudioDeviceProcessRoutingHelper(loggerScope.Logger, router, 10);
        int resetCalls = 0;

        ProcessAudioDeviceSwitchResult result = helper.ApplyProcessDeviceRouting(
            42,
            DataFlow.Render,
            "device-1",
            "Speakers",
            () => [Role.Multimedia],
            "app-process-output",
            "SwitchApplicationOutputDeviceAsync",
            "op-1",
            static (_, _) => null,
            (_, _) => resetCalls++);

        Assert.Equal(ProcessAudioRoutingResult.Applied, result.Result);
        Assert.Equal("Speakers", result.DeviceName);
        Assert.Equal(1, resetCalls);
    }

    [Fact]
    public void ApplyProcessDeviceRouting_UsesDeferredOccurrence_WhenNoAudio()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceProcessRoutingHelperTests), "process-routing-deferred.log");
        var router = new FakeProcessAudioRouter { SetResult = ProcessAudioRoutingResult.DeferredNoAudio };
        var helper = new AudioDeviceProcessRoutingHelper(loggerScope.Logger, router, 10);

        ProcessAudioDeviceSwitchResult result = helper.ApplyProcessDeviceRouting(
            42,
            DataFlow.Render,
            "device-1",
            "Speakers",
            () => [Role.Multimedia],
            "app-process-output",
            "SwitchApplicationOutputDeviceAsync",
            "op-1",
            static (_, _) => 3,
            static (_, _) => { });

        Assert.Equal(ProcessAudioRoutingResult.DeferredNoAudio, result.Result);
        Assert.Equal("Speakers", result.DeviceName);
    }

    [Fact]
    public void TryResetProcessDeviceRouting_ReturnsFalse_WhenRouterFails()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceProcessRoutingHelperTests), "process-routing-reset.log");
        var router = new FakeProcessAudioRouter { ClearResult = ProcessAudioRoutingResult.Failed };
        var helper = new AudioDeviceProcessRoutingHelper(loggerScope.Logger, router, 10);

        bool success = helper.TryResetProcessDeviceRouting(42, DataFlow.Render, [Role.Multimedia], "app-process-output-reset", "op-1", "TryResetApplicationDeviceRouting");

        Assert.False(success);
    }

    [Fact]
    public void ApplyProcessDeviceRouting_RedactsProcessId_InLogs()
    {
        using var loggerScope = new TestLoggerScope(nameof(ApplyProcessDeviceRouting_RedactsProcessId_InLogs), "process-routing-privacy.log");
        var router = new FakeProcessAudioRouter { SetResult = ProcessAudioRoutingResult.Applied, ClearResult = ProcessAudioRoutingResult.Failed };
        var helper = new AudioDeviceProcessRoutingHelper(loggerScope.Logger, router, 10);

        ProcessAudioDeviceSwitchResult applyResult = helper.ApplyProcessDeviceRouting(
            42,
            DataFlow.Render,
            "device-1",
            "Speakers",
            () => [Role.Multimedia],
            "app-process-output",
            "SwitchApplicationOutputDeviceAsync",
            "op-privacy",
            static (_, _) => null,
            static (_, _) => { });

        bool resetResult = helper.TryResetProcessDeviceRouting(42, DataFlow.Render, [Role.Multimedia], "app-process-output-reset", "op-privacy", "TryResetApplicationDeviceRouting");

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Equal(ProcessAudioRoutingResult.Applied, applyResult.Result);
        Assert.False(resetResult);
        Assert.Contains("app-process-output-success | opId=op-privacy flow=Render pid=id[", logText, StringComparison.Ordinal);
        Assert.Contains("app-process-output-reset-failed | opId=op-privacy flow=Render pid=id[", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("pid=42", logText, StringComparison.Ordinal);
        Assert.Contains("pid=id[len=2 hash=", logText, StringComparison.Ordinal);
    }

    private sealed class FakeProcessAudioRouter : IProcessAudioRouter
    {
        public ProcessAudioRoutingResult SetResult { get; set; } = ProcessAudioRoutingResult.Applied;
        public ProcessAudioRoutingResult ClearResult { get; set; } = ProcessAudioRoutingResult.Applied;
        public ApplicationDevicePreference Preference { get; set; }
        public Action? OnSet { get; set; }
        public int PreferenceReads { get; private set; }

        public ApplicationDevicePreference ReadProcessPreference(uint processId, DataFlow flow, IReadOnlyList<Role> roles)
        {
            PreferenceReads++;
            return Preference;
        }

        public ProcessAudioRoutingResult TrySetProcessDevice(uint processId, DataFlow flow, string targetDeviceId, IReadOnlyList<Role> roles)
        {
            OnSet?.Invoke();
            return SetResult;
        }

        public ProcessAudioRoutingResult TryClearProcessDevice(uint processId, DataFlow flow, IReadOnlyList<Role> roles)
        {
            return ClearResult;
        }
    }
}
