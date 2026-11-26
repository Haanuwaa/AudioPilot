using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppCliSettingsMutationCoordinatorTests
{
    [Fact]
    public async Task CancellationDuringPersistenceStillPublishesTheKnownCommittedOutcome()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var current = new Settings();
        Settings? committed = null;
        Settings? published = null;
        var result = await AppCliSettingsMutationCoordinator.ExecuteAsync(() => current, () => Task.FromResult(current),
            candidate => { candidate.App.PlayAppSounds = false; return new CliSettingsMutationDecision<bool>(true, true); },
            candidate => { committed = candidate; cancellation.Cancel(); return Task.CompletedTask; },
            candidate => published = candidate, gate, cancellationToken: cancellation.Token);
        Assert.True(result.Result);
        Assert.Same(committed, published);
        Assert.Same(committed, result.PersistedSettings);
        Assert.False(published!.App.PlayAppSounds);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsAndPublishesAClone()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var current = new Settings { App = new() { Theme = AppTheme.Light } };
        Settings? persisted = null;
        Settings? published = null;

        CliSettingsMutationOutcome<string> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                candidate.App.Theme = AppTheme.Dark;
                return new CliSettingsMutationDecision<string>("updated", Persist: true);
            },
            candidate =>
            {
                persisted = candidate;
                return Task.CompletedTask;
            },
            candidate => published = candidate,
            semaphore,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppTheme.Light, current.App.Theme);
        Assert.NotSame(current, persisted);
        Assert.Same(persisted, published);
        Assert.Same(published, outcome.PersistedSettings);
        Assert.Equal(AppTheme.Dark, outcome.PersistedSettings!.App.Theme);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceFails_DoesNotPublishCandidate()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var current = new Settings { App = new() { Theme = AppTheme.Light } };
        Settings? published = null;

        await Assert.ThrowsAsync<IOException>(() => AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                candidate.App.Theme = AppTheme.Dark;
                return new CliSettingsMutationDecision<bool>(true, Persist: true);
            },
            _ => throw new IOException("disk failure"),
            candidate => published = candidate,
            semaphore,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(AppTheme.Light, current.App.Theme);
        Assert.Null(published);
    }

    [Fact]
    public async Task ExecuteAsync_PublishesBeforeTheNextQueuedMutationReadsCurrentSettings()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var current = new Settings { App = new() { Theme = AppTheme.Light } };
        var firstPersistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CliSettingsMutationOutcome<bool>> first = AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                candidate.App.Theme = AppTheme.Dark;
                return new CliSettingsMutationDecision<bool>(true, Persist: true);
            },
            async _ =>
            {
                firstPersistEntered.SetResult();
                await releaseFirstPersist.Task;
            },
            candidate => current = candidate,
            semaphore,
            cancellationToken: TestContext.Current.CancellationToken);

        await firstPersistEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        bool secondSawFirstMutation = false;
        Task<CliSettingsMutationOutcome<bool>> second = AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                secondSawFirstMutation = candidate.App.Theme == AppTheme.Dark;
                candidate.Startup.Enabled = true;
                return new CliSettingsMutationDecision<bool>(true, Persist: true);
            },
            _ => Task.CompletedTask,
            candidate => current = candidate,
            semaphore,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);
        releaseFirstPersist.SetResult();
        await Task.WhenAll(first, second);

        Assert.True(secondSawFirstMutation);
        Assert.Equal(AppTheme.Dark, current.App.Theme);
        Assert.True(current.Startup.Enabled);
    }
}
