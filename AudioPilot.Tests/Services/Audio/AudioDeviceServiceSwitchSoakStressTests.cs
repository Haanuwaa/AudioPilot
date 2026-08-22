using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Stress)]
[Collection("AudioHardwareStressIsolation")]
public sealed class AudioDeviceServiceSwitchSoakStressTests
{
    [StressFact]
    public async Task SwitchSoak_AlternatingOutputAndInput_DoesNotDeadlock_AndMemoryStaysBounded_WhenStressEnabled()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(SwitchSoak_AlternatingOutputAndInput_DoesNotDeadlock_AndMemoryStaysBounded_WhenStressEnabled)))
        {
            return;
        }

        using var service = CreateAudioService();

        for (int i = 0; i < 32; i++)
        {
            _ = await service.SwitchAudioDeviceAsync("missing-output-a", "missing-output-b", false, false, false, false);
            _ = await service.SwitchInputDeviceAsync("missing-input-a", "Input A", "missing-input-b", "Input B", false, null);
        }

        long beforeBytes = GC.GetTotalMemory(true);

        int iterations = 5000;
        for (int i = 0; i < iterations; i++)
        {
            if ((i & 1) == 0)
            {
                var (success, _) = await service.SwitchAudioDeviceAsync("missing-output-a", "missing-output-b", false, false, false, false);
                Assert.False(success);
            }
            else
            {
                var (success, _) = await service.SwitchInputDeviceAsync("missing-input-a", "Input A", "missing-input-b", "Input B", false, null);
                Assert.False(success);
            }
        }

        long afterBytes = GC.GetTotalMemory(true);
        long growthBytes = afterBytes - beforeBytes;
        Assert.True(growthBytes < 128L * 1024L * 1024L, $"Unexpected memory growth during switch soak: {growthBytes} bytes");

        bool outputGateEntered = service.TryEnterOutputSwitchGateForTests();
        bool inputGateEntered = service.TryEnterInputSwitchGateForTests();

        try
        {
            Assert.True(outputGateEntered);
            Assert.True(inputGateEntered);
        }
        finally
        {
            if (outputGateEntered)
            {
                service.ExitOutputSwitchGateForTests();
            }

            if (inputGateEntered)
            {
                service.ExitInputSwitchGateForTests();
            }
        }
    }

    private static AudioDeviceService CreateAudioService()
    {
        return new AudioDeviceService(new FakeInputListenPropertyWriter());
    }
}
