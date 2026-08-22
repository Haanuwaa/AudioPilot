namespace AudioPilot.Tests;

public sealed class AppRuntimeHostShutdownTests
{
    [Fact]
    public async Task StalledOwner_PreventsDependentResourceDisposal()
    {
        var ownerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AppShutdownStepResult result = await AppRuntimeHost.EvaluateShutdownStepAsync(
            ownerCompletion.Task,
            Task.CompletedTask);
        bool dependentDisposed = false;
        if (AppRuntimeHost.CanDisposeDependentResources(result))
        {
            dependentDisposed = true;
        }

        Assert.Equal(AppShutdownStepOutcome.TimedOut, result.Outcome);
        Assert.False(dependentDisposed);
        ownerCompletion.SetResult();
    }

    [Fact]
    public async Task FaultedOwner_AllowsDependentResourceDisposalAfterOwnerHasStopped()
    {
        var expected = new InvalidOperationException("synthetic cleanup failure");

        AppShutdownStepResult result = await AppRuntimeHost.EvaluateShutdownStepAsync(
            Task.FromException(expected),
            new TaskCompletionSource().Task);

        Assert.Equal(AppShutdownStepOutcome.Faulted, result.Outcome);
        Assert.Same(expected, result.Exception);
        Assert.True(AppRuntimeHost.CanDisposeDependentResources(result));
    }
}
