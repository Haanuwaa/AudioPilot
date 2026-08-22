using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppCliSettingsMutationCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_PersistsAndPublishesAClone()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var current = new Settings { Theme = AppTheme.Light };
        Settings? persisted = null;
        Settings? published = null;

        CliSettingsMutationOutcome<string> outcome = await AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                candidate.Theme = AppTheme.Dark;
                return new CliSettingsMutationDecision<string>("updated", Persist: true);
            },
            candidate =>
            {
                persisted = candidate;
                return Task.CompletedTask;
            },
            candidate => published = candidate,
            semaphore,
            TestContext.Current.CancellationToken);

        Assert.Equal(AppTheme.Light, current.Theme);
        Assert.NotSame(current, persisted);
        Assert.Same(persisted, published);
        Assert.Same(published, outcome.PersistedSettings);
        Assert.Equal(AppTheme.Dark, outcome.PersistedSettings!.Theme);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceFails_DoesNotPublishCandidate()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var current = new Settings { Theme = AppTheme.Light };
        Settings? published = null;

        await Assert.ThrowsAsync<IOException>(() => AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                candidate.Theme = AppTheme.Dark;
                return new CliSettingsMutationDecision<bool>(true, Persist: true);
            },
            _ => throw new IOException("disk failure"),
            candidate => published = candidate,
            semaphore,
            TestContext.Current.CancellationToken));

        Assert.Equal(AppTheme.Light, current.Theme);
        Assert.Null(published);
    }

    [Fact]
    public async Task ExecuteAsync_PublishesBeforeTheNextQueuedMutationReadsCurrentSettings()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var current = new Settings { Theme = AppTheme.Light };
        var firstPersistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CliSettingsMutationOutcome<bool>> first = AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                candidate.Theme = AppTheme.Dark;
                return new CliSettingsMutationDecision<bool>(true, Persist: true);
            },
            async _ =>
            {
                firstPersistEntered.SetResult();
                await releaseFirstPersist.Task;
            },
            candidate => current = candidate,
            semaphore,
            TestContext.Current.CancellationToken);

        await firstPersistEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        bool secondSawFirstMutation = false;
        Task<CliSettingsMutationOutcome<bool>> second = AppCliSettingsMutationCoordinator.ExecuteAsync(
            () => current,
            () => Task.FromResult(current),
            candidate =>
            {
                secondSawFirstMutation = candidate.Theme == AppTheme.Dark;
                candidate.RunAtStartup = true;
                return new CliSettingsMutationDecision<bool>(true, Persist: true);
            },
            _ => Task.CompletedTask,
            candidate => current = candidate,
            semaphore,
            TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);
        releaseFirstPersist.SetResult();
        await Task.WhenAll(first, second);

        Assert.True(secondSawFirstMutation);
        Assert.Equal(AppTheme.Dark, current.Theme);
        Assert.True(current.RunAtStartup);
    }
}
