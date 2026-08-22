using System.Reflection;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Internal;

public sealed class SessionMonitorCoordinatorTests
{
    [Fact]
    public void Update_DoesNotScheduleWork_WhenServiceIsDisposed()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-disposed.log");
        var scheduled = new List<Func<CancellationToken, Task>>();

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: static () => [],
            runBackgroundWork: (work, _) => scheduled.Add(work),
            isDisposed: static () => true);

        coordinator.Update();

        Assert.Empty(scheduled);
        coordinator.Stop();

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("session-monitor-update-skip | mode=Output reason=disposed", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_DropsStaleScheduledWork_WhenNewerUpdateArrives()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-stale.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        int endpointEnumerations = 0;

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () =>
            {
                endpointEnumerations++;
                return [];
            },
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        coordinator.Update();

        Assert.Equal(2, scheduled.Count);

        await scheduled[0](CancellationToken.None);
        Assert.Equal(0, endpointEnumerations);

        await scheduled[1](CancellationToken.None);
        Assert.Equal(1, endpointEnumerations);

        coordinator.Stop();
    }

    [Fact]
    public async Task ExistingSystemSoundsSession_PublishesLifecycleSignalsAsSharedRowAffecting()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-system-sounds.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var signals = new List<AudioSessionLifecycleSignal>();
        var session = new StubAudioSessionControl2("session-0", processId: 0);
        var endpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [session]);

        var coordinator = CreateCoordinator(
            loggerScope,
            mixerMode: AudioMixerMode.Output,
            getActiveEndpoints: () => [endpoint],
            onSessionLifecycleChanged: signal => signals.Add(signal),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        session.RaiseVolumeChanged(0.25f, isMuted: true);

        AudioSessionLifecycleSignal signal = Assert.Single(signals);
        Assert.Equal(AudioSessionLifecycleSignalKind.VolumeChanged, signal.Kind);
        Assert.True(signal.AffectsSharedRows);
        Assert.Equal(25f, signal.VolumePercent);
        Assert.True(signal.IsMuted);

        coordinator.Stop();
    }

    [Fact]
    public async Task Update_SkipsEndpointEnumeration_WhenShutdownTokenIsCancelled()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-cancelled.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        int endpointEnumerations = 0;

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () =>
            {
                endpointEnumerations++;
                return [];
            },
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        Func<CancellationToken, Task> scheduledUpdate = Assert.Single(scheduled);

        using var shutdownCts = new CancellationTokenSource();
        shutdownCts.Cancel();

        await scheduledUpdate(shutdownCts.Token);

        Assert.Equal(0, endpointEnumerations);
        coordinator.Stop();
    }

    [Fact]
    public async Task Update_AttachesAllActiveEndpoints_ForOutputAndInput()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-multi-endpoint.log");
        var outputScheduled = new List<Func<CancellationToken, Task>>();
        var inputScheduled = new List<Func<CancellationToken, Task>>();
        var outputEndpointVolumes = new List<(AudioMixerMode MixerMode, string EndpointId, float VolumePercent)>();
        var inputEndpointVolumes = new List<(AudioMixerMode MixerMode, string EndpointId, float VolumePercent)>();

        var outputPrimary = new FakeSessionMonitorEndpoint("render-primary", "Speakers");
        var outputSecondary = new FakeSessionMonitorEndpoint("render-secondary", "Headset");
        var inputPrimary = new FakeSessionMonitorEndpoint("capture-primary", "Mic");
        var inputSecondary = new FakeSessionMonitorEndpoint("capture-secondary", "USB Mic");

        var outputCoordinator = CreateCoordinator(
            loggerScope,
            mixerMode: AudioMixerMode.Output,
            getActiveEndpoints: () => [outputPrimary, outputSecondary],
            onEndpointVolumeChanged: (mixerMode, endpointId, volumePercent, _) => outputEndpointVolumes.Add((mixerMode, endpointId, volumePercent)),
            runBackgroundWork: (work, _) => outputScheduled.Add(work));

        var inputCoordinator = CreateCoordinator(
            loggerScope,
            mixerMode: AudioMixerMode.Input,
            getActiveEndpoints: () => [inputPrimary, inputSecondary],
            onEndpointVolumeChanged: (mixerMode, endpointId, volumePercent, _) => inputEndpointVolumes.Add((mixerMode, endpointId, volumePercent)),
            runBackgroundWork: (work, _) => inputScheduled.Add(work));

        outputCoordinator.Update();
        inputCoordinator.Update();

        await outputScheduled[0](CancellationToken.None);
        await inputScheduled[0](CancellationToken.None);

        Assert.Equal(1, outputPrimary.SessionCreatedSubscriberCount);
        Assert.Equal(1, outputSecondary.SessionCreatedSubscriberCount);
        Assert.Equal(1, inputPrimary.SessionCreatedSubscriberCount);
        Assert.Equal(1, inputSecondary.SessionCreatedSubscriberCount);

        outputSecondary.TriggerEndpointVolume(0.35f);
        inputSecondary.TriggerEndpointVolume(0.62f);

        var (outputMixerMode, outputEndpointId, outputVolumePercent) = Assert.Single(outputEndpointVolumes);
        Assert.Equal(AudioMixerMode.Output, outputMixerMode);
        Assert.Equal("render-secondary", outputEndpointId);
        Assert.Equal(35f, outputVolumePercent);

        var (inputMixerMode, inputEndpointId, inputVolumePercent) = Assert.Single(inputEndpointVolumes);
        Assert.Equal(AudioMixerMode.Input, inputMixerMode);
        Assert.Equal("capture-secondary", inputEndpointId);
        Assert.Equal(62f, inputVolumePercent);

        outputCoordinator.Stop();
        inputCoordinator.Stop();
    }

    [Fact]
    public async Task SessionCreated_FromSecondaryEndpoint_IsPublishedAndMonitored()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-session-created.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var createdCalls = new List<(AudioMixerMode MixerMode, object? Sender, ISessionMonitorSession Session)>();
        var signals = new List<AudioSessionLifecycleSignal>();
        var secondaryEndpoint = new FakeSessionMonitorEndpoint("render-secondary", "Headset");

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () => [secondaryEndpoint],
            onSessionCreated: (mixerMode, sender, session) => createdCalls.Add((mixerMode, sender, session)),
            onSessionLifecycleChanged: signal => signals.Add(signal),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        var createdSession = new StubAudioSessionControl2("session-created-secondary");
        secondaryEndpoint.TriggerSessionCreated(createdSession, sender: secondaryEndpoint);
        createdSession.RaiseStateChanged(AudioSessionState.AudioSessionStateActive);

        var (createdMixerMode, createdSender, createdControl) = Assert.Single(createdCalls);
        Assert.Equal(AudioMixerMode.Output, createdMixerMode);
        Assert.Same(secondaryEndpoint, createdSender);
        Assert.Same(createdSession, createdControl);

        AudioSessionLifecycleSignal signal = Assert.Single(signals);
        Assert.Equal(AudioSessionLifecycleSignalKind.StateChanged, signal.Kind);
        Assert.Equal("session-created-secondary", signal.SessionInstanceId);
        Assert.Equal("render-secondary", signal.EndpointId);

        coordinator.Stop();
    }

    [Fact]
    public async Task ExistingSessions_FromSecondaryEndpoint_PublishLifecycleSignalsWithEndpointIdentity()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-lifecycle-secondary.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var signals = new List<AudioSessionLifecycleSignal>();
        var session = new StubAudioSessionControl2("secondary-session");
        var endpoint = new FakeSessionMonitorEndpoint("capture-secondary", "USB Mic", [session]);

        var coordinator = CreateCoordinator(
            loggerScope,
            mixerMode: AudioMixerMode.Input,
            getActiveEndpoints: () => [endpoint],
            onSessionLifecycleChanged: signal => signals.Add(signal),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        session.RaiseVolumeChanged(0.42f, isMuted: false);
        session.RaiseDisconnected(AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval);

        Assert.Collection(
            signals,
            volume =>
            {
                Assert.Equal(AudioMixerMode.Input, volume.MixerMode);
                Assert.Equal(AudioSessionLifecycleSignalKind.VolumeChanged, volume.Kind);
                Assert.Equal("secondary-session", volume.SessionInstanceId);
                Assert.Equal("capture-secondary", volume.EndpointId);
                Assert.False(volume.AffectsSharedRows);
            },
            disconnected =>
            {
                Assert.Equal(AudioMixerMode.Input, disconnected.MixerMode);
                Assert.Equal(AudioSessionLifecycleSignalKind.Disconnected, disconnected.Kind);
                Assert.Equal("secondary-session", disconnected.SessionInstanceId);
                Assert.Equal("capture-secondary", disconnected.EndpointId);
                Assert.False(disconnected.AffectsSharedRows);
                Assert.Equal(AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval, disconnected.DisconnectReason);
            });

        coordinator.Stop();
    }

    [Fact]
    public async Task Reconcile_DetachesRemovedEndpoints_AndStopsCallbacks()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-detach.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var signals = new List<AudioSessionLifecycleSignal>();
        var retainedSession = new StubAudioSessionControl2("retained-session");
        var retainedEndpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [retainedSession]);
        var removedSession = new StubAudioSessionControl2("removed-session");
        var removedEndpoint = new FakeSessionMonitorEndpoint("render-secondary", "Headset", [removedSession]);
        int enumeration = 0;

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () =>
            {
                enumeration++;
                return enumeration switch
                {
                    1 => [retainedEndpoint, removedEndpoint],
                    _ => [retainedEndpoint],
                };
            },
            onSessionLifecycleChanged: signal => signals.Add(signal),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        coordinator.Update();
        await scheduled[1](CancellationToken.None);

        Assert.Equal(1, removedEndpoint.DisposeCount);
        Assert.Equal(1, removedSession.UnregisterCallCount);
        Assert.Equal(0, removedEndpoint.SessionCreatedSubscriberCount);
        removedEndpoint.TriggerEndpointVolume(0.4f);
        Assert.Empty(signals);

        retainedSession.RaiseStateChanged(AudioSessionState.AudioSessionStateActive);
        AudioSessionLifecycleSignal retainedSignal = Assert.Single(signals);
        Assert.Equal("render-primary", retainedSignal.EndpointId);

        coordinator.Stop();
    }

    [Fact]
    public async Task Reconcile_IsIdempotent_WhenEndpointSetIsUnchanged()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-idempotent.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var session = new StubAudioSessionControl2("stable-session");
        var firstEndpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [session]);
        var redundantEndpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [new StubAudioSessionControl2("stable-session")]);

        int enumeration = 0;
        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () =>
            {
                enumeration++;
                return enumeration switch
                {
                    1 => [firstEndpoint],
                    _ => [redundantEndpoint],
                };
            },
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        coordinator.Update();
        await scheduled[1](CancellationToken.None);

        Assert.Equal(1, session.RegisterCallCount);
        Assert.Equal(0, session.UnregisterCallCount);
        Assert.Equal(1, redundantEndpoint.DisposeCount);

        coordinator.Stop();
    }

    [Fact]
    public async Task DuplicateSessionIdsAcrossEndpoints_DoNotCollide()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-duplicate-session-id.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var signals = new List<AudioSessionLifecycleSignal>();
        var outputSession = new StubAudioSessionControl2("shared-session");
        var inputSession = new StubAudioSessionControl2("shared-session");
        var outputEndpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [outputSession]);
        var inputEndpoint = new FakeSessionMonitorEndpoint("render-secondary", "Headset", [inputSession]);

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () => [outputEndpoint, inputEndpoint],
            onSessionLifecycleChanged: signal => signals.Add(signal),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        outputSession.RaiseStateChanged(AudioSessionState.AudioSessionStateActive);
        inputSession.RaiseStateChanged(AudioSessionState.AudioSessionStateActive);

        Assert.Collection(
            signals,
            first =>
            {
                Assert.Equal("shared-session", first.SessionInstanceId);
                Assert.Equal("render-primary", first.EndpointId);
            },
            second =>
            {
                Assert.Equal("shared-session", second.SessionInstanceId);
                Assert.Equal("render-secondary", second.EndpointId);
            });

        coordinator.Stop();
    }

    [Fact]
    public async Task Stop_ReturnsPromptly_WhenEndpointEnumerationIsBlocked()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-stop-blocked-lookup.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        using var enumerationStarted = new ManualResetEventSlim(false);
        using var allowEnumerationToFinish = new ManualResetEventSlim(false);

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () =>
            {
                enumerationStarted.Set();
                Assert.True(allowEnumerationToFinish.Wait(TimeSpan.FromSeconds(15), testCancellationToken));
                return [];
            },
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        Func<CancellationToken, Task> scheduledUpdate = Assert.Single(scheduled);

        Task updateTask = Task.Factory.StartNew(
            () => scheduledUpdate(testCancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        try
        {
            Assert.True(enumerationStarted.Wait(TimeSpan.FromSeconds(10), testCancellationToken));

            Task stopTask = Task.Factory.StartNew(
                coordinator.Stop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2), testCancellationToken);
        }
        finally
        {
            allowEnumerationToFinish.Set();
            await updateTask.WaitAsync(TimeSpan.FromSeconds(10), testCancellationToken);
        }
    }

    [Fact]
    public async Task Stop_ReleasesLockBeforeBlockingSessionDispose()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-stop-blocking-dispose.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        using var unregisterStarted = new ManualResetEventSlim(false);
        using var allowUnregisterToFinish = new ManualResetEventSlim(false);
        var session = new StubAudioSessionControl2(
            "session-blocked-stop",
            onUnregister: () =>
            {
                unregisterStarted.Set();
                Assert.True(allowUnregisterToFinish.Wait(TimeSpan.FromSeconds(5)));
            });
        var endpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [session]);

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () => [endpoint],
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        Task stopTask = Task.Run(coordinator.Stop, TestContext.Current.CancellationToken);
        Assert.True(unregisterStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        endpoint.TriggerEndpointVolume(0.75f);

        allowUnregisterToFinish.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAndDrainAsync_WaitsForBlockingSessionDispose()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-stop-drain.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        using var unregisterStarted = new ManualResetEventSlim(false);
        using var allowUnregisterToFinish = new ManualResetEventSlim(false);
        var session = new StubAudioSessionControl2(
            "session-blocked-drain",
            onUnregister: () =>
            {
                unregisterStarted.Set();
                Assert.True(allowUnregisterToFinish.Wait(TimeSpan.FromSeconds(5)));
            });
        var endpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [session]);

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () => [endpoint],
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        Task drainTask = coordinator.StopAndDrainAsync();
        Assert.True(unregisterStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(drainTask.IsCompleted);

        allowUnregisterToFinish.Set();
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("session-monitor-stop-drain-start | mode=Output endpoints=1", logText, StringComparison.Ordinal);
        Assert.Contains("session-monitor-stop-drain-complete | mode=Output endpoints=1", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopThenRestart_WithReusedEndpointAndSessionId_IgnoresStaleSessionAndEndpointCallbacks()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-stale-overlap.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var lifecycleSignals = new List<AudioSessionLifecycleSignal>();
        var endpointVolumes = new List<(AudioMixerMode MixerMode, string EndpointId, float VolumePercent)>();
        var unregisterStarted = new ManualResetEventSlim(false);
        var allowUnregisterToFinish = new ManualResetEventSlim(false);
        var staleSession = new StubAudioSessionControl2(
            "shared-session",
            onUnregister: () =>
            {
                unregisterStarted.Set();
                Assert.True(allowUnregisterToFinish.Wait(TimeSpan.FromSeconds(5)));
            });
        var replacementSession = new StubAudioSessionControl2("shared-session");
        FakeSessionMonitorEndpoint currentEndpoint = new("render-primary", "Speakers", [staleSession]);

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () => [currentEndpoint],
            onEndpointVolumeChanged: (mixerMode, endpointId, volumePercent, _) => endpointVolumes.Add((mixerMode, endpointId, volumePercent)),
            onSessionLifecycleChanged: signal => lifecycleSignals.Add(signal),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        long staleMonitorInstanceId = GetInstalledMonitorInstanceId(coordinator, "render-primary");

        coordinator.Stop();
        Assert.True(unregisterStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        currentEndpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers", [replacementSession]);
        coordinator.Update();
        await scheduled[1](CancellationToken.None);

        long replacementMonitorInstanceId = GetInstalledMonitorInstanceId(coordinator, "render-primary");
        Assert.NotEqual(staleMonitorInstanceId, replacementMonitorInstanceId);

        staleSession.RaiseDisconnected(AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval);
        staleSession.RaiseStateChanged(AudioSessionState.AudioSessionStateActive);
        staleSession.RaiseVolumeChanged(0.42f, isMuted: false);
        InvokePrivateEndpointVolumeNotification(coordinator, "render-primary", staleMonitorInstanceId, 0.55f);

        Assert.Empty(lifecycleSignals);
        Assert.Empty(endpointVolumes);

        replacementSession.RaiseStateChanged(AudioSessionState.AudioSessionStateActive);
        replacementSession.RaiseVolumeChanged(0.61f, isMuted: false);
        currentEndpoint.TriggerEndpointVolume(0.35f);

        Assert.Collection(
            lifecycleSignals,
            stateChanged =>
            {
                Assert.Equal(AudioSessionLifecycleSignalKind.StateChanged, stateChanged.Kind);
                Assert.Equal("shared-session", stateChanged.SessionInstanceId);
                Assert.Equal("render-primary", stateChanged.EndpointId);
            },
            volumeChanged =>
            {
                Assert.Equal(AudioSessionLifecycleSignalKind.VolumeChanged, volumeChanged.Kind);
                Assert.Equal("shared-session", volumeChanged.SessionInstanceId);
                Assert.Equal("render-primary", volumeChanged.EndpointId);
            });

        var (mixerMode, endpointId, volumePercent) = Assert.Single(endpointVolumes);
        Assert.Equal(AudioMixerMode.Output, mixerMode);
        Assert.Equal("render-primary", endpointId);
        Assert.Equal(35f, volumePercent);

        allowUnregisterToFinish.Set();
        await coordinator.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopThenRestart_WithReusedEndpointId_IgnoresStaleSessionCreatedCallbacks()
    {
        using var loggerScope = new TestLoggerScope(nameof(SessionMonitorCoordinatorTests), "session-monitor-stale-created.log");
        var scheduled = new List<Func<CancellationToken, Task>>();
        var createdCalls = new List<(AudioMixerMode MixerMode, object? Sender, ISessionMonitorSession Session)>();
        var unregisterStarted = new ManualResetEventSlim(false);
        var allowUnregisterToFinish = new ManualResetEventSlim(false);
        var blockingSession = new StubAudioSessionControl2(
            "shared-session",
            onUnregister: () =>
            {
                unregisterStarted.Set();
                Assert.True(allowUnregisterToFinish.Wait(TimeSpan.FromSeconds(5)));
            });
        FakeSessionMonitorEndpoint currentEndpoint = new("render-primary", "Speakers", [blockingSession]);

        var coordinator = CreateCoordinator(
            loggerScope,
            getActiveEndpoints: () => [currentEndpoint],
            onSessionCreated: (mixerMode, sender, session) => createdCalls.Add((mixerMode, sender, session)),
            runBackgroundWork: (work, _) => scheduled.Add(work));

        coordinator.Update();
        await scheduled[0](CancellationToken.None);

        long staleMonitorInstanceId = GetInstalledMonitorInstanceId(coordinator, "render-primary");

        coordinator.Stop();
        Assert.True(unregisterStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var replacementEndpoint = new FakeSessionMonitorEndpoint("render-primary", "Speakers");
        currentEndpoint = replacementEndpoint;
        coordinator.Update();
        await scheduled[1](CancellationToken.None);

        long replacementMonitorInstanceId = GetInstalledMonitorInstanceId(coordinator, "render-primary");
        Assert.NotEqual(staleMonitorInstanceId, replacementMonitorInstanceId);

        var staleCreatedSession = new StubAudioSessionControl2("new-session");
        InvokePrivateSessionCreated(coordinator, "render-primary", staleMonitorInstanceId, replacementEndpoint, staleCreatedSession);

        Assert.Empty(createdCalls);

        var replacementCreatedSession = new StubAudioSessionControl2("new-session");
        replacementEndpoint.TriggerSessionCreated(replacementCreatedSession, sender: replacementEndpoint);

        var (createdMixerMode, createdSender, createdSession) = Assert.Single(createdCalls);
        Assert.Equal(AudioMixerMode.Output, createdMixerMode);
        Assert.Same(replacementEndpoint, createdSender);
        Assert.Same(replacementCreatedSession, createdSession);

        allowUnregisterToFinish.Set();
        await coordinator.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static SessionMonitorCoordinator CreateCoordinator(
        TestLoggerScope loggerScope,
        Func<IReadOnlyList<ISessionMonitorEndpoint>> getActiveEndpoints,
        Action<AudioMixerMode, object?, ISessionMonitorSession>? onSessionCreated = null,
        Action<AudioMixerMode, string, float, bool>? onEndpointVolumeChanged = null,
        Action<AudioSessionLifecycleSignal>? onSessionLifecycleChanged = null,
        Action<Func<CancellationToken, Task>, string>? runBackgroundWork = null,
        Func<bool>? isDisposed = null,
        AudioMixerMode mixerMode = AudioMixerMode.Output)
    {
        Action<AudioMixerMode, object?, ISessionMonitorSession> sessionCreatedHandler = onSessionCreated ?? ((_, _, _) => { });
        Action<AudioMixerMode, string, float, bool> endpointVolumeHandler = onEndpointVolumeChanged ?? ((_, _, _, _) => { });
        Action<AudioSessionLifecycleSignal> lifecycleHandler = onSessionLifecycleChanged ?? (_ => { });
        Action<Func<CancellationToken, Task>, string> backgroundWorkHandler = runBackgroundWork ?? ((_, _) => { });
        Func<bool> disposedEvaluator = isDisposed ?? (() => false);

        return new SessionMonitorCoordinator(
            loggerScope.Logger,
            mixerMode,
            getActiveEndpoints,
            sessionCreatedHandler,
            endpointVolumeHandler,
            lifecycleHandler,
            backgroundWorkHandler,
            disposedEvaluator);
    }

    private static long GetInstalledMonitorInstanceId(SessionMonitorCoordinator coordinator, string endpointId)
    {
        FieldInfo? field = typeof(SessionMonitorCoordinator).GetField("_endpointMonitors", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        object rawDictionary = field!.GetValue(coordinator) ?? throw new InvalidOperationException("Missing endpoint monitor dictionary.");
        var endpointMonitors = Assert.IsType<System.Collections.IDictionary>(rawDictionary, exactMatch: false);
        object endpointState = endpointMonitors[endpointId] ?? throw new InvalidOperationException("Missing endpoint state.");
        return (long)(endpointState.GetType().GetProperty("InstanceId")!.GetValue(endpointState) ?? 0L);
    }

    private static void InvokePrivateSessionCreated(SessionMonitorCoordinator coordinator, string endpointId, long monitorInstanceId, object? sender, ISessionMonitorSession session)
    {
        MethodInfo? method = typeof(SessionMonitorCoordinator).GetMethod("HandleSessionCreated", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(long), typeof(object), typeof(ISessionMonitorSession)], null);
        Assert.NotNull(method);
        _ = method!.Invoke(coordinator, [endpointId, monitorInstanceId, sender, session]);
    }

    private static void InvokePrivateEndpointVolumeNotification(SessionMonitorCoordinator coordinator, string endpointId, long monitorInstanceId, float masterVolume)
    {
        MethodInfo? method = typeof(SessionMonitorCoordinator).GetMethod("HandleEndpointVolumeNotification", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(long), typeof(AudioVolumeNotificationData)], null);
        Assert.NotNull(method);
        AudioVolumeNotificationData notification = new(Guid.Empty, muted: false, masterVolume, [], Guid.Empty);
        _ = method!.Invoke(coordinator, [endpointId, monitorInstanceId, notification]);
    }

    private sealed class FakeSessionMonitorEndpoint(
        string endpointId,
        string displayName,
        IReadOnlyList<StubAudioSessionControl2>? existingSessions = null) : ISessionMonitorEndpoint
    {
        private readonly List<SessionMonitorSessionCreatedDelegate> _sessionCreatedHandlers = [];
        private readonly List<AudioEndpointVolumeNotificationDelegate> _endpointVolumeHandlers = [];
        private readonly IReadOnlyList<StubAudioSessionControl2> _existingSessions = existingSessions ?? [];
        private bool _disposed;

        public string EndpointId { get; } = endpointId;
        public string DisplayName { get; } = displayName;
        public int DisposeCount { get; private set; }
        public int SessionCreatedSubscriberCount => _sessionCreatedHandlers.Count;
        public int EndpointVolumeSubscriberCount => _endpointVolumeHandlers.Count;

        public IReadOnlyList<ISessionMonitorSession> GetExistingSessions()
        {
            return _existingSessions;
        }

        public void SubscribeSessionCreated(SessionMonitorSessionCreatedDelegate handler)
        {
            _sessionCreatedHandlers.Add(handler);
        }

        public void UnsubscribeSessionCreated(SessionMonitorSessionCreatedDelegate handler)
        {
            _sessionCreatedHandlers.Remove(handler);
        }

        public void SubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler)
        {
            _endpointVolumeHandlers.Add(handler);
        }

        public void UnsubscribeEndpointVolume(AudioEndpointVolumeNotificationDelegate handler)
        {
            _endpointVolumeHandlers.Remove(handler);
        }

        public void TriggerSessionCreated(StubAudioSessionControl2 session, object? sender = null)
        {
            foreach (SessionMonitorSessionCreatedDelegate handler in _sessionCreatedHandlers.ToArray())
            {
                handler(sender, session);
            }
        }

        public void TriggerEndpointVolume(float masterVolume)
        {
            AudioVolumeNotificationData notification = new(Guid.Empty, muted: false, masterVolume, [], Guid.Empty);
            foreach (AudioEndpointVolumeNotificationDelegate handler in _endpointVolumeHandlers.ToArray())
            {
                handler(notification);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCount++;
            _sessionCreatedHandlers.Clear();
            _endpointVolumeHandlers.Clear();
        }
    }

    private sealed class StubAudioSessionControl2(string sessionInstanceId, Action? onUnregister = null, uint processId = 42) : ISessionMonitorSession
    {
        private sealed class FakeLease(uint processId, string displayName) : ISessionMonitorSessionLease
        {
            public AudioSessionState State => AudioSessionState.AudioSessionStateInactive;
            public uint ProcessId { get; } = processId;
            public string DisplayName { get; } = displayName;
            public void UseNativeControl(Action<AudioSessionControl> action) => throw new NotSupportedException();
            public void Dispose() { }
        }

        private IAudioSessionEventsHandler? _events;
        private readonly string _sessionInstanceId = sessionInstanceId;
        private readonly Action? _onUnregister = onUnregister;
        private readonly uint _processId = processId;

        public int RegisterCallCount { get; private set; }
        public int UnregisterCallCount { get; private set; }

        public void RaiseStateChanged(AudioSessionState state)
        {
            Assert.NotNull(_events);
            _events!.OnStateChanged(state);
        }

        public void RaiseDisconnected(AudioSessionDisconnectReason reason)
        {
            Assert.NotNull(_events);
            _events!.OnSessionDisconnected(reason);
        }

        public void RaiseVolumeChanged(float volume, bool isMuted)
        {
            Assert.NotNull(_events);
            _events!.OnVolumeChanged(volume, isMuted);
        }

        public string SessionInstanceId => _sessionInstanceId;
        public string SessionId => _sessionInstanceId + "-identifier";
        public uint ProcessId => _processId;
        public string DisplayName => string.Empty;

        public void RegisterEventClient(IAudioSessionEventsHandler newNotifications)
        {
            RegisterCallCount++;
            _events = newNotifications;
        }

        public void UnregisterEventClient(IAudioSessionEventsHandler newNotifications)
        {
            UnregisterCallCount++;
            _onUnregister?.Invoke();
            if (ReferenceEquals(_events, newNotifications))
            {
                _events = null;
            }
        }

        public ISessionMonitorSessionLease TryAcquireLease() => new FakeLease(_processId, DisplayName);
        public void Dispose() { }
    }
}
