namespace AudioPilot.Tests.Services.Bluetooth;

public sealed class RememberedBluetoothEndpointCacheTests
{
    [Fact]
    public void RememberEndpointId_PrunesExpiredEntriesOnWrite()
    {
        DateTime nowUtc = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new RememberedBluetoothEndpointCache(
            ttl: TimeSpan.FromMinutes(5),
            maxEntries: 8,
            utcNow: () => nowUtc);

        cache.RememberEndpointId("old", "endpoint-1");
        nowUtc = nowUtc.AddMinutes(6);

        cache.RememberEndpointId("new", "endpoint-2");

        Assert.False(cache.TryGetEndpointId("old", out _));
        Assert.True(cache.TryGetEndpointId("new", out string endpointId));
        Assert.Equal("endpoint-2", endpointId);
        Assert.Equal(1, cache.EntryCountForTests);
    }

    [Fact]
    public void RememberEndpointId_TrimsOldestEntriesWhenCapacityIsExceeded()
    {
        DateTime nowUtc = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new RememberedBluetoothEndpointCache(
            ttl: TimeSpan.FromHours(1),
            maxEntries: 2,
            utcNow: () => nowUtc);

        cache.RememberEndpointId("first", "endpoint-1");
        nowUtc = nowUtc.AddMinutes(1);
        cache.RememberEndpointId("second", "endpoint-2");
        nowUtc = nowUtc.AddMinutes(1);
        cache.RememberEndpointId("third", "endpoint-3");

        Assert.False(cache.TryGetEndpointId("first", out _));
        Assert.True(cache.TryGetEndpointId("second", out string secondEndpointId));
        Assert.True(cache.TryGetEndpointId("third", out string thirdEndpointId));
        Assert.Equal("endpoint-2", secondEndpointId);
        Assert.Equal("endpoint-3", thirdEndpointId);
        Assert.Equal(2, cache.EntryCountForTests);
    }
}
