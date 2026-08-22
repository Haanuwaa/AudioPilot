using System.Runtime.InteropServices;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("AudioHardwareStressIsolation")]
public sealed class AudioDeviceIntegrationTests
{
    private const string OutputDeviceAEnvVar = "AUDIOPILOT_TEST_OUTPUT_DEVICE_A";
    private const string OutputDeviceBEnvVar = "AUDIOPILOT_TEST_OUTPUT_DEVICE_B";
    private const string InputDeviceAEnvVar = "AUDIOPILOT_TEST_INPUT_DEVICE_A";
    private const string InputDeviceBEnvVar = "AUDIOPILOT_TEST_INPUT_DEVICE_B";
    private const int RapidOutputSwitchCycles = 20;

    [IntegrationFact]
    public async Task SwitchAudioDeviceAsync_UpdatesAllRenderRoles_WhenIntegrationEnabled()
    {
        if (!TestExecutionGuards.RequireIntegrationEnabled(nameof(SwitchAudioDeviceAsync_UpdatesAllRenderRoles_WhenIntegrationEnabled)))
        {
            return;
        }

        if (!TryGetIntegrationDevicePair(OutputDeviceAEnvVar, OutputDeviceBEnvVar, out var deviceA, out var deviceB))
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointsAvailable(enumerator, DataFlow.Render, Role.Console, Role.Multimedia, Role.Communications))
        {
            return;
        }

        if (!EnsureConfiguredDevicesAreActive(enumerator, DataFlow.Render, deviceA, deviceB))
        {
            return;
        }

        var originalConsole = TryGetDefaultEndpointId(enumerator, DataFlow.Render, Role.Console);
        var originalMultimedia = TryGetDefaultEndpointId(enumerator, DataFlow.Render, Role.Multimedia);
        var originalCommunications = TryGetDefaultEndpointId(enumerator, DataFlow.Render, Role.Communications);

        using var service = new AudioDeviceService();
        int notificationCount = 0;

        try
        {
            if (!await PrimeDefaultEndpointsAsync(enumerator, DataFlow.Render, deviceA, Role.Console, Role.Multimedia, Role.Communications))
            {
                return;
            }

            using MMDeviceNotificationClient notificationClient = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            notificationClient.DefaultDeviceChanged += (_, args) =>
            {
                if (args.Flow == DataFlow.Render &&
                    string.Equals(args.DeviceId, deviceB, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref notificationCount);
                }
            };
            service.RegisterNotificationClient();

            var (success, _) = await service.SwitchAudioDeviceAsync(
                deviceA,
                deviceB,
                muteMic: false,
                muteSound: false,
                deafen: false,
                preserveAudioLevels: false);

            Assert.True(success);

            Assert.Equal(deviceB, GetRequiredDefaultEndpointId(enumerator, DataFlow.Render, Role.Console));
            Assert.Equal(deviceB, GetRequiredDefaultEndpointId(enumerator, DataFlow.Render, Role.Multimedia));
            Assert.Equal(deviceB, GetRequiredDefaultEndpointId(enumerator, DataFlow.Render, Role.Communications));
            await TestExecutionGuards.WaitUntilAsync(
                () => Volatile.Read(ref notificationCount) > 0,
                "NAudio 3 managed notification client did not observe the real default-render-device change.",
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.UnregisterNotificationClient();
            RestoreRoleIfPresent(originalConsole, Role.Console);
            RestoreRoleIfPresent(originalMultimedia, Role.Multimedia);
            RestoreRoleIfPresent(originalCommunications, Role.Communications);
        }
    }

    [IntegrationFact]
    public async Task SwitchInputDeviceToAsync_UpdatesAllCaptureRoles_WhenIntegrationEnabled()
    {
        if (!TestExecutionGuards.RequireIntegrationEnabled(nameof(SwitchInputDeviceToAsync_UpdatesAllCaptureRoles_WhenIntegrationEnabled)))
        {
            return;
        }

        if (!TryGetIntegrationDevicePair(InputDeviceAEnvVar, InputDeviceBEnvVar, out var deviceA, out var deviceB))
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointsAvailable(enumerator, DataFlow.Capture, Role.Console, Role.Multimedia, Role.Communications))
        {
            return;
        }

        if (!EnsureConfiguredDevicesAreActive(enumerator, DataFlow.Capture, deviceA, deviceB))
        {
            return;
        }

        var originalConsole = TryGetDefaultEndpointId(enumerator, DataFlow.Capture, Role.Console);
        var originalMultimedia = TryGetDefaultEndpointId(enumerator, DataFlow.Capture, Role.Multimedia);
        var originalCommunications = TryGetDefaultEndpointId(enumerator, DataFlow.Capture, Role.Communications);

        using var service = new AudioDeviceService();
        int notificationCount = 0;

        try
        {
            if (!await PrimeDefaultEndpointsAsync(enumerator, DataFlow.Capture, deviceA, Role.Console, Role.Multimedia, Role.Communications))
            {
                return;
            }

            using MMDeviceNotificationClient notificationClient = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            notificationClient.DefaultDeviceChanged += (_, args) =>
            {
                if (args.Flow == DataFlow.Capture &&
                    string.Equals(args.DeviceId, deviceB, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref notificationCount);
                }
            };
            service.RegisterNotificationClient();

            (bool success, _) = await service.SwitchInputDeviceToAsync(
                deviceB,
                "Integration Input B",
                preserveAudioLevels: false,
                showOverlay: null);

            Assert.True(success);

            Assert.Equal(deviceB, GetRequiredDefaultEndpointId(enumerator, DataFlow.Capture, Role.Console));
            Assert.Equal(deviceB, GetRequiredDefaultEndpointId(enumerator, DataFlow.Capture, Role.Multimedia));
            Assert.Equal(deviceB, GetRequiredDefaultEndpointId(enumerator, DataFlow.Capture, Role.Communications));
            await TestExecutionGuards.WaitUntilAsync(
                () => Volatile.Read(ref notificationCount) > 0,
                "NAudio 3 managed notification client did not observe the real default-capture-device change.",
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.UnregisterNotificationClient();
            RestoreRoleIfPresent(originalConsole, Role.Console);
            RestoreRoleIfPresent(originalMultimedia, Role.Multimedia);
            RestoreRoleIfPresent(originalCommunications, Role.Communications);
        }
    }

    [IntegrationFact]
    public void SetMuteMethods_UpdateAndRestoreAllDefaultEndpoints_WhenIntegrationEnabled()
    {
        if (!TestExecutionGuards.RequireIntegrationEnabled(nameof(SetMuteMethods_UpdateAndRestoreAllDefaultEndpoints_WhenIntegrationEnabled)))
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointsAvailable(enumerator, DataFlow.Render, Role.Console, Role.Multimedia, Role.Communications) ||
            !EnsureDefaultEndpointsAvailable(enumerator, DataFlow.Capture, Role.Console, Role.Multimedia, Role.Communications))
        {
            return;
        }

        Dictionary<string, bool> originalPlaybackStates = CaptureDistinctDefaultMuteStates(enumerator, DataFlow.Render);
        Dictionary<string, bool> originalCaptureStates = CaptureDistinctDefaultMuteStates(enumerator, DataFlow.Capture);
        using var service = new AudioDeviceService();

        try
        {
            ComThreadingHelper.RunOnCoreAudioThread(() => service.SetPlaybackMute(true));
            AssertMuteStates(enumerator, originalPlaybackStates.Keys, expectedMuted: true);

            ComThreadingHelper.RunOnCoreAudioThread(() => service.SetPlaybackMute(false));
            AssertMuteStates(enumerator, originalPlaybackStates.Keys, expectedMuted: false);

            ComThreadingHelper.RunOnCoreAudioThread(() => service.SetMicrophoneMute(true));
            AssertMuteStates(enumerator, originalCaptureStates.Keys, expectedMuted: true);

            ComThreadingHelper.RunOnCoreAudioThread(() => service.SetMicrophoneMute(false));
            AssertMuteStates(enumerator, originalCaptureStates.Keys, expectedMuted: false);
        }
        finally
        {
            RestoreMuteStates(enumerator, originalPlaybackStates);
            RestoreMuteStates(enumerator, originalCaptureStates);
        }
    }

    [HardwareSoakFact]
    [Trait(TestCategories.Name, TestCategories.Stress)]
    [Trait(TestCategories.Name, TestCategories.HardwareSoak)]
    public async Task RapidOutputSwitching_RepeatedlyUpdatesAllRolesAndManagedNotifications()
    {
        if (!TryGetIntegrationDevicePair(OutputDeviceAEnvVar, OutputDeviceBEnvVar, out var deviceA, out var deviceB))
        {
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        if (!EnsureDefaultEndpointsAvailable(enumerator, DataFlow.Render, Role.Console, Role.Multimedia, Role.Communications) ||
            !EnsureConfiguredDevicesAreActive(enumerator, DataFlow.Render, deviceA, deviceB))
        {
            return;
        }

        string? originalConsole = TryGetDefaultEndpointId(enumerator, DataFlow.Render, Role.Console);
        string? originalMultimedia = TryGetDefaultEndpointId(enumerator, DataFlow.Render, Role.Multimedia);
        string? originalCommunications = TryGetDefaultEndpointId(enumerator, DataFlow.Render, Role.Communications);

        using var service = new AudioDeviceService();
        int notificationCount = 0;

        try
        {
            if (!await PrimeDefaultEndpointsAsync(enumerator, DataFlow.Render, deviceA, Role.Console, Role.Multimedia, Role.Communications))
            {
                return;
            }

            using MMDeviceNotificationClient notificationClient = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            notificationClient.DefaultDeviceChanged += (_, args) =>
            {
                if (args.Flow == DataFlow.Render)
                {
                    Interlocked.Increment(ref notificationCount);
                }
            };
            service.RegisterNotificationClient();

            string current = deviceA;
            for (int index = 0; index < RapidOutputSwitchCycles; index++)
            {
                string target = index % 2 == 0 ? deviceB : deviceA;
                (bool success, _) = await service.SwitchAudioDeviceAsync(
                    current,
                    target,
                    muteMic: false,
                    muteSound: false,
                    deafen: false,
                    preserveAudioLevels: false);
                Assert.True(success, $"Rapid hardware switch {index + 1} to '{target}' failed.");

                await WaitForAllDefaultRolesAsync(enumerator, DataFlow.Render, target);
                current = target;
            }

            Assert.True(
                Volatile.Read(ref notificationCount) >= RapidOutputSwitchCycles,
                $"Expected at least {RapidOutputSwitchCycles} managed default-device notifications, but observed {Volatile.Read(ref notificationCount)}.");
        }
        finally
        {
            service.UnregisterNotificationClient();
            RestoreRoleIfPresent(originalConsole, Role.Console);
            RestoreRoleIfPresent(originalMultimedia, Role.Multimedia);
            RestoreRoleIfPresent(originalCommunications, Role.Communications);
        }
    }

    private static bool TryGetIntegrationDevicePair(string envA, string envB, out string deviceA, out string deviceB)
    {
        bool hasA = TestExecutionGuards.TryGetNonEmptyEnvironmentVariable(envA, out deviceA);
        bool hasB = TestExecutionGuards.TryGetNonEmptyEnvironmentVariable(envB, out deviceB);

        if (!hasA || !hasB)
        {
            string message =
                $"Integration test prerequisites missing. Set environment variables '{envA}' and '{envB}' when AUDIOPILOT_RUN_INTEGRATION is enabled.";

            if (TestExecutionGuards.ShouldRequireIntegrationHardware())
            {
                throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(nameof(AudioDeviceIntegrationTests), message);
            }

            return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(AudioDeviceIntegrationTests), message);
        }

        return true;
    }

    private static Dictionary<string, bool> CaptureDistinctDefaultMuteStates(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(flow, role);
            if (!states.ContainsKey(endpoint.ID))
            {
                states[endpoint.ID] = endpoint.AudioEndpointVolume.Mute;
            }
        }

        return states;
    }

    private static void AssertMuteStates(MMDeviceEnumerator enumerator, IEnumerable<string> endpointIds, bool expectedMuted)
    {
        foreach (string endpointId in endpointIds)
        {
            using MMDevice endpoint = enumerator.GetDevice(endpointId);
            Assert.Equal(expectedMuted, endpoint.AudioEndpointVolume.Mute);
        }
    }

    private static void RestoreMuteStates(MMDeviceEnumerator enumerator, IReadOnlyDictionary<string, bool> states)
    {
        foreach ((string endpointId, bool muted) in states)
        {
            try
            {
                using MMDevice endpoint = enumerator.GetDevice(endpointId);
                endpoint.AudioEndpointVolume.Mute = muted;
            }
            catch
            {
            }
        }
    }

    private static string GetRequiredDefaultEndpointId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, role);
        return endpoint.ID;
    }

    private static string? TryGetDefaultEndpointId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, role);
            return endpoint.ID;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreRoleIfPresent(string? deviceId, Role role)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        try
        {
            AudioPolicyConfig.SetDefaultDevice(deviceId, role);
        }
        catch
        {
        }
    }

    private static bool EnsureDefaultEndpointsAvailable(MMDeviceEnumerator enumerator, DataFlow flow, params Role[] roles)
    {
        foreach (Role role in roles)
        {
            try
            {
                using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, role);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
            {
                string message = $"Audio integration prerequisites unavailable: no default endpoint for flow '{flow}' role '{role}' (HRESULT 0x80070490). Configure a hardware-capable runner with active default audio endpoints.";

                if (TestExecutionGuards.ShouldRequireIntegrationHardware())
                {
                    throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(nameof(AudioDeviceIntegrationTests), message, ex);
                }

                return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(AudioDeviceIntegrationTests), message);
            }
        }

        return true;
    }

    private static bool EnsureConfiguredDevicesAreActive(MMDeviceEnumerator enumerator, DataFlow flow, string deviceA, string deviceB)
    {
        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < devices.Count; index++)
        {
            using MMDevice device = devices[index];
            activeIds.Add(device.ID);
        }

        if (activeIds.Contains(deviceA) && activeIds.Contains(deviceB))
        {
            return true;
        }

        string message =
            $"Audio integration prerequisites unavailable: configured {flow} test device IDs are not both active. Ensure the endpoints from the environment variables are currently connected and enabled.";

        if (TestExecutionGuards.ShouldRequireIntegrationHardware())
        {
            throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(nameof(AudioDeviceIntegrationTests), message);
        }

        return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(AudioDeviceIntegrationTests), message);
    }

    private static async Task<bool> PrimeDefaultEndpointsAsync(MMDeviceEnumerator enumerator, DataFlow flow, string targetDeviceId, params Role[] roles)
    {
        try
        {
            foreach (Role role in roles)
            {
                AudioPolicyConfig.SetDefaultDevice(targetDeviceId, role);
            }
        }
        catch (COMException ex)
        {
            string message =
                $"Audio integration prerequisite failed while priming {flow} defaults to the configured test device (HRESULT 0x{(uint)ex.HResult:X8}).";

            if (TestExecutionGuards.ShouldRequireIntegrationHardware())
            {
                throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(nameof(AudioDeviceIntegrationTests), message, ex);
            }

            return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(AudioDeviceIntegrationTests), message);
        }

        long deadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(2).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            bool allMatch = true;
            foreach (Role role in roles)
            {
                if (!string.Equals(TryGetDefaultEndpointId(enumerator, flow, role), targetDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                return true;
            }

            await Task.Delay(50);
        }

        string timeoutMessage =
            $"Audio integration prerequisite failed while waiting for {flow} defaults to settle on the configured test device.";

        if (TestExecutionGuards.ShouldRequireIntegrationHardware())
        {
            throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(nameof(AudioDeviceIntegrationTests), timeoutMessage);
        }

        return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(AudioDeviceIntegrationTests), timeoutMessage);
    }

    private static async Task WaitForAllDefaultRolesAsync(MMDeviceEnumerator enumerator, DataFlow flow, string targetDeviceId)
    {
        await TestExecutionGuards.WaitUntilAsync(
            () =>
                string.Equals(TryGetDefaultEndpointId(enumerator, flow, Role.Console), targetDeviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(TryGetDefaultEndpointId(enumerator, flow, Role.Multimedia), targetDeviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(TryGetDefaultEndpointId(enumerator, flow, Role.Communications), targetDeviceId, StringComparison.OrdinalIgnoreCase),
            $"Default {flow} roles did not settle on '{targetDeviceId}' during rapid switching.",
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(25));
    }
}

