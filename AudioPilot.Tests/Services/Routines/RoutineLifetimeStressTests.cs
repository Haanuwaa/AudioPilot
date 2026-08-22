using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Routines;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class RoutineLifetimeStressTests
{
    [StressFact]
    public async Task MultipleTriggers_ConcurrentActivationAndTeardownKeepOneAudioOwner()
    {
        var lifetime = new RoutineLifetimeService();
        var routine = new AudioRoutine
        {
            Id = "multi",
            MasterVolumePercent = 45,
            Triggers = [new RoutineTrigger { Id = "first", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Primary.exe" }, .. Enumerable.Range(1, 7).Select(index => new RoutineTrigger
                { Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), Kind = RoutineTriggerKind.Application, AppPath = $@"C:\Apps\Player{index}.exe" })],
            RestorePreviousAudioOnDeactivate = true
        };
        AudioRoutine[] triggers = [.. routine.ExpandAutomaticTriggers()];
        int writes = 0;
        int restores = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        for (int cycle = 0; cycle < 200; cycle++)
        {
            await Task.WhenAll(triggers.Select((trigger, index) => lifetime.RunActivationAsync(trigger, index + 10, async token =>
            {
                if (lifetime.TryJoinActiveRoutine(trigger, index + 10, null, true, token)) return true;
                await Task.Yield();
                writes++;
                lifetime.Register(trigger, index + 10, null, cancellationToken: token);
                return true;
            }, cancellationToken: deadline.Token)));
            Assert.Equal(cycle + 1, writes);
            Assert.Equal(8, lifetime.Sessions.Count);
            var endings = lifetime.CaptureDeactivations(static _ => true);
            await Task.WhenAll(endings.Reverse().Select(ending => lifetime.DeactivateAsync(ending, async (_, restore) =>
            {
                await Task.Yield();
                if (restore) restores++;
            }))).WaitAsync(deadline.Token);
            Assert.Equal(cycle + 1, restores);
            Assert.Empty(lifetime.Sessions);
        }
        lifetime.Stop();
        await lifetime.DrainAsync().WaitAsync(deadline.Token);
    }

    [StressFact]
    public async Task OverlappingLifetimes_ReleaseClaimsAndLeases_WithoutStaleAudioRestoration()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        using var monitor = new FakeProcessLifecycleMonitor();
        var lifetime = new RoutineLifetimeService();
        int stops = 0;
        int owner = -1;
        int restorations = 0;
        try
        {
            for (int cycle = 0; cycle < 120; cycle++)
            {
                lifetime.AttachProcessMonitor(monitor, _ => { }, _ => stops++);
                var routine = new AudioRoutine
                {
                    Id = "routine",
                    Enabled = true,
                    TriggerKind = RoutineTriggerKind.Application,
                    TriggerAppPath = "app.exe",
                    SwitchOutputPerApp = true,
                    OutputDeviceId = "speakers",
                    RestorePreviousAudioOnDeactivate = true,
                };
                var process = new RoutineProcessSnapshot(42, "app.exe", StartTimeUtcTicks: cycle + 1);
                lifetime.Synchronize([routine], []);
                Assert.True(lifetime.TryClaim(routine, process, out var claim, out _));
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<bool> old = lifetime.RunActivationAsync(routine, 42, async cancellation =>
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                    cancellation.ThrowIfCancellationRequested();
                    owner = -100;
                    return true;
                }, cancellationToken: token);
                await entered.Task.WaitAsync(token);
                Task<bool> staleRouting = lifetime.ApplyRoutingAsync(() => false,
                    () => { owner = -200; return Task.FromResult(true); });
                AudioRoutine replacement = routine.Clone();
                if (cycle % 3 == 0) monitor.FireProcessStopped(42);
                else if (cycle % 3 == 1)
                {
                    replacement.OutputDeviceStableId = $"stable-{cycle}";
                    lifetime.Synchronize([replacement], []);
                }
                else lifetime.CancelActivation(routine, 42);
                Task<bool> next = lifetime.RunActivationAsync(replacement, 42, cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    owner = cycle;
                    lifetime.Register(replacement, 42, null, process, cancellationToken: cancellation);
                    lifetime.RegisterLease(replacement, 42, true, false, false, process, cancellation);
                    return Task.FromResult(true);
                }, cancellationToken: token);
                Assert.False(next.IsCompleted);
                release.SetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(token));
                Assert.False(await staleRouting.WaitAsync(token));
                Assert.True(await next.WaitAsync(token));
                lifetime.ReleaseClaim(claim!);
                RoutineLifetimeService.ActivationEnd ending = lifetime.CancelActivation(replacement, 42);
                RoutineAppOutputLease lease = Assert.IsType<RoutineAppOutputLease>(ending.Lease);
                RoutineLifetimeService.Deactivation stale = Assert.Single(ending.Sessions);
                await lifetime.RunActivationAsync(replacement, 42, cancellation =>
                {
                    lifetime.Register(replacement, 42, null, process, cancellationToken: cancellation);
                    lifetime.RegisterLease(replacement, 42, true, false, false, process, cancellation);
                    return Task.FromResult(true);
                }, cancellationToken: token);
                Assert.Null(lifetime.RemoveLease(lease.LeaseKey, lease.Generation));
                await lifetime.DeactivateAsync(stale, (_, _) => throw new InvalidOperationException("Stale restoration ran.")).WaitAsync(token);
                Assert.Equal(cycle, owner);
                RoutineLifetimeService.ActivationEnd current = lifetime.CancelActivation(replacement, 42);
                Assert.NotNull(lifetime.RemoveLease(current.Lease!.LeaseKey, current.Lease.Generation));
                var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task restoration = lifetime.DeactivateAsync(Assert.Single(current.Sessions), async (_, restore) =>
                {
                    Assert.True(restore);
                    await restoring.Task.WaitAsync(token);
                    owner = -1;
                    restorations++;
                });
                Task<bool> afterRestore = lifetime.RunActivationAsync(replacement, 42,
                    _ => { Assert.Equal(-1, owner); return Task.FromResult(true); }, cancellationToken: token);
                Assert.False(afterRestore.IsCompleted);
                restoring.SetResult();
                await restoration.WaitAsync(token);
                Assert.True(await afterRestore.WaitAsync(token));
                Assert.Empty(lifetime.Sessions);
                Assert.Empty(lifetime.GetLeases());
                Assert.Equal((0, 0), lifetime.PendingLeaseCounts);
                Assert.True(lifetime.TryClaim(replacement, process, out var finalClaim, out _));
                lifetime.ReleaseClaim(finalClaim!);
            }
            Assert.Equal(120, restorations);
            Assert.Equal(40, stops);
        }
        finally
        {
            lifetime.Stop();
            await deadline.CancelAsync();
            await lifetime.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        monitor.FireProcessStopped(42);
        Assert.Equal(40, stops);
    }
}
