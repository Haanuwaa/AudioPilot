using System.Diagnostics;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;


namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Name, TestCategories.Integration)]
[Collection("AudioHardwareStressIsolation")]
public sealed class AudioDeviceSessionLifecycleIntegrationTests
{

    [AudioHardwareFact]
    public async Task ManagedNotificationsAndSessionWrappers_ObserveRealSilentPlaybackSession()
    {
        if (!TestExecutionGuards.RequireAudioHardwareEnabled(nameof(ManagedNotificationsAndSessionWrappers_ObserveRealSilentPlaybackSession)) ||
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
            await RunSessionCycleAsync(async () =>
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

        TimeSpan duration = SoakResourceTracker.ResolveDuration();
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

            var sessionIds = endpointIds.ToDictionary(id => id, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < endpointIds.Length; index++)
            {
                int createdBefore = Volatile.Read(ref createdCount);
                await RunSessionCycleAsync(
                    () => TestExecutionGuards.WaitUntilAsync(
                        () => Volatile.Read(ref createdCount) > createdBefore,
                        $"Warm-up {index}: no session-created callback on endpoint {endpointIds[index]}.",
                        TimeSpan.FromSeconds(5)),
                    endpointId: endpointIds[index], sessionId: sessionIds[endpointIds[index]]);
            }

            var resources = new SoakResourceTracker();
            var monitor = TestPrivateAccess.GetField<SessionMonitorCoordinator>(service, "_playbackSessionMonitorCoordinator");
            resources.Sample(TimeSpan.Zero, monitor.GetMonitoredSessionCountForTests());
            var stopwatch = Stopwatch.StartNew();
            int cycleCount = 0;
            int maximumSnapshotCount = 0;
            var endpointsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (stopwatch.Elapsed < duration)
            {
                string endpointId = endpointIds[cycleCount % endpointIds.Length];
                endpointsUsed.Add(endpointId);
                int lifecycleBefore = Volatile.Read(ref lifecycleCount);
                await RunSessionCycleAsync(async () =>
                {
                    IReadOnlyList<AudioSessionSnapshot> snapshots = await service.GetAllAudioSessionSnapshotsAsync(
                        AudioMixerMode.Output,
                        recentSnapshotCacheWindowMs: 0);
                    maximumSnapshotCount = Math.Max(maximumSnapshotCount, snapshots.Count);
                    await TestExecutionGuards.WaitUntilAsync(
                        () => Volatile.Read(ref lifecycleCount) > lifecycleBefore,
                        "A reopened WASAPI stream did not produce a session lifecycle callback.",
                        TimeSpan.FromSeconds(5));
                }, endpointId, sessionId: sessionIds[endpointId]);
                cycleCount++;
                if (stopwatch.Elapsed >= resources.NextSampleAt) resources.Sample(stopwatch.Elapsed, monitor.GetMonitoredSessionCountForTests());

                if (cycleCount % 10 == 0)
                {
                    Assert.NotEmpty(service.GetActivePlaybackCycleEntries());
                    _ = service.GetActiveCaptureCycleEntries();
                }
            }

            resources.Sample(stopwatch.Elapsed, monitor.GetMonitoredSessionCountForTests());
            resources.AssertBounded($"playback cycles={cycleCount} maxSnapshots={maximumSnapshotCount}");
            Assert.True(cycleCount > 0, "The playback soak did not complete any session cycles.");
            Assert.Equal(endpointIds.Length, endpointsUsed.Count);
            Assert.True(Volatile.Read(ref createdCount) > 0);
            Assert.True(Volatile.Read(ref lifecycleCount) > 0);
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
    public async Task RecordingSessionSoak_ReleasesSessionsAndRestartsMonitoring_WithoutSustainedResourceGrowth()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? configured = Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_INPUT_DEVICE_ID");
        using MMDevice endpoint = string.IsNullOrWhiteSpace(configured)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(configured);
        Assert.Equal(DataFlow.Capture, endpoint.DataFlow);
        Assert.Equal(DeviceState.Active, endpoint.State);
        var reference = new AudioEndpointReference(endpoint.ID, endpoint.FriendlyName);
        var factory = new WasapiAudioEndpointTestSessionFactory();
        using var service = new AudioDeviceService();
        var monitor = TestPrivateAccess.GetField<SessionMonitorCoordinator>(service, "_recordingSessionMonitorCoordinator");
        int created = 0;
        int lifecycle = 0;
        service.AudioSessionCreated += mode => { if (mode == AudioMixerMode.Input) Interlocked.Increment(ref created); };
        service.AudioSessionLifecycleChanged += signal => { if (signal.MixerMode == AudioMixerMode.Input) Interlocked.Increment(ref lifecycle); };
        bool monitoring = false;
        int lifecycleAtRestart = 0;
        int cycles = 0;
        Guid sessionId = Guid.NewGuid();
        TimeSpan duration = SoakResourceTracker.ResolveDuration();
        var resources = new SoakResourceTracker();
        var elapsed = Stopwatch.StartNew();
        try
        {
            service.RegisterNotificationClient();
            while (elapsed.Elapsed < duration)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (!monitoring)
                {
                    lifecycleAtRestart = Volatile.Read(ref lifecycle);
                    service.AcquireSessionMonitoring(AudioMixerMode.Input);
                    monitoring = true;
                    await TestExecutionGuards.WaitUntilAsync(() => service.GetSessionMonitoringEndpointCountForTests(AudioMixerMode.Input) > 0,
                        "Recording monitor did not attach.", TimeSpan.FromSeconds(5));
                }

                await RunSessionCycleAsync(async () =>
                {
                    await using IAudioInputTestSession session = await factory.CreateInputAsync(reference, null, TestContext.Current.CancellationToken);
                    await TestExecutionGuards.WaitUntilAsync(() => session.ReadLevel().SampleRevision > 0 && Volatile.Read(ref created) > 0,
                        $"Recording cycle {cycles}: packets or session-created callback missing.", TimeSpan.FromSeconds(5));
                    _ = await service.GetAllAudioSessionSnapshotsAsync(AudioMixerMode.Input, recentSnapshotCacheWindowMs: 0);
                }, endpoint.ID, flow: DataFlow.Capture, sessionId: sessionId);
                cycles++;
                if (cycles >= 4 && elapsed.Elapsed >= resources.NextSampleAt)
                    resources.Sample(elapsed.Elapsed, monitor.GetMonitoredSessionCountForTests());
                if (cycles % 10 == 0)
                {
                    Assert.True(Volatile.Read(ref lifecycle) > lifecycleAtRestart,
                        $"No recording lifecycle events after monitoring restart at cycle {cycles - 10}.");
                    service.ReleaseSessionMonitoring(AudioMixerMode.Input);
                    monitoring = false;
                    await TestExecutionGuards.WaitUntilAsync(() => monitor.GetEndpointMonitorCountForTests() == 0 && monitor.GetMonitoredSessionCountForTests() == 0,
                        "Recording monitor retained registrations after release.", TimeSpan.FromSeconds(5));
                    Assert.Equal(0, service.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
                }
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            resources.Sample(elapsed.Elapsed, monitor.GetMonitoredSessionCountForTests());
            resources.AssertBounded($"recording cycles={cycles} created={created} lifecycle={lifecycle}");
            Assert.True(cycles >= 20, $"Only {cycles} recording cycles completed.");
            Assert.True(Volatile.Read(ref lifecycle) > 0, "No recording lifecycle events were observed.");
        }
        finally
        {
            if (monitoring) service.ReleaseSessionMonitoring(AudioMixerMode.Input);
            await TestExecutionGuards.WaitUntilAsync(() => monitor.GetEndpointMonitorCountForTests() == 0 && monitor.GetMonitoredSessionCountForTests() == 0,
                        "Recording monitor retained registrations after release.", TimeSpan.FromSeconds(5));
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

    /// <summary>
    /// Reuses an explicit session identity during soaks: Windows can retain inactive sessions until process exit.
    /// A fresh identity for each iteration would grow the native topology rather than measure a steady workload.
    /// </summary>
    private static async Task RunSessionCycleAsync(
        Func<Task>? whileActive = null,
        string? endpointId = null,
        Func<Task>? afterStop = null,
        DataFlow flow = DataFlow.Render,
        Guid? sessionId = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice endpoint = string.IsNullOrWhiteSpace(endpointId)
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
            : enumerator.GetDevice(endpointId);
        using AudioClient client = endpoint.CreateAudioClient();
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
            1_000_000, 0, client.MixFormat, sessionId ?? Guid.NewGuid());
        using AudioRenderClient? render = flow == DataFlow.Render ? client.AudioRenderClient : null;
        using AudioCaptureClient? capture = flow == DataFlow.Capture ? client.AudioCaptureClient : null;
        if (render != null)
        {
            _ = render.GetBuffer(client.BufferSize);
            render.ReleaseBuffer(client.BufferSize, AudioClientBufferFlags.Silent);
        }
        client.Start();
        try
        {
            if (capture != null)
            {
                await TestExecutionGuards.WaitUntilAsync(() => capture.GetNextPacketSize() > 0,
                    "No microphone packets arrived.", TimeSpan.FromSeconds(5));
                _ = capture.GetBuffer(out int frames, out _);
                capture.ReleaseBuffer(frames);
            }
            if (whileActive != null) await whileActive();
        }
        finally { client.Stop(); }
        if (afterStop != null) await afterStop();
    }
}
