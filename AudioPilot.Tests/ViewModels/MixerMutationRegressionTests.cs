using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class MixerMutationRegressionTests
{
    private static readonly DataFlow[] EndpointFlows = [DataFlow.Render, DataFlow.Capture];
    private static readonly bool[] MuteEditOptions = [false, true];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTargetSessionsRequestRecoveryRatherThanBehavingLikeCanceledWork(bool mute)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            MixerViewModel mixer = Mixer(AudioMixerMode.Output);
            mixer.Sessions.Add(new AudioSessionItem("Example", 90, false, false, processId: 42));
            try
            {
                var failure = Task.FromResult(new MixerViewModel.MixerMutationResult(0, 0, 1));
                Task observation = mute
                    ? (Task)Invoke(mixer, "ObserveMuteApplyAsync", failure, "missing-target", CancellationToken.None)!
                    : (Task)Invoke(mixer, "ObserveVolumeApplyAsync", failure, "missing-target")!;
                TestPrivateAccess.RunTaskOnDispatcher(observation);
                Assert.True(mixer.RequiresActivationRefresh);
            }
            finally { mixer.Cleanup(); }
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, false)]
    [InlineData(AudioMixerMode.Input, true)]
    public void SnapshotCapturedDuringAnEditCannotOverwriteItAfterCompletion(AudioMixerMode mode, bool mute)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Example", 75, false, false, processId: 42);
            mixer.Sessions.Add(row);
            TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById")["pid:42"] = row;
            try
            {
                if (mute) { row.IsMuted = true; row.BeginMuteEdit(row.MuteEditRevision); }
                else { row.Volume = 90; row.BeginVolumeEdit(row.VolumeEditRevision); }
                Task<bool> refresh = mixer.ApplyRefreshResultsAsync(0, null, null, [("pid:42", 20f, false)], CancellationToken.None);
                if (mute) row.CompleteMuteEdit(row.MuteEditRevision);
                else row.CompleteVolumeEdit(row.VolumeEditRevision);
                TestPrivateAccess.RunTaskOnDispatcher(refresh);
                Assert.Equal(mute ? 20 : 90, row.Volume);
                Assert.Equal(mute, row.IsMuted);
            }
            finally { mixer.Cleanup(); }
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public void SameStateMuteIntentProtectsBothTheRowAndFlagsFromOlderFeedback(AudioMixerMode mode)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(MixerMutationRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Endpoint", 75, mode == AudioMixerMode.Output, mode == AudioMixerMode.Input, endpointId: "original");
            mixer.Sessions.Add(row);
            TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById")[mode == AudioMixerMode.Input ? "mic:primary" : "master:primary"] = row;
            TestPrivateAccess.SetField(harness.ViewModel, mode == AudioMixerMode.Output ? "_mixer" : "_inputMixer", mixer);
            Task.Run(() => harness.ViewModel.ProjectEndpointVolumeStateFromCommand(mode, "original", 20, true),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            harness.Audio.RecordEndpointMuteIntent(mode == AudioMixerMode.Output);
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Assert.Equal(20, row.Volume);
            Assert.False(row.IsMuted);
            Assert.False(mode == AudioMixerMode.Input ? harness.ViewModel.MuteMic : harness.ViewModel.MuteSound);
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, false)]
    [InlineData(AudioMixerMode.Input, true)]
    public void QueuedEndpointFeedbackCannotOverwriteANewerCompletedEdit(AudioMixerMode mode, bool mute)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(MixerMutationRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Endpoint", 75, mode == AudioMixerMode.Output, mode == AudioMixerMode.Input, endpointId: "original");
            mixer.Sessions.Add(row);
            TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById")[mode == AudioMixerMode.Input ? "mic:primary" : "master:primary"] = row;
            TestPrivateAccess.SetField(harness.ViewModel, mode == AudioMixerMode.Output ? "_mixer" : "_inputMixer", mixer);
            Task.Run(() => harness.ViewModel.ProjectEndpointVolumeStateFromCommand(mode, "original", 20, false),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            if (mute) row.IsMuted = true;
            else row.Volume = 90;
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Assert.Equal(mute ? 20 : 90, row.Volume);
            Assert.Equal(mute, row.IsMuted);
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, false)]
    [InlineData(AudioMixerMode.Input, true)]
    public void RefreshCapturedBeforeAnEditCannotOverwriteItAfterTheWriteCompletes(AudioMixerMode mode, bool mute)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Example", 75, false, false, processId: 42);
            var rows = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById");
            rows["pid:42"] = row;
            mixer.Sessions.Add(row);
            try
            {
                Task<bool> refresh = mixer.ApplyRefreshResultsAsync(0, null, null, [("pid:42", 20f, false)], CancellationToken.None);
                if (mute) row.IsMuted = true;
                else row.Volume = 90;
                Assert.False(row.HasPendingVolumeEdit);
                Assert.False(row.HasPendingMuteEdit);
                TestPrivateAccess.RunTaskOnDispatcher(refresh);
                Assert.Equal(mute ? 20 : 90, row.Volume);
                Assert.Equal(mute, row.IsMuted);
            }
            finally { mixer.Cleanup(); }
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public void ReturningToTheSameEndpointStillInvalidatesFeedbackQueuedBeforeSwitching(AudioMixerMode mode)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(MixerMutationRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            DataFlow flow = mode == AudioMixerMode.Input ? DataFlow.Capture : DataFlow.Render;
            Role role = mode == AudioMixerMode.Input ? harness.Audio.RecordingDetectionRole : harness.Audio.PlaybackDetectionRole;
            Invoke(harness.ViewModel, "OnPrimaryEndpointIdentityChanged", flow, role, "original");
            Task.Run(() => harness.ViewModel.ProjectEndpointVolumeStateFromCommand(mode, "original", 20, true),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Invoke(harness.ViewModel, "OnPrimaryEndpointIdentityChanged", flow, role, "replacement");
            Invoke(harness.ViewModel, "OnPrimaryEndpointIdentityChanged", flow, role, "original");
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Assert.False(mode == AudioMixerMode.Input ? harness.ViewModel.MuteMic : harness.ViewModel.MuteSound);
            harness.ViewModel.ProjectEndpointVolumeStateFromCommand(mode, "original", 20, true);
            Assert.True(mode == AudioMixerMode.Input ? harness.ViewModel.MuteMic : harness.ViewModel.MuteSound);
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, false)]
    [InlineData(AudioMixerMode.Input, true)]
    public void OlderRefreshPreservesNewerLeadingVolumeOrMuteIntent(AudioMixerMode mode, bool mute)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Example", 75, false, false, processId: 42);
            TestPrivateAccess.RunTaskOnDispatcher(mixer.ApplyRefreshResultsAsync(0, null, [("pid:42", row)], null, CancellationToken.None));
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task worker = ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                started.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                Task<bool> oldRefresh = mixer.ApplyRefreshResultsAsync(0, null, null, [("pid:42", 20f, false)], CancellationToken.None);
                if (mute) row.IsMuted = true;
                else row.Volume = 90;
                Assert.Equal(!mute, mixer.HasPendingVolumeChange("pid:42"));
                TestPrivateAccess.RunTaskOnDispatcher(oldRefresh);
                Assert.Equal(mute ? 20 : 90, row.Volume);
                Assert.Equal(mute, row.IsMuted);
            }
            finally
            {
                mixer.Cleanup();
                release.Set();
                worker.GetAwaiter().GetResult();
                ComThreadingHelper.RunOnCoreAudioThread(static () => { });
            }
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, false)]
    [InlineData(AudioMixerMode.Input, true)]
    public void QueueFailureRequestsAuthoritativeRecovery(AudioMixerMode mode, bool mute)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Example", 90, false, false, processId: 42, isMuted: true);
            mixer.Sessions.Add(row);
            var failure = Task.FromException<MixerViewModel.MixerMutationResult>(new InvalidOperationException("CoreAudio queue full"));
            try
            {
                Task observation = mute
                    ? (Task)Invoke(mixer, "ObserveMuteApplyAsync", failure, "regression", CancellationToken.None)!
                    : (Task)Invoke(mixer, "ObserveVolumeApplyAsync", failure, "regression")!;
                TestPrivateAccess.RunTaskOnDispatcher(observation);
                Assert.Equal(90, row.Volume);
                Assert.True(row.IsMuted);
                Assert.True(mixer.RequiresActivationRefresh);
            }
            finally { mixer.Cleanup(); }
        });
    }

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public void QueuedOldEndpointProjectionCannotUpdateReplacementEndpointOrMuteFlags(AudioMixerMode mode)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(MixerMutationRegressionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            MixerViewModel mixer = Mixer(mode);
            var row = new AudioSessionItem("Endpoint", 75, mode == AudioMixerMode.Output, mode == AudioMixerMode.Input,
                endpointId: "old-endpoint");
            mixer.Sessions.Add(row);
            TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById")[mode == AudioMixerMode.Input ? "mic:primary" : "master:primary"] = row;
            TestPrivateAccess.SetField(harness.ViewModel, mode == AudioMixerMode.Output ? "_mixer" : "_inputMixer", mixer);
            Task.Run(() => harness.ViewModel.ProjectEndpointVolumeStateFromCommand(mode, "old-endpoint", 20, true),
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            row.UpdateRoutingMetadataFromSystem(null, "", "replacement-endpoint");
            row.SetStateFromSystem(90, false);
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Assert.Equal("replacement-endpoint", row.EndpointId);
            Assert.Equal(90, row.Volume);
            Assert.False(row.IsMuted);
            Assert.False(mode == AudioMixerMode.Output ? harness.ViewModel.MuteSound : harness.ViewModel.MuteMic);
        });
    }

    [AudioHardwareFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    public void FaultedEndpointWritesReconcileAgainstActualStateDespiteRecentEditCaching()
    {
        if (!TestExecutionGuards.RequireDefaultAudioEndpoints(nameof(FaultedEndpointWritesReconcileAgainstActualStateDespiteRecentEditCaching),
            (DataFlow.Render, Role.Multimedia), (DataFlow.Capture, Role.Console))) return;
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = TestLoggerScope.CreateInMemory("mixer-fault-recovery.log");
            using AudioDeviceService audio = ComThreadingHelper.RunOnCoreAudioThread(() => new AudioDeviceService(logger: logger.Logger,
                inputListenPropertyWriter: new InputListenPropertyWriter(logger.Logger), deviceCacheAccessor: static () => null));
            DeviceCacheHelper.Initialize(audio);
            try
            {
                foreach (AudioMixerMode mode in new[] { AudioMixerMode.Output, AudioMixerMode.Input })
                {
                    var mixer = new MixerViewModel(audio, Dispatcher.CurrentDispatcher, mode, logger.Logger);
                    try
                    {
                        TestPrivateAccess.RunTaskOnDispatcher(mixer.RefreshAsync());
                        AudioSessionItem row = Assert.IsType<AudioSessionItem>(mixer.GetEndpointRow(mode));
                        float actualVolume = row.Volume;
                        bool actualMute = row.IsMuted;
                        foreach (bool mute in MuteEditOptions)
                        {
                            row.SetStateFromSystem(actualVolume > 50 ? 0 : 100, !actualMute);
                            string id = mode == AudioMixerMode.Input ? "mic:primary" : "master:primary";
                            TestPrivateAccess.GetField<ConcurrentDictionary<string, DateTime>>(mixer, "_lastVolumeSetByUs")[id] = DateTime.UtcNow;
                            var fault = Task.FromException<MixerViewModel.MixerMutationResult>(new InvalidOperationException("CoreAudio queue full"));
                            Task observation = mute
                                ? (Task)Invoke(mixer, "ObserveMuteApplyAsync", fault, "regression", CancellationToken.None)!
                                : (Task)Invoke(mixer, "ObserveVolumeApplyAsync", fault, "regression")!;
                            TestPrivateAccess.RunTaskOnDispatcher(observation);
                            TestPrivateAccess.RunTaskOnDispatcher(mixer.WaitForRefreshSettlementAsync(TestContext.Current.CancellationToken));
                            Assert.Equal(actualVolume, row.Volume, 1);
                            Assert.Equal(actualMute, row.IsMuted);
                            Assert.False(mixer.RequiresActivationRefresh);
                        }

                        AudioSessionService sessionService = TestPrivateAccess.GetField<AudioSessionService>(audio, "_sessionService");
                        AudioSessionSnapshot[] snapshots = Assert.IsType<AudioSessionSnapshot[]>(sessionService.GetRecentSnapshotDataForTests(mode).Snapshot);
                        row.SetStateFromSystem(80, false);
                        TestPrivateAccess.GetField<ConcurrentDictionary<string, DateTime>>(mixer, "_lastVolumeSetByUs")[mode == AudioMixerMode.Input ? "mic:primary" : "master:primary"] = DateTime.UtcNow;
                        AudioSessionSnapshot[] staleVolumeWithNewMute = [.. snapshots.Select(snapshot => snapshot.DisplayName == row.DisplayName && snapshot.EndpointId == row.EndpointId
                            ? snapshot with { Volume = 20, IsMuted = true } : snapshot)];
                        sessionService.SeedRecentSnapshotForTests(mode, staleVolumeWithNewMute, DateTime.UtcNow);
                        TestPrivateAccess.RunTaskOnDispatcher(mixer.RefreshAsync());
                        Assert.Equal(80, row.Volume);
                        Assert.True(row.IsMuted);
                    }
                    finally { mixer.Cleanup(); }
                }
            }
            finally { DeviceCacheHelper.DisposeSingleton(); }
        });
    }

    [AudioHardwareFact]
    [Trait(TestCategories.Name, TestCategories.Integration)]
    public async Task StaleEndpointEditsAreRejectedAndExplicitMixerMuteInvalidatesPreservation()
    {
        if (!TestExecutionGuards.RequireDefaultAudioEndpoints(nameof(StaleEndpointEditsAreRejectedAndExplicitMixerMuteInvalidatesPreservation),
            (DataFlow.Render, Role.Multimedia), (DataFlow.Capture, Role.Multimedia))) return;
        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpointLock = new ReaderWriterLockSlim();
            var query = new AudioDeviceEndpointQueryHelper(enumerator, endpointLock, Logger.Instance, () => false,
                () => [Role.Multimedia], () => [Role.Multimedia], (_, _) => { });
            var audio = (AudioDeviceService)RuntimeHelpers.GetUninitializedObject(typeof(AudioDeviceService));
            TestPrivateAccess.SetField(audio, "_endpointQueryHelper", query);
            foreach (DataFlow flow in EndpointFlows)
            {
                using var target = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                AudioEndpointVolume endpointVolume = target.AudioEndpointVolume;
                float originalVolume = endpointVolume.MasterVolumeLevelScalar;
                bool originalMute = endpointVolume.Mute;
                MixerViewModel mixer = Mixer(flow == DataFlow.Render ? AudioMixerMode.Output : AudioMixerMode.Input);
                TestPrivateAccess.SetField(mixer, "_audio", audio);
                var row = new AudioSessionItem("Previous endpoint", originalVolume * 100, flow == DataFlow.Render,
                    flow == DataFlow.Capture, endpointId: "previous-endpoint");
                try
                {
                    float requested = originalVolume > .9f ? originalVolume - .01f : originalVolume + .01f;
                    var result = (MixerViewModel.MixerMutationResult)Invoke(mixer, "ApplyVolumeChange", row, requested * 100, row.EndpointId)!;
                    Assert.False(result.IsSuccess);
                    Assert.Equal(originalVolume, endpointVolume.MasterVolumeLevelScalar, 4);
                    var staleMute = (MixerViewModel.MixerMutationResult)Invoke(mixer, "ApplyMuteChange", row, originalMute, row.EndpointId, null)!;
                    Assert.False(staleMute.IsSuccess);
                    Assert.Equal(originalMute, endpointVolume.Mute);
                    using var guard = EndpointStatePreservationGuard.Capture(target.ID, Logger.Instance);
                    Assert.NotNull(guard);
                    string revisionField = flow == DataFlow.Render ? "_playbackMuteRevision" : "_microphoneMuteRevision";
                    long revision = TestPrivateAccess.GetField<long>(audio, revisionField);
                    row.UpdateRoutingMetadataFromSystem(null, "", target.ID);
                    var muteResult = (MixerViewModel.MixerMutationResult)Invoke(mixer, "ApplyMuteChange", row, originalMute, row.EndpointId, null)!;
                    Assert.True(muteResult.IsSuccess);
                    Assert.True(revision < TestPrivateAccess.GetField<long>(audio, revisionField));
                    Assert.True(guard.CanRestoreMute());
                    bool? restoration = null;
                    bool Allowed() => revision == TestPrivateAccess.GetField<long>(audio, revisionField) && guard.CanRestoreMute();
                    if (flow == DataFlow.Capture)
                        PostSwitchCoordinator.RestoreInputStateAsync(null, target.ID, (_, _, muted) => restoration = muted,
                            () => true, TestContext.Current.CancellationToken, muted: !originalMute, canRestoreMute: Allowed).GetAwaiter().GetResult();
                    else
                        PostSwitchCoordinator.ExecuteAsync(() => false, Logger.Instance, null!, "audit", target.ID,
                            Role.Multimedia, null, !originalMute, false, false, false, false, null, TestContext.Current.CancellationToken,
                            runMuteApplyWorkAsync: (_, muted, _, _) => { restoration = muted; return Task.CompletedTask; },
                            canRestorePlaybackMute: Allowed).GetAwaiter().GetResult();
                    Assert.Null(restoration);
                    Assert.Equal(originalMute, endpointVolume.Mute);
                    long queuedIntent = audio.RecordEndpointMuteIntent(flow == DataFlow.Render);
                    audio.RecordEndpointMuteIntent(flow == DataFlow.Render);
                    var superseded = (MixerViewModel.MixerMutationResult)Invoke(mixer, "ApplyMuteChange", row, originalMute, row.EndpointId, queuedIntent)!;
                    Assert.False(superseded.IsSuccess);
                    Assert.Equal(originalMute, endpointVolume.Mute);
                }
                finally
                {
                    endpointVolume.MasterVolumeLevelScalar = originalVolume;
                    mixer.Cleanup();
                }
            }
        }, TestContext.Current.CancellationToken);
    }

    private static MixerViewModel Mixer(AudioMixerMode mode) =>
        MixerViewModelChurnTests.CreateMixerForApplyTests(mode, Dispatcher.CurrentDispatcher);

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
