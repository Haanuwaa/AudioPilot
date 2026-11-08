using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("AudioHardwareStressIsolation")]
public sealed class NativeApplicationRoutingTests
{
    [Theory]
    [InlineData(DataFlow.Render)]
    [InlineData(DataFlow.Capture)]
    public void OwnProcessPreference_CanBeReadChangedAndCleared_WithoutChangingWindowsDefaults(DataFlow flow)
    {
        if (!TestExecutionGuards.RequireAudioHardwareEnabled(nameof(OwnProcessPreference_CanBeReadChangedAndCleared_WithoutChangingWindowsDefaults))) return;
        string variable = flow == DataFlow.Render ? "AUDIOPILOT_TEST_OUTPUT_DEVICE_A" : "AUDIOPILOT_TEST_INPUT_DEVICE_A";
        string? id = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(id))
        {
            TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(OwnProcessPreference_CanBeReadChangedAndCleared_WithoutChangingWindowsDefaults), $"Set {variable} to an active endpoint.");
            return;
        }
        Role[] roles = [Role.Console, Role.Multimedia, Role.Communications];
        uint pid = checked((uint)Environment.ProcessId);
        using var enumerator = new MMDeviceEnumerator();
        var defaults = roles.ToDictionary(role => role, role => { using var current = enumerator.GetDefaultAudioEndpoint(flow, role); return current.ID; });
        using var endpoint = enumerator.GetDevice(id);
        using var client = endpoint.CreateAudioClient();
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, client.MixFormat, Guid.NewGuid());
        using var render = flow == DataFlow.Render ? client.AudioRenderClient : null;
        if (render != null) { _ = render.GetBuffer(client.BufferSize); render.ReleaseBuffer(client.BufferSize, AudioClientBufferFlags.Silent); }
        var original = new Dictionary<Role, string?>();
        foreach (Role role in roles)
        {
            Assert.True(AudioPolicyConfig.TryGetProcessDefaultDevice(pid, flow, role, out string? current), $"Readback unavailable for {flow}/{role}.");
            original[role] = current;
        }
        client.Start();
        var failures = new List<Exception>();
        try
        {
            Assert.Equal(ProcessAudioRoutingResult.Applied, AudioPolicyConfig.TrySetProcessDefaultDevice(pid, flow, roles, id));
            foreach (Role role in roles)
            {
                Assert.True(AudioPolicyConfig.TryGetProcessDefaultDevice(pid, flow, role, out string? current));
                Assert.Equal(id, current);
            }
            Assert.Equal(ProcessAudioRoutingResult.Applied, AudioPolicyConfig.TryClearProcessDefaultDevice(pid, flow, roles));
            foreach (Role role in roles)
            {
                Assert.True(AudioPolicyConfig.TryGetProcessDefaultDevice(pid, flow, role, out string? current));
                Assert.Null(current);
                using var actual = enumerator.GetDefaultAudioEndpoint(flow, role);
                Assert.Equal(defaults[role], actual.ID);
            }
        }
        catch (Exception ex) { failures.Add(ex); }
        finally
        {
            foreach (var pair in original)
            {
                try
                {
                    ProcessAudioRoutingResult restored = pair.Value == null
                        ? AudioPolicyConfig.TryClearProcessDefaultDevice(pid, flow, [pair.Key])
                        : AudioPolicyConfig.TrySetProcessDefaultDevice(pid, flow, [pair.Key], pair.Value);
                    Assert.NotEqual(ProcessAudioRoutingResult.Failed, restored);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            try { client.Stop(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Per-application preference or restoration failed.", failures);
    }
}
