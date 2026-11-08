using AudioPilot.Tests.Helpers;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class TufBridgeLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOutputIsRejectedWhileTheChildAndRequestWriterAreStillBlocked(bool standardError)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var oversized = new MemoryStream(new byte[16385]);
        using var empty = new MemoryStream();
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        Task output = TufLocalTrustClient.ReadBoundedAsync(standardError ? empty : oversized, 16384, cancellation.Token);
        Task errors = TufLocalTrustClient.ReadBoundedAsync(standardError ? oversized : empty, 16384, cancellation.Token);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => TufLocalTrustClient.WaitForBridgeAsync(exit.Task, writer, output, errors)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(exit.Task.IsCompleted);
            Assert.False(writer.IsCompleted);
        }
        finally { exit.TrySetResult(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer); }
    }
}
