using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Stress)]
[Collection("AudioHardwareStressIsolation")]
public sealed class AudioDeviceServiceSwitchSoakStressTests
{
    [StressFact]
    public async Task MissingTargets_RepeatedFailures_ReleaseGatesAndKeepManagedMemoryBounded()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(MissingTargets_RepeatedFailures_ReleaseGatesAndKeepManagedMemoryBounded)))
        {
            return;
        }

        using var service = CreateAudioService();

        for (int i = 0; i < 32; i++)
        {
            _ = await service.SwitchAudioDeviceAsync("missing-output-b", false, false, false, false);
            _ = await service.SwitchInputDeviceToAsync("missing-input-b", "Input B", false, null);
        }

        long beforeBytes = GC.GetTotalMemory(true);

        int iterations = 5000;
        for (int i = 0; i < iterations; i++)
        {
            if ((i & 1) == 0)
            {
                var (success, _) = await service.SwitchAudioDeviceAsync("missing-output-b", false, false, false, false);
                Assert.False(success);
            }
            else
            {
                var (success, _) = await service.SwitchInputDeviceToAsync("missing-input-b", "Input B", false, null);
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

    [StressFact]
    public async Task SuccessfulRoleSwitches_KeepOutputAndInputAssignmentsIndependentAcrossRepeatedChanges()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await Task.WhenAll(Task.Run(() => ExerciseAsync(true), deadline.Token), Task.Run(() => ExerciseAsync(false), deadline.Token)).WaitAsync(deadline.Token);

        async Task ExerciseAsync(bool output)
        {
            NRole[] roles = [NRole.Console, NRole.Multimedia, NRole.Communications];
            var defaults = roles.ToDictionary(role => role, _ => "initial");
            int writes = 0;
            for (int cycle = 0; cycle < 500; cycle++)
            {
                string target = $"{(output ? "output" : "input")}-{cycle % 2}";
                void Apply(string id, NRole role) { defaults[role] = id; writes++; }
                string Read(NRole role) => defaults[role];
                bool success = output
                    ? await DeviceRoleSwitchEngine.TrySwitchOutputRolesAsync(target, roles, Apply, Read, Logger.Instance,
                        "stress", "test", deadline.Token)
                    : await DeviceRoleSwitchEngine.TrySwitchInputRolesAsync(target, target, roles, Apply, Read, Logger.Instance,
                        "stress", "test", false, false, deadline.Token);
                Assert.True(success, $"Role switch failed: output={output}, cycle={cycle}");
                Assert.All(roles, role => Assert.Equal(target, defaults[role]));
            }
            Assert.Equal(1500, writes);
        }
    }

    private static AudioDeviceService CreateAudioService()
    {
        return new AudioDeviceService(new FakeInputListenPropertyWriter());
    }
}
