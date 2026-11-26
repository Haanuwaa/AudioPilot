using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceCycleCollectionProjectionHelperTests
{
    [Fact]
    public void Project_ForwardsProjectedDevices_AndFormatsDisposeFailures()
    {
        string? traceMessage = null;
        FakeCollection collection = new();

        List<CycleDevice> result = AudioDeviceCycleCollectionProjectionHelper.Project<FakeCollection, FakeDevice, CycleDevice>(
            collection,
            static (_, onDisposeFailure) =>
            {
                onDisposeFailure?.Invoke(new FakeDevice("Desk Speakers", "out-1"), new InvalidOperationException("boom"));
                return
                [
                    new CycleDevice { Id = "out-1", Name = "Desk Speakers" }
                ];
            },
            static device => device?.FriendlyName,
            static device => device?.Id,
            message => traceMessage = message);

        CycleDevice device = Assert.Single(result);
        Assert.Equal("out-1", device.Id);
        Assert.Equal("Desk Speakers", device.Name);
        Assert.NotNull(traceMessage);
        Assert.Contains("Ignored dispose exception for enumerated", traceMessage, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", traceMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_DoesNotRequireDisposeFailureCallback_ToReturnProjectedDevices()
    {
        FakeCollection collection = new();

        List<CycleDevice> result = AudioDeviceCycleCollectionProjectionHelper.Project<FakeCollection, FakeDevice, CycleDevice>(
            collection,
            static (_, _) =>
            [
                new CycleDevice { Id = "out-2", Name = "Headset" }
            ],
            static device => device?.FriendlyName,
            static device => device?.Id,
            static _ => { });

        CycleDevice device = Assert.Single(result);
        Assert.Equal("out-2", device.Id);
        Assert.Equal("Headset", device.Name);
    }

    private sealed class FakeCollection;

    private sealed class FakeDevice(string friendlyName, string id)
    {
        public string FriendlyName { get; } = friendlyName;
        public string Id { get; } = id;
    }
}
