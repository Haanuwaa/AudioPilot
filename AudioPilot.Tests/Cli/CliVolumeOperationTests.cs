using AudioPilot.Cli;

namespace AudioPilot.Tests.Cli;

public sealed class CliVolumeOperationTests
{
    [Theory]
    [InlineData(98f, 5f, 100f)]
    [InlineData(2f, -5f, 0f)]
    [InlineData(40f, 0f, 40f)]
    public void RelativeAdjustmentClampsAndZeroDoesNotUnmute(float current, float delta, float expected)
    {
        int writes = 0;
        int projections = 0;
        var (Success, Output) = CliVolumeOperation.Execute(true, "endpoint-id", delta, true, true, true,
            (out volume, out muted) => { volume = current; muted = true; return true; },
            (value, out applied, out muted) => { writes++; applied = value; muted = value == 0f; Assert.Equal(expected, value); return true; },
            () => "failed", (_, _) => projections++);
        Assert.True(Success);
        Assert.Equal(delta == 0f ? 0 : 1, writes);
        Assert.Equal(writes, projections);
        Assert.DoesNotContain("endpoint-id", Output);
    }

    [Theory]
    [InlineData(float.NaN, true)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(-1f, false)]
    [InlineData(101f, true)]
    public void InvalidRequestsNeverTouchAnEndpoint(float requested, bool relative)
    {
        var (Success, Output) = CliVolumeOperation.Execute(false, "endpoint", requested, relative, true, false,
            (out volume, out muted) => throw new InvalidOperationException("Unexpected read"),
            (value, out applied, out muted) => throw new InvalidOperationException("Unexpected write"),
            () => "failed");
        Assert.False(Success);
    }

    [Fact]
    public void FailedWriteDoesNotProjectOrReportSuccess()
    {
        var (Success, Output) = CliVolumeOperation.Execute(true, "endpoint", 5f, true, true, false,
            (out value, out muted) => { value = 20; muted = false; return true; },
            (value, out applied, out muted) => { applied = 0; muted = false; return false; },
            () => "Endpoint disappeared", (_, _) => throw new InvalidOperationException("Unexpected projection"));
        Assert.False(Success);
        Assert.Contains("Endpoint disappeared", Output);
    }
}
