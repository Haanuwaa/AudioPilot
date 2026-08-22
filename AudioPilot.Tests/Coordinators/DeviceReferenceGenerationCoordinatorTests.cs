using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class DeviceReferenceGenerationCoordinatorTests
{
    [Fact]
    public async Task Requests_QueueWorkAndSkipSupersededRequestsBeforeReadingInventory()
    {
        using var gate = new SemaphoreSlim(1, 1);
        List<Func<CancellationToken, Task>> queued = [];
        int reads = 0;
        List<CycleDevice> fresh = [new() { Id = "fresh", Name = "Fresh endpoint", StableId = " Opaque " }];
        List<CycleDevice>? exported = null;
        var coordinator = new DeviceReferenceGenerationCoordinator(queued.Add,
            _ => { reads++; return Task.FromResult((fresh, new List<CycleDevice>())); },
            () => DeviceReferenceFileMode.Plaintext, (outputs, _, _) => exported = outputs, gate, () => false);

        coordinator.Request();
        coordinator.Request();
        Assert.Equal(0, reads);
        Assert.Null(exported);
        await queued[0](TestContext.Current.CancellationToken);
        Assert.Equal(0, reads);
        await queued[1](TestContext.Current.CancellationToken);
        Assert.Equal(1, reads);
        Assert.Same(fresh, exported);
    }

    [Theory]
    [InlineData(DeviceReferenceFileMode.Off)]
    [InlineData(DeviceReferenceFileMode.Hashed)]
    [InlineData(DeviceReferenceFileMode.Plaintext)]
    public async Task ModeChange_DiscardsOldInventoryAndOnlyWritesLatestRequest(DeviceReferenceFileMode nextMode)
    {
        using var gate = new SemaphoreSlim(1, 1);
        List<Func<CancellationToken, Task>> queued = [];
        var pending = new TaskCompletionSource<(List<CycleDevice>, List<CycleDevice>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        DeviceReferenceFileMode mode = DeviceReferenceFileMode.Plaintext;
        int reads = 0;
        List<(string Id, bool Hashed)> writes = [];
        var coordinator = new DeviceReferenceGenerationCoordinator(queued.Add,
            _ => ++reads == 1 ? pending.Task : Task.FromResult((new List<CycleDevice> { new() { Id = "fresh" } }, new List<CycleDevice>())),
            () => mode, (outputs, _, hashed) => writes.Add((outputs[0].Id, hashed)), gate, () => false);

        coordinator.Request();
        Task first = queued[0](TestContext.Current.CancellationToken);
        mode = nextMode;
        coordinator.Request();
        pending.SetResult(([new() { Id = "old" }], []));
        await first;
        Assert.Empty(writes);
        if (nextMode == DeviceReferenceFileMode.Off) Assert.Single(queued);
        else
        {
            await queued[1](TestContext.Current.CancellationToken);
            Assert.Equal(("fresh", nextMode == DeviceReferenceFileMode.Hashed), Assert.Single(writes));
        }
    }

    [Theory]
    [InlineData("hashed")]
    [InlineData("off")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public async Task CommitWait_RechecksModeAndLifetimeAfterSettingsWriteGate(string change)
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Func<CancellationToken, Task>? queued = null;
        DeviceReferenceFileMode mode = DeviceReferenceFileMode.Plaintext;
        bool stopping = false;
        List<bool> writes = [];
        var coordinator = new DeviceReferenceGenerationCoordinator(work => queued = work,
            _ => Task.FromResult((new List<CycleDevice>(), new List<CycleDevice>())),
            () => mode, (_, _, hashed) => writes.Add(hashed), gate, () => stopping);
        coordinator.Request();
        Task work = queued!(lifetime.Token);
        Assert.False(work.IsCompleted);
        if (change == "cancel") lifetime.Cancel();
        else if (change == "stop") stopping = true;
        else mode = change == "off" ? DeviceReferenceFileMode.Off : DeviceReferenceFileMode.Hashed;
        gate.Release();
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await work;
        if (change == "hashed") Assert.True(Assert.Single(writes));
        else Assert.Empty(writes);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task InventoryFailure_DoesNotWriteAndNextRequestCanRecover()
    {
        using var gate = new SemaphoreSlim(1, 1);
        Func<CancellationToken, Task>? queued = null;
        bool fail = true;
        int writes = 0;
        var coordinator = new DeviceReferenceGenerationCoordinator(work => queued = work,
            _ => fail ? Task.FromException<(List<CycleDevice>, List<CycleDevice>)>(new IOException("Inventory unavailable"))
                : Task.FromResult((new List<CycleDevice>(), new List<CycleDevice>())),
            () => DeviceReferenceFileMode.Hashed, (_, _, _) => writes++, gate, () => false);
        coordinator.Request();
        await Assert.ThrowsAsync<IOException>(() => queued!(TestContext.Current.CancellationToken));
        Assert.Equal(0, writes);
        fail = false;
        coordinator.Request();
        await queued!(TestContext.Current.CancellationToken);
        Assert.Equal(1, writes);
        Assert.Equal(1, gate.CurrentCount);
    }
}
