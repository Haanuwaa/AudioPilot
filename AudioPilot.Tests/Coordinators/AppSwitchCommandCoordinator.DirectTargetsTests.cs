using AudioPilot.Coordinators;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchCommandCoordinatorDirectTargetsTests
{
    [Fact]
    public void ReplaceDirectRequest_CancelsPreviousRequestAndPublishesLatest()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        CancellationTokenSource? current = first;

        AppSwitchCommandCoordinator.ReplaceDirectRequest(ref current, second);

        Assert.True(first.IsCancellationRequested);
        Assert.Same(second, current);
        Assert.False(second.IsCancellationRequested);
    }
}
