using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests;

public sealed class AppDispatcherHelperTests
{
    [Fact]
    public void Dispatch_RunsActionOnDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            int calls = 0;
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

            AppDispatcherHelper.Dispatch(
                dispatcher,
                Logger.Instance,
                () => calls++,
                "dispatch-failed",
                nameof(Dispatch_RunsActionOnDispatcher));

            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public void ExecuteAsync_ShowsError_WhenActionThrows()
    {
        var messages = new RecordingAppDialogService();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            AppDispatcherHelper.ExecuteAsync(
                () => throw new InvalidOperationException("boom"),
                Logger.Instance,
                Dispatcher.CurrentDispatcher,
                messages,
                "hotkey-failed",
                "User-visible failure",
                nameof(ExecuteAsync_ShowsError_WhenActionThrows))
                .GetAwaiter()
                .GetResult();

            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        });

        Assert.Contains(messages.ErrorMessages, call =>
            string.Equals(call.message, "User-visible failure", StringComparison.Ordinal));
    }

    [Fact]
    public void InvokeAsync_CompletesAfterAsyncActionRunsOnDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            bool completed = false;

            Task task = AppDispatcherHelper.InvokeAsync(
                Dispatcher.CurrentDispatcher,
                Logger.Instance,
                async () =>
                {
                    await Task.Yield();
                    completed = true;
                },
                "hotkey-failed",
                nameof(InvokeAsync_CompletesAfterAsyncActionRunsOnDispatcher));

            TestPrivateAccess.RunTaskOnDispatcher(task);

            Assert.True(completed);
        });
    }

    [Fact]
    public void InvokeAsync_WithBackgroundPriority_DrainsPendingInputBeforeStartingAction()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var executionOrder = new List<string>();

            Task background = AppDispatcherHelper.InvokeAsync(
                dispatcher,
                Logger.Instance,
                () =>
                {
                    executionOrder.Add("background");
                    return Task.CompletedTask;
                },
                "dispatch-failed",
                nameof(InvokeAsync_WithBackgroundPriority_DrainsPendingInputBeforeStartingAction),
                DispatcherPriority.Background);
            Task input = dispatcher.InvokeAsync(
                () => executionOrder.Add("input"),
                DispatcherPriority.Input).Task;

            TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(background, input));

            Assert.Equal(["input", "background"], executionOrder);
        });
    }

    [Fact]
    public void InvokeAsync_WithInputPriority_DefersTheActionWithoutBackgroundStarvation()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var executionOrder = new List<string>();

            Task input = AppDispatcherHelper.InvokeAsync(
                dispatcher,
                Logger.Instance,
                () =>
                {
                    executionOrder.Add("input");
                    return Task.CompletedTask;
                },
                "dispatch-failed",
                nameof(InvokeAsync_WithInputPriority_DefersTheActionWithoutBackgroundStarvation),
                DispatcherPriority.Input);

            Assert.Empty(executionOrder);

            Task background = dispatcher.InvokeAsync(
                () => executionOrder.Add("background"),
                DispatcherPriority.Background).Task;

            TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(input, background));

            Assert.Equal(["input", "background"], executionOrder);
        });
    }

    [Fact]
    public void InvokeAsync_ReturnsCompletedTask_WhenDispatcherShutdownHasStarted()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeShutdown();

            Task task = AppDispatcherHelper.InvokeAsync(
                dispatcher,
                Logger.Instance,
                static () => Task.CompletedTask,
                "hotkey-failed",
                nameof(InvokeAsync_ReturnsCompletedTask_WhenDispatcherShutdownHasStarted));

            Assert.True(task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void ExecuteAsync_DoesNotShowError_WhenDispatcherShutdownHasStarted()
    {
        var messages = new RecordingAppDialogService();
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeShutdown();

            AppDispatcherHelper.ExecuteAsync(
                () => throw new InvalidOperationException("boom"),
                Logger.Instance,
                dispatcher,
                messages,
                "hotkey-failed",
                "User-visible failure",
                nameof(ExecuteAsync_DoesNotShowError_WhenDispatcherShutdownHasStarted))
                .GetAwaiter()
                .GetResult();
        });

        Assert.Empty(messages.ErrorMessages);
    }
}
