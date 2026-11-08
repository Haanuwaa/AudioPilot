using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineRestorationDispatcherTests
{
    [Fact]
    public async Task CancellationDuringCapture_PreventsExecutionAndRegistration()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task cancel = Task.Run(async () =>
        {
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                cancellation.Cancel();
            }
            finally { release.Set(); }
        }, TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
                CreateRoutine(), 42, false, "application", Logger.Instance,
                _ =>
                {
                    entered.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                    return new("old", "Old speaker", "", "");
                },
                (_, _, _, _) => throw new InvalidOperationException("Cancelled activation executed."),
                (_, _, _) => throw new InvalidOperationException("Cancelled activation registered."),
                AppViewModel.BuildRoutineExecutionLogContext, AppViewModel.BuildRoutineExecutionResultLogContext,
                cancellationToken: cancellation.Token));
        }
        finally { release.Set(); await cancel; }
    }

    [Fact]
    public Task Capture_KeepsInputResponsiveAndPresentationOnDispatcher() =>
        WithBlockedAudioAsync(async (block, dispatcher) =>
        {
            bool captured = false;
            bool registered = false;
            var result = await AppViewModelRoutineStatefulActivationHelper.ExecuteAsync(
                CreateRoutine(), 42, false, "application", Logger.Instance,
                _ => { block(); captured = true; return new("old", "Old speaker", "", ""); },
                (_, _, _, _) =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    Assert.True(captured);
                    return Task.FromResult(new RoutineExecutionResult(true, "Speaker", null));
                },
                (_, _, snapshot) =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    Assert.Equal("old", snapshot?.PreviousOutputDeviceId);
                    registered = true;
                },
                AppViewModel.BuildRoutineExecutionLogContext, AppViewModel.BuildRoutineExecutionResultLogContext,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(registered);
            Assert.True(result.Result.Success);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FallbackRestoration_KeepsInputResponsive(bool blockVolume) =>
        WithBlockedAudioAsync(async (block, dispatcher) =>
        {
            bool switched = false;
            var session = new RoutineStatefulSession("session", "routine", "Routine", RoutineTriggerKind.Application,
                1, true, new("old", "Old speaker", "", "", 50));
            var result = await AppRoutineRestoreCoordinator.ExecuteRestoreAsync(session, new(
                (id, name) => { if (!blockVolume) block(); return new() { Id = id, Name = name }; },
                () => "current",
                async (_, _, _) => { await Task.Yield(); switched = true; },
                (_, _) => null,
                (_, _, _) => throw new InvalidOperationException("Unexpected input switch"),
                (_, _, _) => { Assert.True(switched); if (blockVolume) block(); return Task.CompletedTask; }), Logger.Instance);
            Assert.True(dispatcher.CheckAccess());
            Assert.True(result.OutputVolumeRestored);
            Assert.Equal("Old speaker", result.RestoredOutputDeviceName);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OwnedCleanup_KeepsInputResponsiveAndHoldsActivationGate(bool blockDispose) =>
        WithBlockedAudioAsync(async (block, dispatcher) =>
        {
            var lifetime = new RoutineLifetimeService();
            var routine = CreateRoutine();
            bool disposed = false;
            bool applied = false;
            Task<bool>? nextActivation = null;
            var change = new CallbackChange(
                () => { if (!blockDispose) BlockAndQueueActivation(); },
                () => { if (blockDispose) BlockAndQueueActivation(); disposed = true; });
            void BlockAndQueueActivation()
            {
                nextActivation = lifetime.RunActivationAsync(routine, 84, _ =>
                {
                    Assert.True(disposed);
                    Assert.True(applied);
                    return Task.FromResult(true);
                }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(nextActivation.IsCompleted);
                block();
                Assert.False(nextActivation.IsCompleted);
            }
            lifetime.Register(routine, 42, new("", "", "", "", AudioRestoration: new([change], Logger.Instance)),
                cancellationToken: TestContext.Current.CancellationToken);
            await lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(_ => true)), (_, restore) =>
            {
                Assert.True(dispatcher.CheckAccess());
                Assert.True(restore);
                Assert.True(disposed);
                applied = true;
                return Task.CompletedTask;
            });
            Assert.NotNull(nextActivation);
            Assert.True(await nextActivation);
            Assert.Empty(lifetime.Sessions);
        });

    private static Task WithBlockedAudioAsync(Func<Action, Dispatcher, Task> execute) =>
        SharedStaDispatcherHost.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> responsive = Task.Run(async () =>
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input).Task
                        .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                    return true;
                }
                catch (TimeoutException) { return false; }
                finally { release.Set(); }
            }, TestContext.Current.CancellationToken);
            void Block()
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            try { await execute(Block, dispatcher); }
            finally { release.Set(); await responsive; }
            Assert.True(await responsive, "Restoration blocked the UI dispatcher while waiting for audio work.");
        });

    private static AudioRoutine CreateRoutine() => new()
    {
        Id = "routine",
        Enabled = true,
        TriggerKind = RoutineTriggerKind.Application,
        TriggerAppPath = "player.exe",
        OutputDeviceId = "new",
        RestorePreviousAudioOnDeactivate = true,
    };

    private sealed class CallbackChange(Action restore, Action dispose) : IRoutineAudioChange
    {
        public void Restore() => restore();
        public void Dispose() => dispose();
    }
}
