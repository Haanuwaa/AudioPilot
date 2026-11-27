using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

[Collection("CoreAudioWorkerIsolation")]
public sealed class OperationTraceTests
{
    [Fact]
    public async Task ConcurrentRequestsKeepTheirIdentityAcrossComAndAsyncStages()
    {
        using var log = TestLoggerScope.CreateInMemory("operation-trace.log");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            string id = $"request-{index}";
            using var operation = OperationTrace.Start("command", log.Logger, id);
            await Task.Yield();
            string? captured = await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                log.Logger.Info("Probe", "worker");
                return OperationTrace.CurrentId;
            }, TestContext.Current.CancellationToken);
            Assert.Equal(id, captured);
            Assert.Equal(id, OperationTrace.CurrentId);
            operation.Complete();
        }, TestContext.Current.CancellationToken)));
        Assert.Null(OperationTrace.CurrentId);
        Assert.Null(await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => OperationTrace.CurrentId, TestContext.Current.CancellationToken));
        string text = log.DisposeAndReadLogText();
        for (int index = 0; index < 8; index++)
            Assert.Contains($"worker | traceId=request-{index}", text);
        Assert.Contains("outcome=success durationMs=", text);
    }

    [Fact]
    public void ExceptionAndExplicitDetachedWorkRestorePreviousContext()
    {
        using var log = TestLoggerScope.CreateInMemory("operation-nesting.log");
        using var parent = OperationTrace.Start("parent", log.Logger, "parent-id");
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var child = OperationTrace.Start("child", log.Logger, "child-id");
            throw new InvalidOperationException();
        }));
        Assert.Equal("parent-id", OperationTrace.CurrentId);
        using (OperationTrace.Attach(null)) Assert.Null(OperationTrace.CurrentId);
        Assert.Equal("parent-id", OperationTrace.CurrentId);
        parent.Complete();
        Assert.Contains("stage=child outcome=incomplete", log.DisposeAndReadLogText());
    }
}
