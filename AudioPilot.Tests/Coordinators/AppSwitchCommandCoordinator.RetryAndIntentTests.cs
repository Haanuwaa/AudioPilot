using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class AppSwitchCommandCoordinatorTests
{


    [Fact]
    public void TryQueueCoalescedRetry_WhenRetryBegins_LogsQueuedReason_AndClonesCycleSnapshot()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-coalesced-retry.log", LogLevel.Debug);
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "Configured A" },
            new CycleDevice { Id = "id-b", Name = "Configured B" },
        ];

        int capturedDelayMs = -1;
        IReadOnlyList<CycleDevice>? capturedCycle = null;

        long debounceWindowTicks = TimeSpan.FromMilliseconds(250).Ticks;
        long lastRequestTicks = DateTime.UtcNow.AddMilliseconds(-100).Ticks;

        InvokeTryQueueCoalescedRetry(
            coordinator,
            tryBeginRetry: static () => true,
            skipEventName: "switch-skip",
            opId: "op-coalesced",
            rejectionReason: AppSwitchRequestRejectionReason.Debounced,
            configuredCycle: configuredCycle,
            lastRequestTicks: lastRequestTicks,
            debounceMs: 250,
            runBackgroundAsync: (delayMs, cycleSnapshot) =>
            {
                capturedDelayMs = delayMs;
                capturedCycle = cycleSnapshot;
                return Task.CompletedTask;
            });

        Assert.NotNull(capturedCycle);
        Assert.Equal(2, capturedCycle.Count);
        Assert.NotSame(configuredCycle, capturedCycle);
        Assert.Equal("Configured A", capturedCycle[0].Name);
        Assert.True(capturedDelayMs >= 1 && capturedDelayMs <= 250, $"Expected retry delay between 1 and 250 ms, got {capturedDelayMs}.");

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-skip | opId=op-coalesced reason=coalesced-retry-queued trigger=Debounced", logText, StringComparison.Ordinal);
    }


    [Theory]
    [InlineData("failed", true, false, "success")]
    [InlineData("failed", false, true, "deferred")]
    [InlineData("failed", false, false, "failed")]
    [InlineData("pending", false, false, "pending")]
    public void ResolvePhaseResult_ReturnsExpectedValue(string currentPhaseResult, bool success, bool deferredAutoSwitchScheduled, string expected)
    {
        string actual = AppSwitchCommandCoordinator.ResolvePhaseResult(currentPhaseResult, success, deferredAutoSwitchScheduled);

        Assert.Equal(expected, actual);
    }


    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, false)]
    [InlineData(0, 0, false)]
    public void IsSwitchIntentCurrent_ReturnsExpectedValue(int latestIntentVersion, int intentVersion, bool expected)
    {
        bool actual = AppSwitchCommandCoordinator.IsSwitchIntentCurrent(latestIntentVersion, intentVersion);

        Assert.Equal(expected, actual);
    }

}
