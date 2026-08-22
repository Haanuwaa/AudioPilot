using AudioPilot.Coordinators;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class AppSwitchCommandCoordinatorTests
{


    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveCycleTargetIndex_ReturnsMinusOne_WhenCycleCountIsNotPositive(int cycleCount)
    {
        int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(
            currentIndex: 0,
            cycleCount: cycleCount,
            reverse: false);

        Assert.Equal(-1, targetIndex);
    }


    [Fact]
    public void ResolveCycleTargetIndex_ReturnsFirst_WhenCurrentMissingAndForward()
    {
        int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(
            currentIndex: -1,
            cycleCount: 4,
            reverse: false);

        Assert.Equal(0, targetIndex);
    }


    [Fact]
    public void ResolveCycleTargetIndex_ReturnsLast_WhenCurrentMissingAndReverse()
    {
        int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(
            currentIndex: -1,
            cycleCount: 4,
            reverse: true);

        Assert.Equal(3, targetIndex);
    }


    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    public void ResolveCycleTargetIndex_MovesForwardAndWraps(int currentIndex, int expected)
    {
        int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(
            currentIndex: currentIndex,
            cycleCount: 4,
            reverse: false);

        Assert.Equal(expected, targetIndex);
    }


    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public void ResolveCycleTargetIndex_MovesBackwardAndWraps(int currentIndex, int expected)
    {
        int targetIndex = AppSwitchCommandCoordinator.ResolveCycleTargetIndex(
            currentIndex: currentIndex,
            cycleCount: 4,
            reverse: true);

        Assert.Equal(expected, targetIndex);
    }


    [Theory]
    [InlineData("id-a")]
    [InlineData("ID-A")]
    public void ContainsConfiguredDeviceId_ReturnsTrue_WhenConfiguredIdMatchesIgnoringCase(string currentDeviceId)
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        bool contains = AppSwitchCycleStateResolver.ContainsConfiguredDeviceId(configuredCycle, currentDeviceId);

        Assert.True(contains);
    }


    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("id-z")]
    public void ContainsConfiguredDeviceId_ReturnsFalse_WhenConfiguredIdIsMissing(string? currentDeviceId)
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "", Name = "Missing ID" },
            new CycleDevice { Id = "id-a", Name = "A" },
        ];

        bool contains = AppSwitchCycleStateResolver.ContainsConfiguredDeviceId(configuredCycle, currentDeviceId);

        Assert.False(contains);
    }

    [Fact]
    public void TryResolveDeferredSwitchTargetIndex_ReturnsFalse_WhenCurrentMissingFromConnectedCycle()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredSwitchTargetIndex(
            currentDeviceId: "id-z",
            connectedCycle,
            reverse: false,
            out int targetIndex);

        Assert.False(resolved);
        Assert.Equal(-1, targetIndex);
    }


    [Fact]
    public void TryResolveDeferredSwitchTargetIndex_ReturnsExpectedForwardTarget_WhenCurrentPresent()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
            new CycleDevice { Id = "id-c", Name = "C" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredSwitchTargetIndex(
            currentDeviceId: "id-b",
            connectedCycle,
            reverse: false,
            out int targetIndex);

        Assert.True(resolved);
        Assert.Equal(2, targetIndex);
    }


    [Fact]
    public void TryResolveDeferredSwitchTargetIndex_ReturnsExpectedReverseTarget_WhenCurrentPresent()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
            new CycleDevice { Id = "id-c", Name = "C" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredSwitchTargetIndex(
            currentDeviceId: "id-b",
            connectedCycle,
            reverse: true,
            out int targetIndex);

        Assert.True(resolved);
        Assert.Equal(0, targetIndex);
    }


    [Fact]
    public void TryResolveConfiguredDeviceByActiveName_ReturnsRemappedDevice_WhenUniqueMatchExists()
    {
        CycleDevice configured = new() { Id = "old-id", Name = "WH-1000XM4 Hands-Free AG Audio" };
        Dictionary<string, string> activeNamesById = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id-a"] = "Speakers (Realtek(R) Audio)",
            ["id-b"] = "WH-1000XM4 Stereo",
        };
        HashSet<string> reservedActiveIds = ["id-a"];

        bool resolved = AppSwitchCommandCoordinator.TryResolveConfiguredDeviceByActiveName(
            configured,
            activeNamesById,
            reservedActiveIds,
            out CycleDevice remappedDevice);

        Assert.True(resolved);
        Assert.Equal("id-b", remappedDevice.Id);
        Assert.Equal("WH-1000XM4 Stereo", remappedDevice.Name);
    }


    [Fact]
    public void TryResolveConfiguredDeviceByActiveName_ReturnsFalse_WhenBestMatchIsAmbiguous()
    {
        CycleDevice configured = new() { Id = "old-id", Name = "Galaxy Buds2" };
        Dictionary<string, string> activeNamesById = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id-a"] = "Galaxy Buds2 Stereo",
            ["id-b"] = "Galaxy Buds2 Hands-Free AG Audio",
        };
        HashSet<string> reservedActiveIds = [];

        bool resolved = AppSwitchCommandCoordinator.TryResolveConfiguredDeviceByActiveName(
            configured,
            activeNamesById,
            reservedActiveIds,
            out CycleDevice remappedDevice);

        Assert.False(resolved);
        Assert.Equal(string.Empty, remappedDevice.Id);
        Assert.Equal(string.Empty, remappedDevice.Name);
    }


    [Fact]
    public void TryBuildExactIdCycleState_ReturnsConnectedAndSkippedDevices_WhenIdsPresent()
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        bool built = AppSwitchCommandCoordinator.TryBuildExactIdCycleState(
            configuredCycle,
            configured => configured.Id == "id-a"
                ? new CycleDevice { Id = configured.Id, Name = "Resolved A" }
                : null,
            out List<CycleDevice> connectedCycle,
            out List<CycleDevice> skippedDevices);

        Assert.True(built);
        Assert.Equal("Resolved A", Assert.Single(connectedCycle).Name);
        Assert.Equal("id-b", Assert.Single(skippedDevices).Id);
    }


    [Fact]
    public void TryBuildExactIdCycleState_ReturnsFalse_WhenConfiguredIdMissing()
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "", Name = "A" },
        ];

        bool built = AppSwitchCommandCoordinator.TryBuildExactIdCycleState(
            configuredCycle,
            configured => configured,
            out List<CycleDevice> connectedCycle,
            out List<CycleDevice> skippedDevices);

        Assert.False(built);
        Assert.Empty(connectedCycle);
        Assert.Empty(skippedDevices);
    }


    [Fact]
    public void ResolveInitialCycleState_UsesExactResolution_WhenNoSkippedDevicesRemain()
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
        ];
        bool enumerated = false;

        SwitchCycleStateResolution result = AppSwitchCommandCoordinator.ResolveInitialCycleState(
            configuredCycle,
            configured => new CycleDevice { Id = configured.Id, Name = "Resolved A" },
            () =>
            {
                enumerated = true;
                return new SwitchCycleState(new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase), [], []);
            });

        Assert.False(result.UsedEnumeratedDevices);
        Assert.False(enumerated);
        AppViewModelAssertSingleConnected(result.State.ConnectedCycle, "Resolved A");
        Assert.Empty(result.State.SkippedDevices);
    }


    [Fact]
    public void ResolveInitialCycleState_FallsBackToEnumeratedState_WhenExactResolutionHasSkippedDevices()
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        SwitchCycleState enumeratedState = new(
            new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase),
            [new CycleDevice { Id = "enum-a", Name = "Enumerated A" }],
            [new CycleDevice { Id = "enum-b", Name = "Enumerated B" }]);

        SwitchCycleStateResolution result = AppSwitchCommandCoordinator.ResolveInitialCycleState(
            configuredCycle,
            configured => configured.Id == "id-a"
                ? new CycleDevice { Id = configured.Id, Name = "Resolved A" }
                : null,
            () => enumeratedState);

        Assert.True(result.UsedEnumeratedDevices);
        Assert.Same(enumeratedState.ConnectedCycle, result.State.ConnectedCycle);
        Assert.Same(enumeratedState.SkippedDevices, result.State.SkippedDevices);
    }


    [Fact]
    public void TryResolveConfiguredTarget_ReturnsNextDevice_WhenCurrentPresent()
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
            new CycleDevice { Id = "id-c", Name = "C" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveConfiguredTarget(
            configuredCycle,
            currentDeviceId: "id-b",
            reverse: false,
            out CycleDevice targetDevice);

        Assert.True(resolved);
        Assert.Equal("id-c", targetDevice.Id);
    }


    [Fact]
    public void TryResolveConfiguredTarget_ReturnsFalse_WhenSingleDeviceAlreadyCurrent()
    {
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveConfiguredTarget(
            configuredCycle,
            currentDeviceId: "id-a",
            reverse: false,
            out CycleDevice targetDevice);

        Assert.False(resolved);
        Assert.Equal(string.Empty, targetDevice.Id);
    }

}
