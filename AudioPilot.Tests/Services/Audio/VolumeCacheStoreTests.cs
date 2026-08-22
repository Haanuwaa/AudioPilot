using System.Collections.Concurrent;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

public sealed class VolumeCacheStoreTests
{
    private long _utcNowTicks = TimeSpan.TicksPerSecond;

    [Theory]
    [InlineData("discord")]
    [InlineData("discord.exe")]
    public void TryGetCachedVolume_AfterDisplayNameChanges_UsesLatestProcessVolume(string processName)
    {
        var store = CreateStore(new ConcurrentDictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromMinutes(5));
        store.UpdateCache("Discord", processName, 35f);
        store.UpdateCache("Discord Voice Chat", processName, 45f);

        Assert.Equal(45f, store.TryGetCachedVolume("discord"));
        Assert.Equal(45f, store.TryGetCachedVolume(processName));

        store.UpdateCache("Discord", processName, 55f);
        Assert.Equal(55f, store.TryGetCachedVolume("discord"));
        Assert.Equal(55f, store.TryGetCachedVolume(processName));
    }

    [Fact]
    public void TryGetCachedVolume_WhenProcessBecomesCanonicalAgain_RemovesPreviousAlias()
    {
        var store = CreateStore(new ConcurrentDictionary<string, string>(StringComparer.Ordinal), TimeSpan.FromMinutes(5));
        store.UpdateCache("Discord Voice Chat", "discord", 45f);
        store.UpdateCache(string.Empty, "discord", 55f);

        Assert.Equal(55f, store.TryGetCachedVolume("discord"));
    }

    [Fact]
    public void TryGetCachedVolume_ReturnsDisplayNameEntry()
    {
        var normalizedNameCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = CreateStore(normalizedNameCache, TimeSpan.FromMinutes(5));

        store.UpdateCache("Discord", "discord.exe", 35f);

        float? volume = store.TryGetCachedVolume("discord");

        Assert.Equal(35f, volume);
    }

    [Fact]
    public void TryGetCachedVolume_ReturnsAliasEntryForSanitizedProcessName()
    {
        var normalizedNameCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = CreateStore(normalizedNameCache, TimeSpan.FromMinutes(5));

        store.UpdateCache("Discord Voice Chat", "discord.exe", 45f);

        float? volume = store.TryGetCachedVolume("discord");

        Assert.Equal(45f, volume);
    }

    [Fact]
    public void CleanupExpiredEntries_RemovesExpiredVolumeAndAliasEntries()
    {
        var normalizedNameCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = CreateStore(normalizedNameCache, TimeSpan.Zero);

        store.UpdateCache("Discord Voice Chat", "discord.exe", 45f);
        _utcNowTicks += TimeSpan.TicksPerMillisecond * 10;

        VolumeCacheCleanupResult cleanup = store.CleanupExpiredEntries();

        Assert.True(cleanup.ExpiredVolumeEntries > 0);
        Assert.True(cleanup.ExpiredAliases > 0);
        Assert.Null(store.TryGetCachedVolume("discord voice chat"));
        Assert.Null(store.TryGetCachedVolume("discord"));
    }

    private VolumeCacheStore CreateStore(ConcurrentDictionary<string, string> normalizedNameCache, TimeSpan ttl)
    {
        return new VolumeCacheStore(
            Logger.Instance,
            normalizedNameCache,
            static name => name.Trim().ToLowerInvariant(),
            ttl,
            maxVolumeCacheEntries: 64,
            maxVolumeAliasEntries: 64,
            maxNormalizedNameCacheEntries: 128,
            timestampTicksProvider: () => _utcNowTicks);
    }
}
