using System.Diagnostics;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("AudioHardwareStressIsolation")]
public sealed class AudioDeviceSessionLifecycleIntegrationTests
{
    private const string SoakMinutesEnvVar = "AUDIOPILOT_HARDWARE_SOAK_MINUTES";

    [IntegrationFact]
    public async Task ManagedNotificationsAndSessionWrappers_ObserveRealSilentPlaybackSession()
    {
        if (!TestExecutionGuards.RequireIntegrationEnabled(nameof(ManagedNotificationsAndSessionWrappers_ObserveRealSilentPlaybackSession)) ||
            !EnsureDefaultRenderEndpointAvailable())
        {
            return;
        }

        using var service = new AudioDeviceService();
        int createdCount = 0;
        int lifecycleCount = 0;
        service.AudioSessionCreated += mode =>
        {
            if (mode == AudioMixerMode.Output)
            {
                Interlocked.Increment(ref createdCount);
            }
        };
        service.AudioSessionLifecycleChanged += signal =>
        {
            if (signal.MixerMode == AudioMixerMode.Output)
            {
                Interlocked.Increment(ref lifecycleCount);
            }
        };

        service.AcquireSessionMonitoring(AudioMixerMode.Output);
        service.RegisterNotificationClient();
        try
        {
            await WaitForOutputSessionMonitoringReadyAsync(service);
            int lifecycleBefore = Volatile.Read(ref lifecycleCount);
            await RunSilentSessionCycleAsync(async () =>
            {
                _ = await service.GetAllAudioSessionSnapshotsAsync(
                    AudioMixerMode.Output,
                    recentSnapshotCacheWindowMs: 0);
                await TestExecutionGuards.WaitUntilAsync(
                    () => Volatile.Read(ref createdCount) > 0,
                    "NAudio 3 session-created wrapper event was not observed for a real WASAPI session.",
                    TimeSpan.FromSeconds(5));
            }, afterStop: () => TestExecutionGuards.WaitUntilAsync(
                () => Volatile.Read(ref lifecycleCount) > lifecycleBefore,
                "NAudio 3 session lifecycle callbacks were not observed after a real WASAPI session stopped.",
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            service.ReleaseSessionMonitoring(AudioMixerMode.Output);
            service.UnregisterNotificationClient();
        }
    }

    [HardwareSoakFact]
    [Trait(TestCategories.Name, TestCategories.Stress)]
    [Trait(TestCategories.Name, TestCategories.HardwareSoak)]
    public async Task SessionLifecycleSoak_RepeatedRealSessionsAndSnapshots_KeepResourcesBounded()
    {
        if (!EnsureDefaultRenderEndpointAvailable(required: true))
        {
            return;
        }

        TimeSpan duration = ResolveSoakDuration();
        using var service = new AudioDeviceService();
        int createdCount = 0;
        int lifecycleCount = 0;
        service.AudioSessionCreated += mode =>
        {
            if (mode == AudioMixerMode.Output)
            {
                Interlocked.Increment(ref createdCount);
            }
        };
        service.AudioSessionLifecycleChanged += signal =>
        {
            if (signal.MixerMode == AudioMixerMode.Output)
            {
                Interlocked.Increment(ref lifecycleCount);
            }
        };

        service.AcquireSessionMonitoring(AudioMixerMode.Output);
        service.RegisterNotificationClient();
        try
        {
            await WaitForOutputSessionMonitoringReadyAsync(service);
            string[] endpointIds = [.. service.GetActivePlaybackCycleEntries()
                .Select(device => device.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            Assert.NotEmpty(endpointIds);

            for (int index = 0; index < 8; index++)
            {
                int createdBefore = Volatile.Read(ref createdCount);
                await RunSilentSessionCycleAsync(
                    () => TestExecutionGuards.WaitUntilAsync(
                        () => Volatile.Read(ref createdCount) > createdBefore,
                        "A warm-up WASAPI session was not observed by the session monitor.",
                        TimeSpan.FromSeconds(5)),
                    endpointId: endpointIds[index % endpointIds.Length]);
            }

            ProcessResourceSnapshot baseline = CaptureResources();
            var stopwatch = Stopwatch.StartNew();
            int cycleCount = 0;
            int maximumSnapshotCount = 0;
            var endpointsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (stopwatch.Elapsed < duration)
            {
                string endpointId = endpointIds[cycleCount % endpointIds.Length];
                endpointsUsed.Add(endpointId);
                int createdBefore = Volatile.Read(ref createdCount);
                await RunSilentSessionCycleAsync(async () =>
                {
                    IReadOnlyList<AudioSessionSnapshot> snapshots = await service.GetAllAudioSessionSnapshotsAsync(
                        AudioMixerMode.Output,
                        recentSnapshotCacheWindowMs: 0);
                    maximumSnapshotCount = Math.Max(maximumSnapshotCount, snapshots.Count);
                    await TestExecutionGuards.WaitUntilAsync(
                        () => Volatile.Read(ref createdCount) > createdBefore,
                        "A soak WASAPI session was not observed by the session monitor.",
                        TimeSpan.FromSeconds(5));
                }, endpointId);
                cycleCount++;

                if (cycleCount % 10 == 0)
                {
                    Assert.NotEmpty(service.GetActivePlaybackCycleEntries());
                    _ = service.GetActiveCaptureCycleEntries();
                }
            }

            ProcessResourceSnapshot final = CaptureResources();
            long managedGrowth = final.ManagedBytes - baseline.ManagedBytes;
            long privateGrowth = final.PrivateBytes - baseline.PrivateBytes;
            int handleGrowth = final.HandleCount - baseline.HandleCount;

            Console.WriteLine(
                $"Hardware session soak completed: duration={stopwatch.Elapsed}, cycles={cycleCount}, " +
                $"availableEndpoints={endpointIds.Length}, endpointsUsed={endpointsUsed.Count}, " +
                $"created={Volatile.Read(ref createdCount)}, lifecycle={Volatile.Read(ref lifecycleCount)}, " +
                $"maxSnapshots={maximumSnapshotCount}, managedGrowth={managedGrowth}, " +
                $"privateGrowth={privateGrowth}, handleGrowth={handleGrowth}.");

            Assert.True(cycleCount > 0, "The hardware session soak did not complete any session cycles.");
            Assert.Equal(endpointIds.Length, endpointsUsed.Count);
            Assert.True(Volatile.Read(ref createdCount) > 0, "No real session-created callbacks were observed during the hardware soak.");
            Assert.True(Volatile.Read(ref lifecycleCount) > 0, "No real session lifecycle callbacks were observed during the hardware soak.");
            Assert.True(managedGrowth < 64L * 1024L * 1024L, $"Managed memory grew unexpectedly during the hardware soak: {managedGrowth} bytes.");
            Assert.True(privateGrowth < 256L * 1024L * 1024L, $"Private memory grew unexpectedly during the hardware soak: {privateGrowth} bytes.");
            Assert.True(handleGrowth < 256, $"Process handle count grew unexpectedly during the hardware soak: {handleGrowth} handles.");
        }
        finally
        {
            service.ReleaseSessionMonitoring(AudioMixerMode.Output);
            service.UnregisterNotificationClient();
        }
    }

    private static bool EnsureDefaultRenderEndpointAvailable(bool required = false)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return !string.IsNullOrWhiteSpace(endpoint.ID);
        }
        catch (Exception ex)
        {
            string message = "A default render endpoint is required to create a real NAudio 3 WASAPI session.";
            if (required || TestExecutionGuards.ShouldRequireIntegrationHardware())
            {
                throw TestExecutionGuards.CreateRequiredIntegrationPrerequisiteException(
                    nameof(AudioDeviceSessionLifecycleIntegrationTests),
                    message,
                    ex);
            }

            return TestExecutionGuards.ReportOptionalIntegrationPrerequisite(
                nameof(AudioDeviceSessionLifecycleIntegrationTests),
                message);
        }
    }

    private static Task WaitForOutputSessionMonitoringReadyAsync(AudioDeviceService service)
    {
        return TestExecutionGuards.WaitUntilAsync(
            () => service.GetSessionMonitoringEndpointCountForTests(AudioMixerMode.Output) > 0,
            "The output session monitor did not attach to an active endpoint.",
            TimeSpan.FromSeconds(5));
    }

    private static async Task RunSilentSessionCycleAsync(
        Func<Task>? whileActive = null,
        string? endpointId = null,
        Func<Task>? afterStop = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice endpoint = string.IsNullOrWhiteSpace(endpointId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(endpointId);
        using WasapiPlayer player = new WasapiPlayerBuilder()
            .WithDevice(endpoint)
            .WithSharedMode()
            .WithLatency(50)
            .Build();

        player.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        player.Volume = 0f;
        player.Play();
        try
        {
            if (whileActive != null)
            {
                await whileActive();
            }
        }
        finally
        {
            player.Stop();
        }

        if (afterStop != null)
        {
            await afterStop();
        }
    }

    private static TimeSpan ResolveSoakDuration()
    {
        const int defaultMinutes = 30;
        string? rawMinutes = Environment.GetEnvironmentVariable(SoakMinutesEnvVar);
        if (string.IsNullOrWhiteSpace(rawMinutes))
        {
            return TimeSpan.FromMinutes(defaultMinutes);
        }

        if (!int.TryParse(rawMinutes, out int minutes) || minutes is < 1 or > 120)
        {
            throw new InvalidOperationException($"{SoakMinutesEnvVar} must be an integer from 1 through 120.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static ProcessResourceSnapshot CaptureResources()
    {
        _ = GC.GetTotalMemory(forceFullCollection: true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessResourceSnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            process.PrivateMemorySize64,
            process.HandleCount);
    }

    private readonly record struct ProcessResourceSnapshot(long ManagedBytes, long PrivateBytes, int HandleCount);
}
