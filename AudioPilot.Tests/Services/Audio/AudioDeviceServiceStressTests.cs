using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class AudioDeviceServiceHotplugStressTests
{
    [StressFact]
    public void HotplugBurst_DeviceStateHandler_RemainsStable()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(HotplugBurst_DeviceStateHandler_RemainsStable)))
        {
            return;
        }

        using var service = CreateAudioService();

        int eventCount = 0;
        service.DeviceStateChanged += () => Interlocked.Increment(ref eventCount);

        const int burstCount = 250;
        for (int i = 0; i < burstCount; i++)
        {
            service.RaiseDeviceStateChangedForTests();
        }

        Assert.Equal(burstCount, eventCount);
    }

    private static AudioDeviceService CreateAudioService()
    {
        return new AudioDeviceService(new FakeInputListenPropertyWriter());
    }
}

