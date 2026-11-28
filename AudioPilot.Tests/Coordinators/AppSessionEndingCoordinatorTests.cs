using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSessionEndingCoordinatorTests
{
    [Fact]
    public void CancelledSessionEndingDoesNotStartPreparation()
    {
        TestExecutionGuards.RunSta(() => Assert.True(AppSessionEndingCoordinator.Prepare(true,
            Dispatcher.CurrentDispatcher, () => throw new InvalidOperationException("Must not start."),
            TimeSpan.FromSeconds(2), _ => Assert.Fail("Must not report a failure."))));
    }

    [Fact]
    public void SessionEndingPumpsDispatcherContinuationsBeforeReturning()
    {
        TestExecutionGuards.RunSta(() =>
        {
            bool saved = false;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Assert.True(AppSessionEndingCoordinator.Prepare(false, dispatcher, async () =>
            {
                await Task.Yield();
                Assert.True(dispatcher.CheckAccess());
                saved = true;
            }, TimeSpan.FromSeconds(5), _ => Assert.Fail("Preparation failed.")));
            Assert.True(saved);
        });
    }

    [Fact]
    public async Task ExpiredSessionEndingBudgetReturnsWithoutWaitingForAnOwnerAndObservesLateFailure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("synthetic late preparation failure");
        try
        {
            TestExecutionGuards.RunSta(() => Assert.False(AppSessionEndingCoordinator.Prepare(false,
                Dispatcher.CurrentDispatcher, () => release.Task, TimeSpan.FromMilliseconds(250),
                ex => reported.TrySetResult(ex))));
        }
        finally { release.TrySetException(error); }
        Assert.Same(error, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PreparationFailureIsReportedWithoutPreventingSessionEnding()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var error = new InvalidOperationException("synthetic preparation failure");
            Exception? reported = null;
            Assert.True(AppSessionEndingCoordinator.Prepare(false, Dispatcher.CurrentDispatcher,
                () => Task.FromException(error), TimeSpan.FromSeconds(5), ex => reported = ex));
            Assert.Same(error, reported);
        });
    }

    [Fact]
    public void SessionEndingNotifications_ParsesConfirmedAndCancelledEndSessionMessages()
    {
        Assert.True(AudioPilot.Platform.SessionEndingNotifications.TryParseEndSession(
            AudioPilot.Platform.SessionEndingNotifications.WmEndSession, 1, out bool endingTrue));
        Assert.True(endingTrue);

        Assert.True(AudioPilot.Platform.SessionEndingNotifications.TryParseEndSession(
            AudioPilot.Platform.SessionEndingNotifications.WmEndSession, 0, out bool endingFalse));
        Assert.False(endingFalse);

        Assert.False(AudioPilot.Platform.SessionEndingNotifications.TryParseEndSession(0x0011, 1, out bool other));
        Assert.False(other);
        Assert.True(AudioPilot.Platform.SessionEndingNotifications.IsCloseRequest(
            AudioPilot.Platform.SessionEndingNotifications.WmClose));
        Assert.False(AudioPilot.Platform.SessionEndingNotifications.IsCloseRequest(
            AudioPilot.Platform.SessionEndingNotifications.WmEndSession));
    }
}
