using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceServiceTests
{
    [Fact]
    public void NormalizeConfiguredRoles_ReturnsFallback_WhenNull()
    {
        var fallback = new[] { Role.Multimedia, Role.Console };

        var result = AudioDeviceService.NormalizeConfiguredRoles(null, fallback);

        Assert.Equal(fallback, result);
    }

    [Fact]
    public void NormalizeConfiguredRoles_ParsesDistinctKnownRoles_AndIgnoresUnknown()
    {
        string[] configured = [" multimedia ", "Console", "invalid", "COMMUNICATIONS", "Multimedia"];

        var result = AudioDeviceService.NormalizeConfiguredRoles(configured, [Role.Console]);

        Assert.Equal([Role.Multimedia, Role.Console, Role.Communications], result);
    }

    [Fact]
    public void ResolveDetectionRole_ReturnsFirstConfiguredRole_OrFallback()
    {
        var configured = new[] { Role.Communications, Role.Console };

        var selected = AudioDeviceService.ResolveDetectionRole(configured, Role.Multimedia);
        var selectedFallback = AudioDeviceService.ResolveDetectionRole([], Role.Multimedia);

        Assert.Equal(Role.Communications, selected);
        Assert.Equal(Role.Multimedia, selectedFallback);
    }

    [Fact]
    public void RaiseAudioSessionCreatedForTests_InvokesSubscribers()
    {
        using var service = CreateAudioService();
        int count = 0;
        AudioMixerMode? receivedMode = null;

        service.AudioSessionCreated += mixerMode =>
        {
            receivedMode = mixerMode;
            Interlocked.Increment(ref count);
        };

        service.RaiseAudioSessionCreatedForTests(AudioMixerMode.Input);

        Assert.Equal(1, count);
        Assert.Equal(AudioMixerMode.Input, receivedMode);
    }

    [Fact]
    public void RaiseAudioSessionLifecycleChangedForTests_InvokesSubscribers()
    {
        using var service = CreateAudioService();
        AudioSessionLifecycleSignal? received = null;

        service.AudioSessionLifecycleChanged += signal => received = signal;

        service.RaiseAudioSessionLifecycleChangedForTests(new AudioSessionLifecycleSignal(
            AudioMixerMode.Input,
            AudioSessionLifecycleSignalKind.Disconnected,
            "session-1",
            EndpointId: "capture-secondary",
            DisconnectReason: AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval));

        Assert.NotNull(received);
        Assert.Equal(AudioMixerMode.Input, received.Value.MixerMode);
        Assert.Equal(AudioSessionLifecycleSignalKind.Disconnected, received.Value.Kind);
        Assert.Equal("session-1", received.Value.SessionInstanceId);
        Assert.Equal("capture-secondary", received.Value.EndpointId);
        Assert.Equal(AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval, received.Value.DisconnectReason);
    }

    [Fact]
    public void RaiseDeviceStateChangedForTests_InvalidatesRecentMixerSnapshotState()
    {
        using var service = CreateAudioService();
        SeedRecentMixerSnapshotState(service);

        service.RaiseDeviceStateChangedForTests();

        AssertRecentMixerSnapshotStateCleared(service);
    }

    [Fact]
    public void RaiseDefaultPlaybackDeviceChangedForTests_InvalidatesRecentMixerSnapshotState()
    {
        using var service = CreateAudioService();
        SeedRecentMixerSnapshotState(service);

        service.RaiseDefaultPlaybackDeviceChangedForTests();

        AssertRecentMixerSnapshotStateCleared(service);
    }

    [Fact]
    public async Task RecoverAfterSystemResumeAsync_InvalidatesRecentMixerSnapshotState()
    {
        using var service = CreateAudioService();
        SeedRecentMixerSnapshotState(service);

        await service.RecoverAfterSystemResumeAsync();

        AssertRecentMixerSnapshotStateCleared(service);
    }

    [Fact]
    public void ApplyConfiguredRoles_WithInvalidDeviceId_ThrowsInteropFailure()
    {
        MethodInfo? applyConfiguredRoles = typeof(AudioDeviceService)
            .GetMethod("ApplyConfiguredRoles", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(applyConfiguredRoles);

        var exception = Record.Exception(() =>
            applyConfiguredRoles!.Invoke(null, ["__invalid_device_id__", new[] { Role.Multimedia }]));

        Assert.NotNull(exception);
        var targetException = Assert.IsType<TargetInvocationException>(exception);
        Assert.NotNull(targetException.InnerException);
        Assert.True(
            targetException.InnerException is COMException ||
            targetException.InnerException is ArgumentException ||
            targetException.InnerException is InvalidOperationException,
            $"Expected COMException, ArgumentException, or InvalidOperationException, got {targetException.InnerException.GetType().Name}");
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var service = CreateAudioService();

        await service.DisposeAsync();
        await service.DisposeAsync();
        service.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_IsConcurrencySafe_WhenCalledInParallel()
    {
        var service = CreateAudioService();

        var disposeCalls = Enumerable.Range(0, 8)
            .Select(_ => service.DisposeAsync().AsTask())
            .ToArray();

        await Task.WhenAll(disposeCalls);
        service.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithinBoundedTimeout_WhenBackgroundTaskIsHung()
    {
        var service = CreateAudioService();
        var stuckTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BackgroundTasksForTests[12345] = stuckTask.Task;

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(
            AppConstants.Timing.CleanupWaitMs +
            AppConstants.Timing.CleanupGraceExtensionMs +
            5000), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposeAsync_SignalsResumeRecoveryCompletion_WhenRecoveryIsMarkedActive()
    {
        var service = CreateAudioService();
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.SetResumeRecoveryStateForTests(completionSource, activeCount: 1);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(
            AppConstants.Timing.CleanupWaitMs +
            AppConstants.Timing.CleanupGraceExtensionMs +
            5000), TestContext.Current.CancellationToken);

        Assert.True(completionSource.Task.IsCompleted);
    }

    [Fact]
    public void Dispose_CompletesOnDispatcherSynchronizationContext_WhenBackgroundTaskIsHung()
    {
        TestExecutionGuards.RunSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var service = CreateAudioService();
            var stuckTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.BackgroundTasksForTests[12345] = stuckTask.Task;

            Exception? exception = Record.Exception(service.Dispose);

            Assert.Null(exception);
        }, timeout: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void BuildDeviceLookup_ReturnsCaseInsensitiveDictionary()
    {
        Dictionary<string, MMDevice> lookup = AudioDeviceService.BuildDeviceLookup([]);

        Assert.Empty(lookup);
        Assert.True(lookup.Comparer.Equals("capture-a", "CAPTURE-A"));
    }

    [Fact]
    public void SyncCycleDevices_ReturnsFalse_WhenCollectionAlreadyMatches()
    {
        ObservableCollection<CycleDevice> target =
        [
            new CycleDevice { Id = "out-1", Name = "Speakers" },
            new CycleDevice { Id = "out-2", Name = "Headset" },
        ];

        bool changed = AppViewModelDeviceCycleHelper.SyncCycleDevices(
            target,
            [
                new CycleDevice { Id = "out-1", Name = "Speakers" },
                new CycleDevice { Id = "out-2", Name = "Headset" },
            ]);

        Assert.False(changed);
        Assert.Equal(2, target.Count);
    }

    [Fact]
    public void SyncCycleDevices_UpdatesInPlace_WithoutClearingCollection()
    {
        ObservableCollection<CycleDevice> target =
        [
            new CycleDevice { Id = "out-1", Name = "Old Speakers" },
            new CycleDevice { Id = "out-2", Name = "Headset" },
            new CycleDevice { Id = "out-3", Name = "Remove Me" },
        ];

        CycleDevice retainedThird = target[2];

        bool changed = AppViewModelDeviceCycleHelper.SyncCycleDevices(
            target,
            [
                new CycleDevice { Id = "out-1", Name = "Speakers" },
                new CycleDevice { Id = "out-4", Name = "HDMI" },
            ]);

        Assert.True(changed);
        Assert.Equal(2, target.Count);
        Assert.Equal("Speakers", target[0].Name);
        Assert.Equal("out-4", target[1].Id);
        Assert.Equal("HDMI", target[1].Name);
        Assert.DoesNotContain(retainedThird, target);
    }

    private static AudioDeviceService CreateAudioService()
    {
        return new AudioDeviceService(new FakeInputListenPropertyWriter());
    }

    private static void SeedRecentMixerSnapshotState(AudioDeviceService service)
    {
        AudioSessionService sessionService = TestPrivateAccess.GetField<AudioSessionService>(service, "_sessionService");
        sessionService.SeedRecentSnapshotForTests(
            AudioMixerMode.Output,
            [new AudioSessionSnapshot("Master Volume", 50f, "Playback", null, null, null)],
            DateTime.UtcNow);
        sessionService.SeedRecentSnapshotForTests(
            AudioMixerMode.Input,
            [new AudioSessionSnapshot("Microphone Volume", 25f, "Recording", null, null, null)],
            DateTime.UtcNow);
        sessionService.SetOutputScanStateForTests(
            "playback-topology",
            new HashSet<string>(["playback-1"], StringComparer.OrdinalIgnoreCase),
            selectivePlaybackScanStreak: 2);
    }

    private static void AssertRecentMixerSnapshotStateCleared(AudioDeviceService service)
    {
        AudioSessionService sessionService = TestPrivateAccess.GetField<AudioSessionService>(service, "_sessionService");
        Assert.Null(sessionService.GetRecentSnapshotDataForTests(AudioMixerMode.Output).Snapshot);
        Assert.Null(sessionService.GetRecentSnapshotDataForTests(AudioMixerMode.Input).Snapshot);
        var outputScanState = sessionService.GetOutputScanStateForTests();
        Assert.Equal(string.Empty, outputScanState.PlaybackFingerprint);
        Assert.Null(outputScanState.SessionBearingPlaybackDeviceIds);
        Assert.Equal(0, outputScanState.SelectivePlaybackScanStreak);
    }
}

