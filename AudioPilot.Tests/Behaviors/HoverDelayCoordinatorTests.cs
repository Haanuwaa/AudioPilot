using AudioPilot.Behaviors;

namespace AudioPilot.Tests.Behaviors;

public sealed class HoverDelayCoordinatorTests
{
    [Fact]
    public void StartOrRestart_CancelsAndDisposesPreviousSource()
    {
        using var previous = new CancellationTokenSource();
        CancellationTokenSource? current = previous;

        CancellationToken replacementToken = HoverDelayCoordinator.StartOrRestart(ref current);

        Assert.True(previous.IsCancellationRequested);
        Assert.NotNull(current);
        Assert.False(current!.IsCancellationRequested);
        Assert.Equal(current.Token, replacementToken);
        Assert.Throws<ObjectDisposedException>(() => _ = previous.Token.WaitHandle);

        current.Dispose();
    }

    [Fact]
    public void CancelAndDispose_ClearsCurrentSourceReference()
    {
        CancellationTokenSource? current = new();

        HoverDelayCoordinator.CancelAndDispose(ref current);

        Assert.Null(current);
    }
}
