using System.Reflection;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionProcessCacheCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionMetadata_FallsBackWhenOptionalNameReadFailsAndCachesResult(bool failNameRead)
    {
        Type contract = typeof(AudioSessionControl).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IAudioSessionControl", throwOnError: true)!;
        var proxy = (SessionNameProxy)DispatchProxy.Create(contract, typeof(SessionNameProxy));
        proxy.FailNameRead = failNameRead;
        using var session = (AudioSessionControl)Activator.CreateInstance(typeof(AudioSessionControl),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [proxy], null)!;
        using var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(1));
        var windows = new AudioSessionService.DeferredWindowPidMap(() => new Dictionary<IntPtr, uint>());

        Assert.True(coordinator.TryGetSessionProcessMetadata((uint)Environment.ProcessId, session, windows, out var metadata));
        Assert.False(string.IsNullOrWhiteSpace(metadata.ProcessName));
        Assert.False(string.IsNullOrWhiteSpace(metadata.DisplayName));
        if (!failNameRead) Assert.Equal("Friendly Player", metadata.DisplayName);
        Assert.True(coordinator.TryGetSessionProcessMetadata((uint)Environment.ProcessId, session, windows, out var cached));
        Assert.Equal(metadata, cached);
        Assert.Equal(1, proxy.NameReads);
    }

    public class SessionNameProxy : DispatchProxy
    {
        public bool FailNameRead { get; set; }
        public int NameReads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "GetDisplayName")
            {
                NameReads++;
                if (FailNameRead) throw new COMException("Optional session name unavailable", unchecked((int)0x80004005));
                args![0] = "Friendly Player";
            }
            return 0;
        }
    }

    [Fact]
    public async Task StartCleanupTaskAsync_IsIdempotent()
    {
        using var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(10));

        await coordinator.StartCleanupTaskAsync(() => false);
        await coordinator.StartCleanupTaskAsync(() => false);

        Assert.True(coordinator.IsCleanupLoopStarted);
        Assert.NotNull(coordinator.CleanupTaskForTests);
    }

    [Fact]
    public async Task Dispose_CancelsAndDrainsCleanupTaskBeforeReturning()
    {
        var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(10));
        await coordinator.StartCleanupTaskAsync(() => false);
        Task cleanupTask = Assert.IsType<Task>(coordinator.CleanupTaskForTests, exactMatch: false);

        coordinator.Dispose();

        Assert.True(cleanupTask.IsCompleted);
        Assert.False(cleanupTask.IsFaulted);
        Assert.False(coordinator.IsCleanupLoopStarted);
    }

    [Fact]
    public async Task PauseCleanupTaskAsync_CancelsLoop_ClearsCache_AndAllowsLazyRestart()
    {
        using var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(10));
        coordinator.AddProcessCacheEntryForTests(42, "process", "Process", null, 1);
        await coordinator.StartCleanupTaskAsync(() => false);

        await coordinator.PauseCleanupTaskAsync();

        Assert.False(coordinator.IsCleanupLoopStarted);
        Assert.Null(coordinator.CleanupTaskForTests);
        Assert.Equal(0, coordinator.ProcessCacheCount);

        await coordinator.ResumeCleanupTaskAsync(() => false);

        Assert.True(coordinator.IsCleanupLoopStarted);
        Assert.NotNull(coordinator.CleanupTaskForTests);
    }

    [Fact]
    public async Task ResumeCleanupTaskAsync_DuringPauseDrain_RestartsAfterCanceledLoopFinishes()
    {
        using var coordinator = new AudioSessionProcessCacheCoordinator(Logger.Instance, TimeSpan.FromMinutes(10));
        await coordinator.StartCleanupTaskAsync(() => false);
        Task? firstCleanupTask = coordinator.CleanupTaskForTests;

        Task pauseTask = coordinator.PauseCleanupTaskAsync();
        await coordinator.ResumeCleanupTaskAsync(() => false);
        await pauseTask;

        Assert.True(coordinator.IsCleanupLoopStarted);
        Assert.NotSame(firstCleanupTask, coordinator.CleanupTaskForTests);
    }

    [Fact]
    public void TrimProcessCacheForTests_TrimsToConfiguredLimit()
    {
        long now = 10_000_000;
        using var coordinator = new AudioSessionProcessCacheCoordinator(
            Logger.Instance,
            TimeSpan.FromMinutes(10),
            () => now);

        for (int index = 0; index < AppConstants.Limits.MaxProcessCacheEntries + 5; index++)
        {
            coordinator.AddProcessCacheEntryForTests(
                (uint)index + 1,
                $"proc-{index}",
                $"Display {index}",
                null,
                now - TimeSpan.FromMinutes(index).Ticks);
        }

        coordinator.TrimProcessCacheForTests();

        Assert.True(coordinator.ProcessCacheCount <= AppConstants.Limits.MaxProcessCacheEntries);
    }

    [Fact]
    public void TryGetOrAddEntry_ExpiresAgainstMonotonicTimestamp()
    {
        long now = 1000;
        using var coordinator = new AudioSessionProcessCacheCoordinator(
            Logger.Instance,
            TimeSpan.FromSeconds(5),
            () => now);
        int factoryCalls = 0;

        Assert.True(coordinator.TryGetOrAddEntry(
            42,
            () =>
            {
                factoryCalls++;
                return AudioSessionProcessCacheCoordinator.CacheEntry.Create("first", "First", null);
            },
            out _));

        now += TimeSpan.FromMilliseconds(4_999).Ticks;
        Assert.True(coordinator.TryGetOrAddEntry(
            42,
            () =>
            {
                factoryCalls++;
                return AudioSessionProcessCacheCoordinator.CacheEntry.Create("unexpected", "Unexpected", null);
            },
            out AudioSessionProcessCacheCoordinator.CacheEntry cached));
        Assert.Equal("first", cached.ProcessName);

        now += TimeSpan.FromMilliseconds(2).Ticks;
        Assert.True(coordinator.TryGetOrAddEntry(
            42,
            () =>
            {
                factoryCalls++;
                return AudioSessionProcessCacheCoordinator.CacheEntry.Create("second", "Second", null);
            },
            out AudioSessionProcessCacheCoordinator.CacheEntry refreshed));

        Assert.Equal(2, factoryCalls);
        Assert.Equal("second", refreshed.ProcessName);
    }
}
