using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class AppViewModelApplicationTriggerDispatcherTests
{
    [Fact]
    public async Task Activation_WaitsForDispatchedWorkAndObservesFailures()
    {
        using var dispatcherReady = new ManualResetEventSlim(initialState: false);
        Dispatcher? dispatcher = null;
        var dispatcherThread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcherReady.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AudioPilot.Tests.ApplicationTriggerActivationDispatcher",
        };
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Assert.True(dispatcherReady.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.NotNull(dispatcher);

        try
        {
            using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(dispatcher!);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var routine = new AudioRoutine
            {
                Id = "routine-activation-dispatch-wait",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                UsesApplicationTrigger = true,
                OutputDeviceId = "output-device",
            };

            Task activationTask = harness.ViewModel.ExecuteRoutineFromApplicationTriggerForTestsAsync(
                routine,
                () =>
                {
                    entered.TrySetResult();
                    return release.Task;
                });

            Task firstCompletion = await Task.WhenAny(entered.Task, activationTask)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            if (ReferenceEquals(firstCompletion, activationTask))
            {
                await activationTask;
            }

            Assert.Same(entered.Task, firstCompletion);
            Assert.False(activationTask.IsCompleted);

            release.TrySetResult();
            await activationTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(activationTask.IsCompletedSuccessfully);

            activationTask = harness.ViewModel.ExecuteRoutineFromApplicationTriggerForTestsAsync(
                routine,
                () => Task.FromException(new InvalidOperationException("injected activation failure")));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await activationTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Equal("injected activation failure", exception.Message);
        }
        finally
        {
            if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false })
            {
                dispatcher.InvokeShutdown();
            }

            Assert.True(dispatcherThread.Join(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Deactivation_WaitsForDispatchedWorkAndObservesFailures()
    {
        using var dispatcherReady = new ManualResetEventSlim(initialState: false);
        Dispatcher? dispatcher = null;
        var dispatcherThread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcherReady.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AudioPilot.Tests.ApplicationTriggerDispatcher",
        };
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Assert.True(dispatcherReady.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.NotNull(dispatcher);

        try
        {
            using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(dispatcher!);
            Assert.False(harness.ViewModel.IsCleaningUpForTests());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var routine = new AudioRoutine
            {
                Id = "routine-dispatch-wait",
                TriggerKind = RoutineTriggerKind.Application,
                UsesApplicationTrigger = true,
            };

            Task deactivationTask = harness.ViewModel.DeactivateRoutineFromApplicationTriggerForTestsAsync(
                routine,
                processId: 321,
                sessionKey =>
                {
                    Assert.Equal("application-launch:routine-dispatch-wait:321", sessionKey);
                    entered.TrySetResult();
                    return release.Task;
                });

            Task firstCompletion = await Task.WhenAny(entered.Task, deactivationTask)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            if (ReferenceEquals(firstCompletion, deactivationTask))
            {
                await deactivationTask;
            }

            Assert.Same(entered.Task, firstCompletion);
            Assert.False(deactivationTask.IsCompleted);

            release.TrySetResult();
            await deactivationTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(deactivationTask.IsCompletedSuccessfully);

            routine = new AudioRoutine
            {
                Id = "routine-dispatch-failure",
                TriggerKind = RoutineTriggerKind.Application,
                UsesApplicationTrigger = true,
            };
            deactivationTask = harness.ViewModel.DeactivateRoutineFromApplicationTriggerForTestsAsync(
                routine,
                processId: 654,
                _ => Task.FromException(new InvalidOperationException("injected deactivation failure")));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await deactivationTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Equal("injected deactivation failure", exception.Message);
        }
        finally
        {
            if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false })
            {
                dispatcher.InvokeShutdown();
            }

            Assert.True(dispatcherThread.Join(TimeSpan.FromSeconds(2)));
        }
    }
}
