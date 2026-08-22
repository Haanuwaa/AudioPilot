using System.Runtime.InteropServices;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Bluetooth;

public sealed class BluetoothAssociationEndpointSourceTests
{
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
