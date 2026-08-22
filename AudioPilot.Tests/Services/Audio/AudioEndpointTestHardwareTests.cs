using System.Diagnostics;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Trait(TestCategories.Name, TestCategories.Stress)]
[Trait(TestCategories.Name, TestCategories.HardwareSoak)]
public sealed class AudioEndpointTestHardwareTests
{
    private const string OutputEndpointVariable = "AUDIOPILOT_TEST_OUTPUT_DEVICE_ID";
    private const string InputEndpointVariable = "AUDIOPILOT_TEST_INPUT_DEVICE_ID";
    private const string MonitorEndpointVariable = "AUDIOPILOT_TEST_MONITOR_DEVICE_ID";

    [HardwareSoakFact]
    public async Task NonDefaultOutput_PlaysDirectlyWithoutChangingWindowsDefault()
    {
        string? endpointId = Environment.GetEnvironmentVariable(OutputEndpointVariable);
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(NonDefaultOutput_PlaysDirectlyWithoutChangingWindowsDefault), $"Set {OutputEndpointVariable} to an active non-default render endpoint.");
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice endpoint = enumerator.GetDevice(endpointId);
        using MMDevice originalDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (endpoint.ID.Equals(originalDefault.ID, StringComparison.OrdinalIgnoreCase))
        {
            TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(NonDefaultOutput_PlaysDirectlyWithoutChangingWindowsDefault), $"{OutputEndpointVariable} currently identifies the default render endpoint.");
            return;
        }

        var factory = new WasapiAudioEndpointTestSessionFactory();
        IAudioOutputTestSession session = await factory.CreateOutputAsync(
            new AudioEndpointReference(endpoint.ID, endpoint.FriendlyName),
            TestContext.Current.CancellationToken);
        await using (session)
        {
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        using MMDevice currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Assert.Equal(originalDefault.ID, currentDefault.ID, ignoreCase: true);
    }

    [HardwareSoakFact]
    public async Task Microphone_MetersWithoutFilesAndOptionalMonitoringDrainsCleanly()
    {
        string? endpointId = Environment.GetEnvironmentVariable(InputEndpointVariable);
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            TestExecutionGuards.ReportOptionalIntegrationPrerequisite(nameof(Microphone_MetersWithoutFilesAndOptionalMonitoringDrainsCleanly), $"Set {InputEndpointVariable} to an active capture endpoint.");
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice endpoint = enumerator.GetDevice(endpointId);
        AudioEndpointReference? monitorEndpoint = ResolveOptionalMonitorEndpoint(enumerator);
        var factory = new WasapiAudioEndpointTestSessionFactory();
        IAudioInputTestSession session = await factory.CreateInputAsync(
            new AudioEndpointReference(endpoint.ID, endpoint.FriendlyName),
            monitorEndpoint,
            TestContext.Current.CancellationToken);
        await using (session)
        {
            await TestExecutionGuards.WaitUntilAsync(
                () => session.ReadLevel().SampleRevision > 0,
                "No microphone packets were observed.",
                TimeSpan.FromSeconds(5));

            if (monitorEndpoint.HasValue)
            {
                await session.ConfigureMonitoringAsync(true, monitorEndpoint, 0.25f, TestContext.Current.CancellationToken);
                Assert.True(session.MonitoringEnabled);
                await session.ConfigureMonitoringAsync(false, monitorEndpoint, 0.25f, TestContext.Current.CancellationToken);
                Assert.False(session.MonitoringEnabled);
            }
        }
    }

    [HardwareSoakFact]
    public async Task RepeatedStartStopAndMonitorReconfiguration_KeepNativeResourceGrowthBounded()
    {
        string? outputId = Environment.GetEnvironmentVariable(OutputEndpointVariable);
        string? inputId = Environment.GetEnvironmentVariable(InputEndpointVariable);
        if (string.IsNullOrWhiteSpace(outputId) && string.IsNullOrWhiteSpace(inputId))
        {
            TestExecutionGuards.ReportOptionalIntegrationPrerequisite(
                nameof(RepeatedStartStopAndMonitorReconfiguration_KeepNativeResourceGrowthBounded),
                $"Set {OutputEndpointVariable}, {InputEndpointVariable}, or both to active endpoints.");
            return;
        }

        using var enumerator = new MMDeviceEnumerator();
        AudioEndpointReference? output = ResolveOptionalEndpoint(enumerator, outputId);
        AudioEndpointReference? input = ResolveOptionalEndpoint(enumerator, inputId);
        AudioEndpointReference? monitor = ResolveOptionalMonitorEndpoint(enumerator);
        var factory = new WasapiAudioEndpointTestSessionFactory();

        await ExerciseLifecycleIterationAsync(factory, output, input, monitor);
        ForceFullCollection();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        int baselineHandles = process.HandleCount;
        long baselinePrivateBytes = process.PrivateMemorySize64;

        for (int iteration = 0; iteration < 20; iteration++)
        {
            await ExerciseLifecycleIterationAsync(factory, output, input, monitor);
        }

        ForceFullCollection();
        process.Refresh();
        int handleGrowth = Math.Max(0, process.HandleCount - baselineHandles);
        long privateByteGrowth = Math.Max(0, process.PrivateMemorySize64 - baselinePrivateBytes);

        Assert.True(handleGrowth <= 96, $"Native handle count grew by {handleGrowth} across repeated audio-test lifecycles.");
        Assert.True(privateByteGrowth <= 128L * 1024 * 1024, $"Private memory grew by {privateByteGrowth:N0} bytes across repeated audio-test lifecycles.");
    }

    private static async Task ExerciseLifecycleIterationAsync(
        WasapiAudioEndpointTestSessionFactory factory,
        AudioEndpointReference? output,
        AudioEndpointReference? input,
        AudioEndpointReference? monitor)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        if (output.HasValue)
        {
            await using IAudioOutputTestSession outputSession = await factory.CreateOutputAsync(output.Value, cancellationToken);
        }

        if (input.HasValue)
        {
            await using IAudioInputTestSession inputSession = await factory.CreateInputAsync(input.Value, monitor, cancellationToken);
            await TestExecutionGuards.WaitUntilAsync(
                () => inputSession.ReadLevel().SampleRevision > 0,
                "No microphone packet was observed during repeated audio-test lifecycle validation.",
                TimeSpan.FromSeconds(5));
            if (monitor.HasValue)
            {
                await inputSession.ConfigureMonitoringAsync(true, monitor, 0.25f, cancellationToken);
                Assert.True(inputSession.MonitoringEnabled);
                await inputSession.ConfigureMonitoringAsync(false, monitor, 0.25f, cancellationToken);
            }
        }
    }

    private static AudioEndpointReference? ResolveOptionalEndpoint(MMDeviceEnumerator enumerator, string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        using MMDevice endpoint = enumerator.GetDevice(endpointId);
        return new AudioEndpointReference(endpoint.ID, endpoint.FriendlyName);
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static AudioEndpointReference? ResolveOptionalMonitorEndpoint(MMDeviceEnumerator enumerator)
    {
        string? monitorId = Environment.GetEnvironmentVariable(MonitorEndpointVariable);
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return null;
        }

        using MMDevice monitor = enumerator.GetDevice(monitorId);
        return new AudioEndpointReference(monitor.ID, monitor.FriendlyName);
    }
}
