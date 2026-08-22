using AudioPilot.Cli;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelCliDeviceWaitTests
{
    [Theory]
    [InlineData(0, 0, 250, 0)]
    [InlineData(0, 1000, 250, 250)]
    [InlineData(800, 1000, 250, 200)]
    [InlineData(1000, 1000, 250, 0)]
    [InlineData(1200, 1000, 250, 0)]
    public void ResolveDeviceWaitDelayMs_RespectsRemainingTimeout(
        long elapsedMs,
        int timeoutMs,
        int pollIntervalMs,
        int expectedDelayMs)
    {
        int delayMs = CliWaitTiming.ResolveRemainingDelayMs(elapsedMs, timeoutMs, pollIntervalMs);

        Assert.Equal(expectedDelayMs, delayMs);
    }
}
