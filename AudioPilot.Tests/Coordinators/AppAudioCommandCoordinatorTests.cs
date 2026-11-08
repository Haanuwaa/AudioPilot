using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppAudioCommandCoordinatorTests
{
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 0)]
    public void StepEndpointVolume_IncreasesLevelWithoutWritingUnmute(bool muted, bool driverClearsMute, int expectedMuteWrites)
    {
        var endpoint = (AudioEndpointVolume)RuntimeHelpers.GetUninitializedObject(typeof(AudioEndpointVolume));
        FieldInfo nativeField = typeof(AudioEndpointVolume).GetField("audioEndPointVolume", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var native = (EndpointVolumeProxy)DispatchProxy.Create(nativeField.FieldType, typeof(EndpointVolumeProxy));
        native.Muted = muted;
        native.DriverClearsMute = driverClearsMute;
        nativeField.SetValue(endpoint, native);

        var (percent, appliedMuted) = EndpointVolumeOperations.StepEndpointVolume(endpoint, 5, increase: true);

        Assert.Equal(45f, percent, 3);
        Assert.Equal(muted, appliedMuted);
        Assert.Equal(muted, native.Muted);
        Assert.Equal(expectedMuteWrites, native.MuteWrites);
    }

    public class EndpointVolumeProxy : DispatchProxy
    {
        public float Level = 0.4f;
        public bool Muted;
        public bool DriverClearsMute;
        public int MuteWrites;

        protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "GetMasterVolumeLevelScalar": args![0] = Level; break;
                case "SetMasterVolumeLevelScalar":
                    Level = (float)args![0]!;
                    if (DriverClearsMute) Muted = false;
                    break;
                case "GetMute": args![0] = Muted; break;
                case "SetMute": Muted = (bool)args![0]!; MuteWrites++; break;
                default: throw new InvalidOperationException(targetMethod?.Name);
            }
            return 0;
        }
    }

    [Theory]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Currently playing")]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, "Currently paused")]
    [InlineData(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped, "Play/pause command sent")]
    public async Task RepeatedPlayPause_DeferredFeedbackDescribesStateWithoutClaimingATransition(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status, string expectedHeader)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailing = new TaskCompletionSource<ExecutionHistoryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new MediaOverlaySessionSnapshot(status, "Unchanged track", "Artist", null, "Player", 30);
        bool held = false;
        int sends = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, sequence, token) =>
            {
                if (sequence > 0 && !held) { held = true; entered.TrySetResult(); await release.Task.WaitAsync(token); }
                return snapshot;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot> { ["Player"] = snapshot }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { snapshot }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile(maxCaptureDurationMs: 30_000));
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var coordinator = new AppAudioCommandCoordinator(null!, overlay, new MediaOverlayCommandService(engine), Logger.Instance, () => new Settings(),
            mediaPlayPauseCommandAsync: (_) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(true, MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc,
                    CandidateSourceAppUserModelId: "Player"));
            },
            mediaHistoryRecorder: entry => { if (entry.DiagCode == "media-overlay-trailing-track") trailing.TrySetResult(entry); });
        try
        {
            coordinator.MediaPlayPause();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            coordinator.MediaPlayPause();
            release.TrySetResult();
            ExecutionHistoryEntry entry = await trailing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, sends);
            Assert.Contains("command sent", entry.Summary);
            Assert.Equal(expectedHeader, Assert.Single(presenter.MediaMessages).header);
        }
        finally { release.TrySetResult(); await coordinator.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("toggle", true)]
    [InlineData("off", true)]
    [InlineData("on", false)]
    [InlineData("toggle", false)]
    [InlineData("off", false)]
    public async Task ListenFeedback_UsesAffectedDevicesWithoutRereadingChangingDefaults(string command, bool verified)
    {
        bool enabled = command != "off";
        var resolver = new FakeInputListenAudioDeviceResolver();
        resolver.DefaultRecordingEndpoints.Enqueue(new FakeAudioEndpointInfo("mic-a", "Microphone A"));
        resolver.DefaultRecordingEndpoints.Enqueue(new FakeAudioEndpointInfo("mic-b", "Microphone B"));
        resolver.DefaultPlaybackEndpoints.Enqueue(new FakeAudioEndpointInfo("headset", "Headphones"));
        resolver.DefaultPlaybackEndpoints.Enqueue(new FakeAudioEndpointInfo("speakers", "Speakers"));
        var reader = new QueueInputListenPropertyReader();
        if (command == "toggle") reader.ListenEnabledResults.Enqueue((true, false, null));
        reader.ListenEnabledResults.Enqueue((verified, enabled, null));
        reader.RenderTargetResults.Enqueue((true, enabled ? "headset" : null, null));
        var writer = new FakeInputListenPropertyWriter();
        using var audio = new AudioDeviceService(writer, reader, resolver);
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var coordinator = new AppAudioCommandCoordinator(audio, overlay, new MediaOverlayCommandService(), Logger.Instance, () => new Settings());
        try
        {
            bool success = command == "toggle" ? coordinator.ToggleListenToInput() : coordinator.SetListenToInput(enabled);

            Assert.True(success);
            Assert.Equal("mic-a", writer.LastInputDeviceId);
            var (_, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(AppAudioCommandCoordinator.GetListenToInputOverlayHeader(enabled, verified), header);
            Assert.Equal(enabled ? "Microphone A\nTo: Headphones" : "Microphone A", deviceName);
            Assert.Equal(1, resolver.GetDefaultRecordingEndpointCalls);
            Assert.Equal(enabled ? 1 : 0, resolver.GetDefaultPlaybackEndpointCalls);
            Assert.Equal(command == "toggle" ? 2 : 1, reader.ListenEnabledCalls);
            Assert.Equal(1, reader.RenderTargetCalls);
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenUnavailablePreferredOutput_ReportsFailureWithoutEnablingDefaultSpeakers(bool toggle)
    {
        var resolver = new FakeInputListenAudioDeviceResolver
        {
            DefaultRecordingEndpoint = new FakeAudioEndpointInfo("mic", "Microphone"),
            DefaultPlaybackEndpoint = new FakeAudioEndpointInfo("speakers", "Speakers"),
        };
        var writer = new FakeInputListenPropertyWriter();
        using var audio = new AudioDeviceService(writer, new QueueInputListenPropertyReader(), resolver);
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var settings = new Settings();
        settings.Hotkeys.Listen.MonitorOutputDeviceId = "missing-headset";
        settings.Hotkeys.Listen.MonitorOutputDeviceName = "Headphones";
        var coordinator = new AppAudioCommandCoordinator(audio, overlay, new MediaOverlayCommandService(), Logger.Instance, () => settings);
        try
        {
            Assert.False(toggle ? coordinator.ToggleListenToInput() : coordinator.SetListenToInput(true));
            Assert.Equal(0, writer.Calls);
            Assert.Equal(0, resolver.GetDefaultPlaybackEndpointCalls);
            var (kind, header, deviceName) = Assert.Single(presenter.Messages);
            Assert.Equal(OverlayDeviceKind.Error, kind);
            Assert.Equal("Monitor output unavailable", header);
            Assert.Contains("Default output", deviceName);
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Fact]
    public async Task MediaCommand_WithUnconfirmedDelivery_ShowsUnknownResultAndRecordsUncertainty()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        AppAudioCommandCoordinator coordinator = CreateCoordinator(presenter, new MediaOverlayCommandService(engine),
            mediaHistoryRecorder: history.Enqueue,
            mediaNextTrackCommandAsync: (_) => Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                false, MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc, SuppressFallback: true, FailureReason: "gsmc-command-timeout")));
        try
        {
            coordinator.MediaNextTrack();
            await AssertEventuallyAsync(() => !history.IsEmpty);

            ExecutionHistoryEntry entry = Assert.Single(history);
            Assert.False(entry.Success);
            Assert.Equal("media-command-unconfirmed", entry.DiagCode);
            Assert.Contains("unknown", entry.Summary);
            Assert.Contains("unknown", entry.Reason);
            Assert.Contains(presenter.Messages, message => message.header == "Media command result unknown");
        }
        finally
        {
            await coordinator.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData("mic", true, OverlayActionStateKind.Disabled, "Microphone muted")]
    [InlineData("sound", false, OverlayActionStateKind.Enabled, "Sound unmuted")]
    [InlineData("deafen", true, OverlayActionStateKind.Disabled, "Deafened")]
    public void CompletedMuteResult_ShowsConfirmedState(string target, bool enabled, OverlayActionStateKind expectedState, string expectedMessage)
    {
        var presenter = new RecordingOverlayPresenter();
        var coordinator = CreateCoordinator(presenter);
        coordinator.ShowMuteResult(target, enabled, succeeded: true);
        var (state, message) = Assert.Single(presenter.ActionMessages);
        Assert.Equal(expectedState, state);
        Assert.Equal(expectedMessage, message);
    }

    [Theory]
    [InlineData(false, "Could not change mute state")]
    [InlineData(true, "Mute change partially applied")]
    public void IncompleteMuteResult_DoesNotShowSuccess(bool partial, string expected)
    {
        var presenter = new RecordingOverlayPresenter();
        var coordinator = CreateCoordinator(presenter);
        coordinator.ShowMuteResult("deafen", true, succeeded: false, partial);
        Assert.Empty(presenter.ActionMessages);
        Assert.Contains(expected, Assert.Single(presenter.Messages).header);
    }

    [Theory]
    [InlineData(true, "Input listen enabled")]
    [InlineData(false, "Input listen disabled")]
    public void GetListenToInputOverlayHeader_ReturnsExpectedHeader(bool enabled, string expected)
    {
        string header = AppAudioCommandCoordinator.GetListenToInputOverlayHeader(enabled);

        Assert.Equal(expected, header);
    }

    [Fact]
    public void GetListenToInputOverlayHeader_IdentifiesUnverifiedAppliedState()
    {
        string header = AppAudioCommandCoordinator.GetListenToInputOverlayHeader(enabled: true, verified: false);

        Assert.Equal("Input listen enabled (verification pending)", header);
    }

    [Theory]
    [InlineData(null, "Input device")]
    [InlineData("", "Input device")]
    [InlineData("   ", "Input device")]
    [InlineData("Desk Mic", "Desk Mic")]
    public void NormalizeListenToInputOverlayDeviceName_ReturnsExpectedName(string? friendlyName, string expected)
    {
        string normalized = AppAudioCommandCoordinator.NormalizeListenToInputOverlayDeviceName(friendlyName);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ComposeListenToInputOverlayDeviceText_WhenEnabled_IncludesMonitorTarget()
    {
        string text = AppAudioCommandCoordinator.ComposeListenToInputOverlayDeviceText(
            enabled: true,
            inputDeviceName: "Desk Mic",
            monitorTargetOutputDeviceName: "Headphones");

        Assert.Equal("Desk Mic\nTo: Headphones", text);
    }

    [Fact]
    public void ComposeListenToInputOverlayDeviceText_WhenTargetNameUnavailable_DoesNotClaimDefaultRouting()
    {
        string text = AppAudioCommandCoordinator.ComposeListenToInputOverlayDeviceText(
            enabled: true,
            inputDeviceName: "Desk Mic",
            monitorTargetOutputDeviceName: null);

        Assert.Equal("Desk Mic\nTo: Output device", text);
    }

    [Fact]
    public void ComposeListenToInputOverlayDeviceText_WhenDisabled_ReturnsInputNameOnly()
    {
        string text = AppAudioCommandCoordinator.ComposeListenToInputOverlayDeviceText(
            enabled: false,
            inputDeviceName: "Desk Mic",
            monitorTargetOutputDeviceName: "Headphones");

        Assert.Equal("Desk Mic", text);
    }

    [Theory]
    [InlineData(40f, 5, true, 45f)]
    [InlineData(40f, 5, false, 35f)]
    [InlineData(98f, 5, true, 100f)]
    [InlineData(2f, 5, false, 0f)]
    [InlineData(50f, 0, true, 55f)]
    public void ComputeSteppedVolumePercent_ReturnsExpectedValue(float currentPercent, int stepPercent, bool increase, float expected)
    {
        float updated = EndpointVolumeOperations.ComputeSteppedVolumePercent(currentPercent, stepPercent, increase);

        Assert.Equal(expected, updated);
    }

    [Theory]
    [InlineData("Master volume", 72.2f, "Master volume 72%")]
    [InlineData("Microphone volume", 48.8f, "Microphone volume 49%")]
    public void BuildVolumeOverlayMessage_ReturnsExpectedMessage(string label, float percent, string expected)
    {
        string message = AppAudioCommandCoordinator.BuildVolumeOverlayMessage(label, percent);

        Assert.Equal(expected, message);
    }

    [Theory]
    [InlineData(true, OverlayActionStateKind.Enabled)]
    [InlineData(false, OverlayActionStateKind.Disabled)]
    public void GetVolumeOverlayStateKind_ReturnsExpectedState(bool increase, OverlayActionStateKind expected)
    {
        OverlayActionStateKind stateKind = AppAudioCommandCoordinator.GetVolumeOverlayStateKind(increase);

        Assert.Equal(expected, stateKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MediaNextTrack_WhenCaptureAlreadyInFlight_SendsCommandAndSchedulesTrailingCapture(bool knownTarget, bool targetDisappears)
    {
        var clock = new MediaOverlayTestHarness.VirtualClock(DateTimeOffset.UtcNow);
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int captureCount = 0;
        int snapshotCallCount = 0;
        int sendCount = 0;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (preferredSource, _, token) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                if (currentCall == 1)
                {
                    Interlocked.Increment(ref captureCount);
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42);
                }

                if (knownTarget && preferredSource != "spotify")
                {
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Unrelated Twitch stream", "Streamer", null, "floorp", 0);
                }

                if (targetDisappears)
                {
                    return MediaOverlaySessionSnapshot.Empty;
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track B",
                    "Artist B",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            eventWaitOverride: (_, delayMs, _, _) =>
            {
                clock.Advance(delayMs);
                return Task.FromResult(new MediaEventAssistOutcome(false, null));
            }, utcNow: clock.UtcNow);

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                    true, MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc,
                    CandidateSourceAppUserModelId: knownTarget ? "spotify" : null));
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        coordinator.MediaNextTrack();

        Assert.Equal(1, Volatile.Read(ref captureCount));
        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests && Volatile.Read(ref sendCount) == 2,
            GetMediaOverlayCaptureTimeout());

        await AssertEventuallyAsync(
            () => (targetDisappears || presenter.MediaUpdateCount >= 1) && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.DoesNotContain(presenter.Messages, message => string.Equals(message.deviceName, "Unrelated Twitch stream", StringComparison.Ordinal));
        if (targetDisappears)
        {
            Assert.Equal(0, presenter.MediaUpdateCount);
        }
        else
        {
            Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));
        }
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal(targetDisappears ? "media-overlay-trailing-no-metadata" : "media-overlay-trailing-track", entry.DiagCode);
    }

    [Fact]
    public async Task MediaNextTrack_UsesAsyncCommandDelegate_WhenProvided()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool commandCompleted = false;
        int snapshotCallCount = 0;

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult(currentCall == 1
                    ? new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42)
                    : new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1));
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: async (_) =>
            {
                commandStarted.TrySetResult();
                await completeCommand.Task;
                commandCompleted = true;
                return MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true);
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await commandStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);

        Assert.False(commandCompleted);
        Assert.Equal(0, presenter.ShowCount);

        completeCommand.TrySetResult();

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.True(commandCompleted);
        Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));

        await AssertEventuallyAsync(
            () => history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.True(entry.Success);
        Assert.False(entry.Skipped);
        Assert.NotNull(entry.ElapsedMs);
        Assert.Equal("NextTrack", entry.Details?["command"]);
        Assert.Equal("changed", entry.Details?["outcome"]);
        Assert.False(string.IsNullOrWhiteSpace(entry.Details?["finalPhase"]));
        Assert.Equal("track-changed", entry.Details?["finalChangeKind"]);
    }

    [Theory]
    [InlineData("hotkey")]
    [InlineData("taskbar")]
    public async Task MediaNextTrack_RecordsCallerSourceAndDetailedHistory(string source)
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult(currentCall == 1
                    ? new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42)
                    : new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1));
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) => Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack(source);

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal(source, entry.Source);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.Equal(source, entry.Details?["source"]);
        Assert.Equal("changed", entry.Details?["outcome"]);
        Assert.False(string.IsNullOrWhiteSpace(entry.Details?["finalPhase"]));
    }

    [Fact]
    public async Task MediaNextTrack_DetailedCommandOutcome_RecordsRouteAndCandidateSource()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                return Task.FromResult(currentCall == 1
                    ? new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42)
                    : new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track B",
                        "Artist B",
                        null,
                        "spotify",
                        1));
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) => Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: true,
                MediaKeyHelper.MediaCommandRouteKind.CurrentGsmc,
                CandidateSourceAppUserModelId: "Spotify.exe",
                ElapsedMs: 3.4)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack("hotkey");

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.Equal("CurrentGsmc", entry.Details?["sendRoute"]);
        Assert.Equal("true", entry.Details?["sendSent"]);
        Assert.Equal("track-change-confirmed", entry.Details?["confirmation"]);
        Assert.Equal("false", entry.Details?["sendUsedInputFallback"]);
        Assert.True(entry.Details?.ContainsKey("sendCandidateSource"));
        Assert.DoesNotContain(
            entry.Details?.Keys ?? [],
            key => key.Contains("Focused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MediaNextTrack_WhenCommandRoutesToPlayingSource_UsesRoutedSourceForOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        const string targetSource = "Chromium.IS35MD6VSEMY3F3YBN6TO6X5E4";
        MediaOverlaySessionSnapshot braveBaseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Live fixture stream",
            "Fixture streamer",
            null,
            "Brave",
            1724);
        MediaOverlaySessionSnapshot spotifyPreCommand = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Fixture track A",
            "Fixture artist A",
            "Fixture album A",
            targetSource,
            42);
        MediaOverlaySessionSnapshot spotifyChanged = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Fixture track B",
            "Fixture artist B",
            "Fixture album B",
            targetSource,
            0);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (preferredSource, _, _) =>
            {
                return Task.FromResult(string.Equals(preferredSource, targetSource, StringComparison.OrdinalIgnoreCase)
                    ? spotifyChanged
                    : braveBaseline);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["Brave"] = braveBaseline,
                [targetSource] = spotifyPreCommand,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { braveBaseline, spotifyChanged }),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) => Task.FromResult(new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: true,
                MediaKeyHelper.MediaCommandRouteKind.PlayingGsmc,
                CandidateSourceAppUserModelId: targetSource,
                ElapsedMs: 1.2)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack("hotkey");

        await AssertEventuallyAsync(
            () => presenter.MediaUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message =>
            string.Equals(message.header, "Next track", StringComparison.Ordinal)
            && string.Equals(message.deviceName, "Fixture track B", StringComparison.Ordinal));
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-changed", entry.DiagCode);
        Assert.Equal("PlayingGsmc", entry.Details?["sendRoute"]);
        Assert.True(entry.Details?.ContainsKey("sendCandidateSource"));
    }

    [Fact]
    public async Task MediaPreviousTrack_WhenMetadataNeverChanges_RecordsUnchangedDiagnostics()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        MediaOverlaySessionSnapshot baseline = new(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            "Track A",
            "Artist A",
            null,
            "spotify",
            84);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(baseline),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = baseline,
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { baseline }),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPreviousTrackCommandAsync: (_) => Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaPreviousTrack();

        await AssertEventuallyAsync(
            () => presenter.MessageUpdateCount == 1 && history.Count == 1,
            GetMediaOverlayCaptureTimeout());
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-track-unchanged", entry.DiagCode);
        Assert.Equal("unchanged", entry.Details?["outcome"]);
        Assert.Equal("unchanged", entry.Details?["finalFallbackClassification"]);
    }

    [Fact]
    public async Task MediaNextTrack_WhenNewerPressArrives_WaitsForTrailingOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        int sendCount = 0;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                if (currentCall == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42);
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track B",
                    "Artist B",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPlayPauseCommandAsync: (_) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true));
            },
            mediaNextTrackCommandAsync: (_) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true));
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        coordinator.MediaPlayPause();

        await AssertEventuallyAsync(
            () => history.Count == 1 && !coordinator.IsMediaOverlayCaptureInFlightForTests && Volatile.Read(ref sendCount) == 2,
            GetMediaOverlayCaptureTimeout());

        Assert.Contains(presenter.Messages, message => string.Equals(message.deviceName, "Track B", StringComparison.Ordinal));
        Assert.True(presenter.MediaUpdateCount >= 1);
        ExecutionHistoryEntry entry = Assert.Single(history);
        Assert.Equal("media-overlay-trailing-track", entry.DiagCode);
        Assert.Equal("2", entry.Details?["acceptedSends"]);
        Assert.Equal(0, presenter.ActionUpdateCount);
        Assert.Equal(0, presenter.DeviceUpdateCount);
        Assert.Equal(0, presenter.RoutineUpdateCount);
        Assert.Equal(0, presenter.RoutinePartialUpdateCount);
    }

    [Fact]
    public async Task MediaNextTrack_WhenCaptureAlreadyInFlightAndSendFails_ShowsFailureOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                captureStarted.TrySetResult();
                await releaseCapture.Task.WaitAsync(token);
                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) => Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(false)),
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        coordinator.MediaNextTrack();

        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests
                && presenter.Messages.Any(message => string.Equals(message.header, "Next track failed", StringComparison.Ordinal)),
            GetMediaOverlayCaptureTimeout());
        Assert.Contains(history, entry => entry.DiagCode == "media-command-send-failed" && !entry.Success);
    }

    [Fact]
    public async Task MediaNextTrack_WhenTrailingCaptureHasNoMetadata_RecordsHiddenHistoryWithoutGenericOverlay()
    {
        var clock = new MediaOverlayTestHarness.VirtualClock(DateTimeOffset.UtcNow);
        var presenter = new RecordingOverlayPresenter();
        var history = new ConcurrentQueue<ExecutionHistoryEntry>();
        int snapshotCallCount = 0;
        int sendCount = 0;
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                int currentCall = Interlocked.Increment(ref snapshotCallCount);
                if (currentCall == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                    return new MediaOverlaySessionSnapshot(
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        "Track A",
                        "Artist A",
                        null,
                        "spotify",
                        42);
                }

                return MediaOverlaySessionSnapshot.Empty;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track A",
                    "Artist A",
                    null,
                    "spotify",
                    42),
            }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            eventWaitOverride: (_, delayMs, _, _) =>
            {
                clock.Advance(delayMs);
                return Task.FromResult(new MediaEventAssistOutcome(false, null));
            }, utcNow: clock.UtcNow);

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true));
            },
            mediaHistoryRecorder: history.Enqueue);

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        coordinator.MediaNextTrack();

        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests
                && Volatile.Read(ref sendCount) == 2
                && history.Any(entry => entry.DiagCode == "media-overlay-trailing-no-metadata"),
            GetMediaOverlayCaptureTimeout());

        Assert.Equal(0, presenter.ShowCount);
        ExecutionHistoryEntry entry = Assert.Single(history, entry => entry.DiagCode == "media-overlay-trailing-no-metadata");
        Assert.True(entry.Success);
        Assert.True(entry.Skipped);
        Assert.Equal("trailing", entry.Details?["overlayCapture"]);
    }

    [Fact]
    public async Task RapidMediaCommands_AreSentInInvocationOrder()
    {
        var presenter = new RecordingOverlayPresenter();
        var sentCommands = new ConcurrentQueue<string>();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshotCallCount = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                if (Interlocked.Increment(ref snapshotCallCount) == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Track",
                    "Artist",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());
        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) =>
            {
                sentCommands.Enqueue("next");
                return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true));
            },
            mediaPreviousTrackCommandAsync: (_) =>
            {
                sentCommands.Enqueue("previous");
                return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true));
            });

        coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        coordinator.MediaPreviousTrack();

        await AssertEventuallyAsync(() => sentCommands.Count == 2, GetMediaOverlayCaptureTimeout());
        Assert.Collection(
            sentCommands,
            command => Assert.Equal("next", command),
            command => Assert.Equal("previous", command));
    }

    [Fact]
    public async Task ShowCurrentTrack_DuringCommandCapture_IsDeferredAndDisplayed()
    {
        var presenter = new RecordingOverlayPresenter();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshotCallCount = 0;
        var snapshotTraces = new ConcurrentQueue<string?>();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                snapshotTraces.Enqueue(OperationTrace.CurrentId);
                int call = Interlocked.Increment(ref snapshotCallCount);
                if (call == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    call == 1 ? "Old track" : "Current track title",
                    "Artist",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            eventWaitOverride: (_, _, _, _) => Task.FromResult(new MediaEventAssistOutcome(false, null)),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());
        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) => Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true)));

        using (OperationTrace.Attach("first-request"))
            coordinator.MediaNextTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        using (OperationTrace.Attach("pending-request"))
            coordinator.ShowCurrentTrack();
        releaseCapture.TrySetResult();

        await AssertEventuallyAsync(
            () => presenter.Messages.Any(message => message.deviceName == "Current track title"),
            GetMediaOverlayCaptureTimeout());
        Assert.DoesNotContain(presenter.Messages, message => message.deviceName == "Old track");
        Assert.Equal("first-request", snapshotTraces.First());
        Assert.Equal("pending-request", snapshotTraces.Last());
        Assert.Null(OperationTrace.CurrentId);
    }

    [Fact]
    public async Task MediaCommand_DuringCurrentTrackCapture_StartsDeferredTrailingCapture()
    {
        var presenter = new RecordingOverlayPresenter();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshotCallCount = 0;
        var snapshotTraces = new ConcurrentQueue<string?>();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                snapshotTraces.Enqueue(OperationTrace.CurrentId);
                int call = Interlocked.Increment(ref snapshotCallCount);
                if (call == 1)
                {
                    captureStarted.TrySetResult();
                    await releaseCapture.Task.WaitAsync(token);
                }

                return new MediaOverlaySessionSnapshot(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    call == 1 ? "Old track" : "Track after command",
                    "Artist",
                    null,
                    "spotify",
                    1);
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaNextTrackCommandAsync: (_) => Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true)));

        using (OperationTrace.Attach("first-request"))
            coordinator.ShowCurrentTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);
        using (OperationTrace.Attach("pending-request"))
            coordinator.MediaNextTrack();
        releaseCapture.TrySetResult();

        await AssertEventuallyAsync(
            () => presenter.Messages.Any(message => message.deviceName == "Track after command"),
            GetMediaOverlayCaptureTimeout());
        Assert.DoesNotContain(presenter.Messages, message => message.deviceName == "Old track");
        Assert.Equal("first-request", snapshotTraces.First());
        Assert.Equal("pending-request", snapshotTraces.Last());
        Assert.Null(OperationTrace.CurrentId);
    }

    [Fact]
    public async Task ShutdownAsync_CancelsAndDrainsCurrentTrackCapture()
    {
        var presenter = new RecordingOverlayPresenter();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                captureStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return MediaOverlaySessionSnapshot.Empty;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();
        await captureStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);

        await coordinator.ShutdownAsync().WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);

        Assert.False(coordinator.IsMediaOverlayCaptureInFlightForTests);
        Assert.Equal(0, presenter.ShowCount);
        coordinator.ShowCurrentTrack();
        Assert.Equal(0, presenter.ShowCount);
    }

    [Fact]
    public async Task ShutdownAsync_CancelsCancellableMediaCommandDelegate()
    {
        var presenter = new RecordingOverlayPresenter();
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());
        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPlayPauseCommandAsync: async cancellationToken =>
            {
                commandStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }

                return new MediaKeyHelper.MediaCommandSendOutcome(true, MediaKeyHelper.MediaCommandRouteKind.Delegate);
            });

        coordinator.MediaPlayPause();
        await commandStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);

        await coordinator.ShutdownAsync().WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, presenter.ShowCount);
    }

    [Fact]
    public async Task ShutdownAsync_WhenLegacyCommandIgnoresCancellation_UsesBoundedDrainAndSuppressesLatePublication()
    {
        var presenter = new RecordingOverlayPresenter();
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()),
            timingProfile: MediaOverlayTestHarness.CreateDeterministicNoDelayTimingProfile());
        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine),
            mediaPlayPauseCommandAsync: async (_) =>
            {
                commandStarted.TrySetResult();
                await releaseCommand.Task;
                return new MediaKeyHelper.MediaCommandSendOutcome(true, MediaKeyHelper.MediaCommandRouteKind.Delegate);
            },
            mediaShutdownDrainTimeoutMs: 50);

        coordinator.MediaPlayPause();
        await commandStarted.Task.WaitAsync(GetMediaOverlayCaptureTimeout(), TestContext.Current.CancellationToken);

        await coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        releaseCommand.TrySetResult();
        await AssertEventuallyAsync(
            () => !coordinator.IsMediaOverlayCaptureInFlightForTests,
            TimeSpan.FromSeconds(2));

        Assert.Equal(0, presenter.ShowCount);
    }

    [Fact]
    public async Task NewMediaCommand_CancelsAnOlderPostSendReadAndReportsBothAcceptedSends()
    {
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var enteredRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = new TaskCompletionSource<ExecutionHistoryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Fixture", "Artist", null, "Player", 30);
        int reads = 0;
        int sends = 0;
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: async (_, _, token) =>
            {
                if (Interlocked.Increment(ref reads) != 2) return snapshot;
                enteredRecovery.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    canceledRecovery.TrySetResult();
                    throw;
                }
                return snapshot;
            },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot> { ["Player"] = snapshot }),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot> { snapshot }));
        var coordinator = new AppAudioCommandCoordinator(null!, overlay, new MediaOverlayCommandService(engine), Logger.Instance, () => new Settings(),
            mediaNextTrackCommandAsync: _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(MediaKeyHelper.MediaCommandSendOutcome.FromDelegate(true));
            },
            mediaHistoryRecorder: entry => { if (entry.DiagCode == "media-overlay-trailing-track") feedback.TrySetResult(entry); });
        CancellationToken testToken = TestContext.Current.CancellationToken;
        try
        {
            coordinator.MediaNextTrack();
            await enteredRecovery.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            Assert.Equal(1, Volatile.Read(ref sends));
            coordinator.MediaNextTrack();
            await canceledRecovery.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            ExecutionHistoryEntry latest = await feedback.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            Assert.Equal(2, Volatile.Read(ref sends));
            Assert.Equal("2", latest.Details?["acceptedSends"]);
            Assert.Single(presenter.MediaMessages);
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenOverlaysDisabled_SkipsCaptureButAllowsStatusReads()
    {
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter, initiallyEnabled: false);
        int reads = 0;
        var snapshot = new MediaOverlaySessionSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, "Fixture", "Artist", null, "Player", 30);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => { reads++; return Task.FromResult(snapshot); },
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        var coordinator = new AppAudioCommandCoordinator(null!, overlay, new MediaOverlayCommandService(engine), Logger.Instance, () => new Settings());
        try
        {
            coordinator.ShowCurrentTrack();
            Assert.Equal(0, reads);
            Assert.Equal(snapshot, await coordinator.GetCurrentMediaSnapshotAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, reads);
            Assert.False(overlay.HasPresenterForTests);
        }
        finally { await coordinator.ShutdownAsync(); }
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenTrackMetadataExists_ShowsTrackOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                new(
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    "Fixture Title",
                    "Fixture Artist",
                    "Fixture Album",
                    "fixture-source",
                    33),
            }));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MediaUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Current track", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenPausedTrackMetadataExists_ShowsPausedTrackOverlay()
    {
        var presenter = new RecordingOverlayPresenter();
        var pausedSnapshot = new MediaOverlaySessionSnapshot(
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            "Fixture Title",
            "Fixture Artist",
            "Fixture Album",
            "fixture-source",
            33);
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(pausedSnapshot),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>
            {
                pausedSnapshot,
            }));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MediaUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Current track paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenNoTrackMetadataExists_ShowsTrackInformationUnavailable()
    {
        var presenter = new RecordingOverlayPresenter();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(new MediaOverlaySessionSnapshot(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                null,
                null,
                null,
                null,
                null)),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>(StringComparer.OrdinalIgnoreCase)),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));

        AppAudioCommandCoordinator coordinator = CreateCoordinator(
            presenter,
            new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MessageUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "Track information unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShowCurrentTrack_WhenNoSessionExists_ShowsNoSessionDetected()
    {
        var presenter = new RecordingOverlayPresenter();
        var engine = new MediaOverlayEngine(
            currentSnapshotOverride: (_, _, _) => Task.FromResult(MediaOverlaySessionSnapshot.Empty),
            snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        AppAudioCommandCoordinator coordinator = CreateCoordinator(presenter, new MediaOverlayCommandService(engine));

        coordinator.ShowCurrentTrack();

        await AssertEventuallyAsync(() => presenter.MessageUpdateCount == 1, GetMediaOverlayCaptureTimeout());
        Assert.Contains(presenter.Messages, message => string.Equals(message.header, "No media session detected", StringComparison.Ordinal));
    }

    private static AppAudioCommandCoordinator CreateCoordinator(
        RecordingOverlayPresenter presenter,
        MediaOverlayCommandService? mediaOverlayCommands = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPlayPauseCommandAsync = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaNextTrackCommandAsync = null,
        Func<CancellationToken, Task<MediaKeyHelper.MediaCommandSendOutcome>>? mediaPreviousTrackCommandAsync = null,
        Action<ExecutionHistoryEntry>? mediaHistoryRecorder = null,
        int mediaShutdownDrainTimeoutMs = AppConstants.MediaOverlay.ShutdownDrainTimeoutMs)
    {
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        var overlay = new OverlayService(action => action(), _ => presenter);
        return new AppAudioCommandCoordinator(
            audio, overlay, mediaOverlayCommands ?? new MediaOverlayCommandService(), Logger.Instance,
            () => new Settings(),
            mediaPlayPauseCommandAsync: mediaPlayPauseCommandAsync,
            mediaNextTrackCommandAsync: mediaNextTrackCommandAsync,
            mediaPreviousTrackCommandAsync: mediaPreviousTrackCommandAsync,
            mediaHistoryRecorder: mediaHistoryRecorder,
            mediaShutdownDrainTimeoutMs: mediaShutdownDrainTimeoutMs);
    }

    private static TimeSpan GetMediaOverlayCaptureTimeout()
    {
        return TimeSpan.FromMilliseconds(AppConstants.MediaOverlay.MaxCaptureDurationMs + 1000);
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        await TestExecutionGuards.WaitUntilAsync(
            condition,
            "Timed out waiting for the overlay coordinator condition.",
            timeout);
    }
}
