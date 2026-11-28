using System.Runtime.InteropServices;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Bluetooth;

public sealed class BluetoothAssociationEndpointSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_WhenAlreadyCancelled_DoesNotEnumerate(bool byId)
    {
        int enumerations = 0;
        var source = new BluetoothAssociationEndpointSource(Logger.Instance, (_, _) =>
        {
            enumerations++;
            return Task.FromResult<IReadOnlyList<BluetoothAssociationEndpointCandidate>>([]);
        }, preferWatcherCache: false, readAssociationEndpointByIdAsync: (_, _) =>
        {
            enumerations++;
            return Task.FromResult<BluetoothAssociationEndpointCandidate?>(null);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (byId) await source.TryGetAssociationEndpointByIdAsync("endpoint", "test", "output", new CancellationToken(true));
            else await source.GetAssociationEndpointsAsync("test", "output", new CancellationToken(true));
        });
        Assert.Equal(0, enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_WhenEnumerationCompletesAfterCancellation_DiscardsResult(bool byId)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new BluetoothAssociationEndpointSource(Logger.Instance, (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<BluetoothAssociationEndpointCandidate>>([CreateCandidate("endpoint", "Fixture Headset")]);
        }, preferWatcherCache: false, readAssociationEndpointByIdAsync: (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<BluetoothAssociationEndpointCandidate?>(CreateCandidate("endpoint", "Fixture Headset"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (byId) await source.TryGetAssociationEndpointByIdAsync("endpoint", "test", "output", cancellation.Token);
            else await source.GetAssociationEndpointsAsync("test", "output", cancellation.Token);
        });
    }

    [Fact]
    public async Task LookupById_RejectsUnrelatedIdWithoutEnumerating()
    {
        var source = new BluetoothAssociationEndpointSource(Logger.Instance,
            (_, _) => throw new InvalidOperationException("Exact lookup must not enumerate."), preferWatcherCache: false,
            readAssociationEndpointByIdAsync: (id, _) => Task.FromResult<BluetoothAssociationEndpointCandidate?>(
                CreateCandidate(id == "endpoint" ? "ENDPOINT" : "unrelated", "Fixture Headset")));
        CancellationToken token = TestContext.Current.CancellationToken;

        Assert.Equal("ENDPOINT", (await source.TryGetAssociationEndpointByIdAsync("endpoint", "test", "output", token))?.Id);
        Assert.Null(await source.TryGetAssociationEndpointByIdAsync("stale-endpoint", "test", "output", token));
    }

    [Fact]
    public void WatcherCache_RejectsIncompleteInventoryAndClearsItOnDisposal()
    {
        var cache = new BluetoothAssociationEndpointSource.AssociationEndpointWatcherCache();
        var devices = TestPrivateAccess.GetField<Dictionary<string, BluetoothAssociationEndpointSource.AssociationEndpointDeviceSnapshot>>(cache, "_devices");
        devices["endpoint"] = new("endpoint", "Fixture Headset", true, true, null);
        Assert.False(cache.TryGetById("endpoint", out _));
        Assert.False(cache.TryGetSnapshot(out _));

        TestPrivateAccess.SetField(cache, "_enumerationCompleted", true);
        Assert.True(cache.TryGetById("endpoint", out var candidate));
        Assert.True(candidate!.IsConnected);
        Assert.True(cache.TryGetSnapshot(out var snapshot));
        Assert.Single(snapshot);

        cache.Dispose(Logger.Instance);
        Assert.Empty(devices);
        Assert.False(cache.TryGetById("endpoint", out _));
        Assert.False(cache.TryGetSnapshot(out _));
    }

    [Fact]
    public void WatcherCache_StaleIdleTimeoutCallbackIsIgnoredBeforeDeadline()
    {
        using var clock = new ManualTimeProvider();
        var cache = new BluetoothAssociationEndpointSource.AssociationEndpointWatcherCache(clock, TimeSpan.FromMinutes(2));
        var devices = TestPrivateAccess.GetField<Dictionary<string, BluetoothAssociationEndpointSource.AssociationEndpointDeviceSnapshot>>(cache, "_devices");
        devices["endpoint"] = new("endpoint", "Fixture Headset", true, true, null);
        TestPrivateAccess.SetField(cache, "_enumerationCompleted", true);
        TestPrivateAccess.SetField(cache, "_lastActivityTimestamp", clock.GetTimestamp());

        clock.Advance(TimeSpan.FromMinutes(1));
        TestPrivateAccess.InvokeNonPublic(cache, "OnIdleTimeout", [null]);
        Assert.True(cache.TryGetById("endpoint", out _));
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(100, 150)]
    [InlineData(249, 1)]
    [InlineData(250, 0)]
    [InlineData(300, 0)]
    public void ResolveMinimalPropertiesRetryBudgetMs_ReturnsExpectedRemainingBudget(long elapsedMs, int expected)
    {
        int actual = BluetoothAssociationEndpointSource.ResolveMinimalPropertiesRetryBudgetMs(elapsedMs);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task GetAssociationEndpointsAsync_RetriesWithMinimalProperties_WhenPropertyKeySyntaxFails()
    {
        List<bool> modes = [];
        var source = new BluetoothAssociationEndpointSource(
            Logger.Instance,
            (useMinimalProperties, cancellationToken) =>
            {
                modes.Add(useMinimalProperties);
                if (!useMinimalProperties)
                {
                    throw new COMException("Invalid property key", unchecked((int)0x8002802B));
                }

                IReadOnlyList<BluetoothAssociationEndpointCandidate> candidates =
                [
                    CreateCandidate("endpoint-id", "WH-1000XM5 Stereo"),
                ];
                return Task.FromResult(candidates);
            },
            preferWatcherCache: false);

        IReadOnlyList<BluetoothAssociationEndpointCandidate> endpoints = await source.GetAssociationEndpointsAsync(
            opId: "op-test",
            kind: "output",
            CancellationToken.None);

        Assert.Equal([false, true], modes);
        BluetoothAssociationEndpointCandidate endpoint = Assert.Single(endpoints);
        Assert.Equal("endpoint-id", endpoint.Id);
    }

    [Fact]
    public async Task GetAssociationEndpointsAsync_ReturnsEmpty_WhenMinimalPropertiesRetryTimesOut()
    {
        List<bool> modes = [];
        var source = new BluetoothAssociationEndpointSource(
            Logger.Instance,
            async (useMinimalProperties, cancellationToken) =>
            {
                modes.Add(useMinimalProperties);
                if (!useMinimalProperties)
                {
                    throw new COMException("Invalid property key", unchecked((int)0x8002802B));
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            },
            preferWatcherCache: false);

        IReadOnlyList<BluetoothAssociationEndpointCandidate> endpoints = await source.GetAssociationEndpointsAsync(
            opId: "op-timeout",
            kind: "output",
            CancellationToken.None);

        Assert.Empty(endpoints);
        Assert.Equal([false, true], modes);
    }

    private static BluetoothAssociationEndpointCandidate CreateCandidate(string id, string name)
    {
        return new BluetoothAssociationEndpointCandidate(
            id,
            name,
            IsPaired: true,
            IsConnected: false,
            TryPairAsync: static _ => Task.FromResult(new BluetoothAssociationEndpointPairAttempt(false, "NotAttempted")));
    }
}
