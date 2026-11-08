using System.Diagnostics;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Platform;

public sealed class SingleInstanceProcessRecoveryHelperTests
{
    [Fact]
    public void CannotOpenTheCurrentProcessAsARecoveryTarget()
    {
        using Process current = Process.GetCurrentProcess();
        Assert.Null(SingleInstanceProcessRecoveryHelper.TryOpen(new(current.Id, current.StartTime.ToUniversalTime().Ticks), Logger.Instance));
    }

    [Fact]
    public void CannotOpenAnUnavailableProcessAsARecoveryTarget()
    {
        Assert.Null(SingleInstanceProcessRecoveryHelper.TryOpen(new(int.MaxValue, 1), Logger.Instance));
    }
}
