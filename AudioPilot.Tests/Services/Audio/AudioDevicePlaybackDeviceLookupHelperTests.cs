using System.Runtime.InteropServices;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDevicePlaybackDeviceLookupHelperTests
{
    [Fact]
    public void ResolveActivePlaybackDevicesById_FiltersInvalidDevices_AndKeepsActivePlaybackOnes()
    {
        Dictionary<string, FakeDevice> devices = new(StringComparer.OrdinalIgnoreCase)
        {
            ["out-1"] = new(true, true),
            ["out-2"] = new(false, true),
            ["out-3"] = new(true, false),
        };

        List<FakeDevice> result = AudioDevicePlaybackDeviceLookupHelper.ResolveActivePlaybackDevicesById(
            ["out-1", "out-2", "out-3", " "],
            deviceId => devices[deviceId],
            static device => device.IsPlayback,
            static device => device.IsActive,
            static device => device.Dispose(),
            static _ => { },
            static _ => { });

        FakeDevice device = Assert.Single(result);
        Assert.Same(devices["out-1"], device);
        Assert.False(devices["out-1"].Disposed);
        Assert.True(devices["out-2"].Disposed);
        Assert.True(devices["out-3"].Disposed);
    }

    [Fact]
    public void ResolveActivePlaybackDevicesById_LogsComAndGeneralFailures()
    {
        int comFailures = 0;
        int generalFailures = 0;

        List<FakeDevice> result = AudioDevicePlaybackDeviceLookupHelper.ResolveActivePlaybackDevicesById(
            ["missing", "boom"],
            static deviceId => deviceId switch
            {
                "missing" => throw new COMException("missing"),
                "boom" => throw new InvalidOperationException("boom"),
                _ => new FakeDevice(true, true),
            },
            static device => device.IsPlayback,
            static device => device.IsActive,
            static device => device.Dispose(),
            _ => comFailures++,
            _ => generalFailures++);

        Assert.Empty(result);
        Assert.Equal(1, comFailures);
        Assert.Equal(1, generalFailures);
    }

    [Fact]
    public void ResolveActivePlaybackDevicesById_DisposesDevice_WhenPredicateThrows()
    {
        FakeDevice device = new(true, true);
        int generalFailures = 0;

        List<FakeDevice> result = AudioDevicePlaybackDeviceLookupHelper.ResolveActivePlaybackDevicesById(
            ["out-1"],
            _ => device,
            static _ => throw new InvalidOperationException("boom"),
            static current => current.IsActive,
            static current => current.Dispose(),
            static _ => { },
            _ => generalFailures++);

        Assert.Empty(result);
        Assert.True(device.Disposed);
        Assert.Equal(1, generalFailures);
    }

    private sealed class FakeDevice(bool isPlayback, bool isActive)
    {
        public bool IsPlayback { get; } = isPlayback;
        public bool IsActive { get; } = isActive;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
