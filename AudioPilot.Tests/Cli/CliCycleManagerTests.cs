using AudioPilot.Cli;
using AudioPilot.Models;

namespace AudioPilot.Tests.Cli;

public sealed class CliCycleManagerTests
{
    [Fact]
    public void TryAddDevice_ReturnsError_WhenDeviceAlreadyConfigured()
    {
        List<CycleDevice> cycle =
        [
            new() { Id = "out-1", Name = "Speakers" },
        ];

        bool updated = CliCycleManager.TryAddDevice(
            cycle,
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            "out-1",
            out CycleDevice? addedDevice,
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Null(addedDevice);
        Assert.Equal("cycle-device-already-configured", errorCode);
        Assert.Equal("Device 'out-1' is already configured in the cycle.", message);
        Assert.Single(cycle);
    }

    [Fact]
    public void TryAddDevice_ReturnsError_WhenActiveDeviceIsMissing()
    {
        List<CycleDevice> cycle = [];

        bool updated = CliCycleManager.TryAddDevice(
            cycle,
            [new CycleDevice { Id = "out-2", Name = "Headset" }],
            "out-1",
            out CycleDevice? addedDevice,
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Null(addedDevice);
        Assert.Equal("cycle-device-not-found", errorCode);
        Assert.Equal("Active device 'out-1' was not found.", message);
        Assert.Empty(cycle);
    }

    [Fact]
    public void TryRemoveDevice_ReturnsError_WhenDeviceIsNotConfigured()
    {
        List<CycleDevice> cycle =
        [
            new() { Id = "in-1", Name = "Desk Mic" },
        ];

        bool updated = CliCycleManager.TryRemoveDevice(
            cycle,
            "in-2",
            out CycleDevice? removedDevice,
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Null(removedDevice);
        Assert.Equal("cycle-device-not-configured", errorCode);
        Assert.Equal("Device 'in-2' is not configured in the cycle.", message);
        Assert.Single(cycle);
    }

    [Fact]
    public void TryReorder_ReturnsError_WhenDeviceCountDoesNotMatch()
    {
        List<CycleDevice> cycle =
        [
            new() { Id = "out-1", Name = "Speakers" },
            new() { Id = "out-2", Name = "Headset" },
        ];

        bool updated = CliCycleManager.TryReorder(
            cycle,
            ["out-2"],
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Equal("cycle-reorder-count-mismatch", errorCode);
        Assert.Equal("Reorder requires exactly 2 device ids.", message);
        Assert.Equal(["out-1", "out-2"], cycle.Select(static device => device.Id));
    }

    [Fact]
    public void TryReorder_ReturnsError_WhenRequestContainsDuplicateDevice()
    {
        List<CycleDevice> cycle =
        [
            new() { Id = "out-1", Name = "Speakers" },
            new() { Id = "out-2", Name = "Headset" },
        ];

        bool updated = CliCycleManager.TryReorder(
            cycle,
            ["out-2", "out-2"],
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Equal("cycle-reorder-duplicate-device", errorCode);
        Assert.Equal("Device 'out-2' was specified more than once in the reorder request.", message);
        Assert.Equal(["out-1", "out-2"], cycle.Select(static device => device.Id));
    }

    [Fact]
    public void TryReorder_ReturnsError_WhenRequestContainsUnknownDevice()
    {
        List<CycleDevice> cycle =
        [
            new() { Id = "in-1", Name = "Desk Mic" },
            new() { Id = "in-2", Name = "Headset Mic" },
        ];

        bool updated = CliCycleManager.TryReorder(
            cycle,
            ["in-2", "in-3"],
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Equal("cycle-reorder-unknown-device", errorCode);
        Assert.Equal("Device 'in-3' is not configured in the cycle.", message);
        Assert.Equal(["in-1", "in-2"], cycle.Select(static device => device.Id));
    }

    [Fact]
    public void ApplyMutation_RedactsFailureSelectorsAndMatchingEndpointIds()
    {
        List<CycleDevice> cycle = [];
        List<CycleDevice> active =
        [
            new() { Id = "{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}", Name = "Alex's Headphones" },
            new() { Id = "{0.0.0.00000000}.{22222222-2222-2222-2222-222222222222}", Name = "Alex's Headphones" },
        ];

        (bool success, string output) = CliCycleManager.ApplyMutation(
            cycle,
            active,
            kind: "output",
            action: "add",
            deviceId: "Alex's Headphones",
            orderedDeviceIds: null,
            jsonOutput: true,
            redactOutput: true);

        Assert.False(success);
        Assert.DoesNotContain("Alex's Headphones", output, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", output, StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReorder_DuplicateConfiguredIdsInSavedCycle_ReturnsStructuredFailure()
    {
        List<CycleDevice> cycle =
        [
            new() { Id = "out-1", Name = "Speakers" },
            new() { Id = "out-1", Name = "Speakers Duplicate" },
        ];

        bool updated = CliCycleManager.TryReorder(
            cycle,
            ["out-1", "out-2"],
            out string errorCode,
            out string message);

        Assert.False(updated);
        Assert.Equal("cycle-reorder-duplicate-configured-device", errorCode);
        Assert.Contains("duplicate", message, StringComparison.OrdinalIgnoreCase);
    }
}
