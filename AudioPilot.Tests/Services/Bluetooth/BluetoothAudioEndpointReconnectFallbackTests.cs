namespace AudioPilot.Tests.Services.Bluetooth;

public sealed class BluetoothAudioEndpointReconnectFallbackTests
{
    [Fact]
    public void TryResolveVisibleEndpointMatch_PrefersPreferredActiveId()
    {
        List<BluetoothAudioEndpointReconnectFallback.VisibleEndpoint> activeEndpoints =
        [
            new("endpoint-1", "Other Device"),
            new("endpoint-2", "Bluetooth Headset Stereo"),
        ];

        bool matched = BluetoothAudioEndpointReconnectFallback.TryResolveVisibleEndpointMatch(
            activeEndpoints,
            "Bluetooth Headset",
            BluetoothReconnectService.NormalizeForMatch("Bluetooth Headset"),
            preferredEndpointId: "endpoint-2",
            out string matchReason);

        Assert.True(matched);
        Assert.Equal("active-id", matchReason);
    }

    [Fact]
    public void TryResolveVisibleEndpointMatch_FallsBackToUniqueNameMatch_WhenPreferredIdIsMissing()
    {
        List<BluetoothAudioEndpointReconnectFallback.VisibleEndpoint> activeEndpoints =
        [
            new("endpoint-1", "Laptop Speakers"),
            new("endpoint-3", "WH-1000XM4 Stereo"),
        ];

        bool matched = BluetoothAudioEndpointReconnectFallback.TryResolveVisibleEndpointMatch(
            activeEndpoints,
            "WH-1000XM4 Hands-Free AG Audio",
            BluetoothReconnectService.NormalizeForMatch("WH-1000XM4 Hands-Free AG Audio"),
            preferredEndpointId: "missing-endpoint",
            out string matchReason);

        Assert.True(matched);
        Assert.Equal("active-normalized-equal", matchReason);
    }

    [Fact]
    public void TryResolveVisibleEndpointMatch_PicksHighestPriorityNameMatch_WhenNameMatchesAreAmbiguous()
    {
        List<BluetoothAudioEndpointReconnectFallback.VisibleEndpoint> activeEndpoints =
        [
            new("endpoint-1", "Galaxy Buds2 Stereo"),
            new("endpoint-2", "Galaxy Buds2 Hands-Free AG Audio"),
        ];

        bool matched = BluetoothAudioEndpointReconnectFallback.TryResolveVisibleEndpointMatch(
            activeEndpoints,
            "Galaxy Buds2",
            BluetoothReconnectService.NormalizeForMatch("Galaxy Buds2"),
            preferredEndpointId: null,
            out string matchReason);

        Assert.True(matched);
        Assert.Equal("active-contains", matchReason);
    }
}
