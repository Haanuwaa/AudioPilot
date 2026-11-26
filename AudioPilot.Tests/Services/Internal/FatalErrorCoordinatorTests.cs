using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Internal;

public sealed class FatalErrorCoordinatorTests
{
    [Fact]
    public async Task FatalHandlingQuiescesBeforeTheDialogAndAlwaysSharesOneCleanup()
    {
        List<string> stages = [];
        var quiesced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new FatalErrorCoordinator(
            () => { stages.Add("quiesce"); return quiesced.Task; },
            _ => { stages.Add("report"); return Task.FromResult(FatalLogWriteResult.Disabled); },
            message => { stages.Add("dialog"); dialog.SetResult(message); return acknowledged.Task; },
            () => { stages.Add("shutdown"); return Task.CompletedTask; },
            (_, _) => stages.Add("unexpected-secondary-error"));

        Task handling = coordinator.HandleAsync(new InvalidOperationException());
        Assert.Same(handling, coordinator.HandleAsync(new InvalidOperationException()));
        Assert.Equal(["quiesce"], stages);
        quiesced.SetResult();
        string message = await dialog.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("logging is disabled", message);
        Assert.Equal(["quiesce", "report", "dialog"], stages);
        acknowledged.SetResult();
        await handling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["quiesce", "report", "dialog", "shutdown"], stages);
    }

    [Theory]
    [InlineData("quiesce")]
    [InlineData("report")]
    [InlineData("dialog")]
    public async Task SecondaryFailuresCannotPreventFinalCleanupEvenIfTheirLoggerThrows(string failedStage)
    {
        int shutdowns = 0;
        List<string> failures = [];
        var failure = new InvalidOperationException();
        var coordinator = new FatalErrorCoordinator(
            () => failedStage == "quiesce" ? Task.FromException(failure) : Task.CompletedTask,
            _ => failedStage == "report" ? Task.FromException<FatalLogWriteResult>(failure) : Task.FromResult(FatalLogWriteResult.Unavailable),
            _ => failedStage == "dialog" ? Task.FromException(failure) : Task.CompletedTask,
            () => { shutdowns++; return Task.CompletedTask; },
            (stage, _) => { failures.Add(stage); throw new InvalidOperationException(); });

        await coordinator.HandleAsync(failure);
        Assert.Equal(1, shutdowns);
        Assert.Equal([failedStage], failures);
    }

    [Fact]
    public async Task CleanupFailureIsReportedAndRemainsObservable()
    {
        List<string> failures = [];
        var coordinator = new FatalErrorCoordinator(() => Task.CompletedTask,
            _ => Task.FromResult(FatalLogWriteResult.Unavailable), _ => Task.CompletedTask,
            () => Task.FromException(new InvalidOperationException("cleanup")), (stage, _) => failures.Add(stage));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(new InvalidOperationException()));
        Assert.Equal(["shutdown"], failures);
        Assert.DoesNotContain("Details were written", FatalErrorCoordinator.BuildMessage(FatalLogWriteResult.Unavailable));
        Assert.DoesNotContain("Details were written", FatalErrorCoordinator.BuildMessage(FatalLogWriteResult.TimedOut));
    }
}
