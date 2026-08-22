using System.Collections.Concurrent;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

public sealed class VolumeCacheStoreTests
{
    private long _utcNowTicks = TimeSpan.TicksPerSecond;

    [Theory]
    [InlineData("discord")]
    [InlineData("discord.exe")]
    public void ApplicationLookup_AfterDisplayNameChanges_UsesLatestProcessVolume(string processName)
    {
        var store = CreateStore(new ConcurrentDictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromMinutes(5));
        store.UpdateCache("Discord", processName, 35f);
        store.UpdateCache("Discord Voice Chat", processName, 45f);

        Assert.Equal(45f, store.TryGetApplicationVolume("discord", null, out _));
        Assert.Equal(45f, store.TryGetApplicationVolume(processName, null, out _));

        store.UpdateCache("Discord", processName, 55f);
        Assert.Equal(55f, store.TryGetApplicationVolume("discord", null, out _));
        Assert.Equal(55f, store.TryGetApplicationVolume(processName, null, out _));
    }

    [Fact]
    public void ApplicationLookup_WithoutDisplayNameUpdatesSameProcess()
    {
        var store = CreateStore(new ConcurrentDictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromMinutes(5));
        store.UpdateCache("Discord Voice Chat", "discord", 45f);
        store.UpdateCache(string.Empty, "discord", 55f);

        Assert.Equal(55f, store.TryGetApplicationVolume("discord", null, out _));
    }

    [Fact]
    public void ApplicationLookup_WithExecutableExtensionMatchesSameProcess()
    {
        var normalizedNameCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = CreateStore(normalizedNameCache, TimeSpan.FromMinutes(5));

        store.UpdateCache("Discord", "discord.exe", 35f);

        float? volume = store.TryGetApplicationVolume("discord", null, out _);

        Assert.Equal(35f, volume);
    }

    [Fact]
    public void ApplicationLookup_WithoutExecutableExtensionMatchesSameProcess()
    {
        var normalizedNameCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = CreateStore(normalizedNameCache, TimeSpan.FromMinutes(5));

        store.UpdateCache("Discord Voice Chat", "discord.exe", 45f);

        float? volume = store.TryGetApplicationVolume("discord", null, out _);

        Assert.Equal(45f, volume);
    }

    [Fact]
    public void CleanupExpiredEntries_RemovesExpiredVolumes()
    {
        var normalizedNameCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = CreateStore(normalizedNameCache, TimeSpan.Zero);

        store.UpdateCache("Discord Voice Chat", "discord.exe", 45f);
        _utcNowTicks += TimeSpan.TicksPerMillisecond * 10;

        int cleanup = store.CleanupExpiredEntries();

        Assert.True(cleanup > 0);
        Assert.Null(store.TryGetApplicationVolume("discord", "Discord Voice Chat", out _));
        Assert.Null(store.TryGetApplicationVolume("discord", null, out _));
    }

    [Fact]
    public void ApplicationLookup_KeepsProcessesWithTheSameTitleIndependent()
    {
        var store = CreateStore(new(StringComparer.Ordinal), TimeSpan.FromMinutes(5));
        store.UpdateCache("Shared title", "first.exe", 25f);
        store.UpdateCache("Shared title", "second.exe", 75f);
        Assert.Equal(25f, store.TryGetApplicationVolume("first", "Shared title", out _));
        Assert.Equal(75f, store.TryGetApplicationVolume("second", "Shared title", out _));
        Assert.Null(store.TryGetApplicationVolume("third", "Shared title", out _));
        store.UpdateCache("New title", "first", 45f);
        Assert.Equal(45f, store.TryGetApplicationVolume("first", "Shared title", out _));
    }

    [Fact]
    public void ApplicationLookup_DoesNotMergeExecutableSuffixesOrPunctuation()
    {
        var store = CreateStore(new(StringComparer.Ordinal), TimeSpan.FromMinutes(5));
        store.UpdateCache("Player", "player-beta.exe", 25f);
        store.UpdateCache("Player", "player.exe", 75f);
        store.UpdateCache("Player", "player.test", 45f);
        Assert.Equal(25f, store.TryGetApplicationVolume("player-beta", "Player", out _));
        Assert.Equal(75f, store.TryGetApplicationVolume("player", "Player", out _));
        Assert.Null(store.TryGetApplicationVolume("playertest", "Player", out _));
    }

    [Fact]
    public async Task ConcurrentCacheUpdates_OlderWriteCannotReplaceNewerChoice()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var store = new VolumeCacheStore(Logger.Instance, new(), static name => name,
            TimeSpan.FromMinutes(5), 64, 128, () =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call != 1) return call * 100L;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Older cache write was not released.");
                return 100L;
            });
        Task olderWrite = Task.Run(() => store.UpdateCache("Player", "player", 25f), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            store.UpdateCache("Player", "player", 75f);
        }
        finally { release.Set(); }
        await olderWrite;
        Assert.Equal(75f, store.TryGetApplicationVolume("player", "Player", out _));
    }

    private VolumeCacheStore CreateStore(ConcurrentDictionary<string, string> normalizedNameCache, TimeSpan ttl)
    {
        return new VolumeCacheStore(
            Logger.Instance,
            normalizedNameCache,
            static name => name.Trim().ToLowerInvariant(),
            ttl,
            maxVolumeCacheEntries: 64,
            maxNormalizedNameCacheEntries: 128,
            timestampTicksProvider: () => _utcNowTicks);
    }
}
