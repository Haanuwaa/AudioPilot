using AudioPilot.Tests.Helpers;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Internal;

[Collection("CoreAudioWorkerIsolation")]
public sealed class PostSwitchCoordinatorTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, false, null)]
    [InlineData(true, null, false, null)]
    [InlineData(false, null, true, true)]
    public async Task OutputMute_IsIndependentOfVolumePreservation_AndDoesNotTouchOtherEndpoints(
        bool keepMuteState, bool? sourceMuted, bool forceMute, bool? expectedMute)
    {
        using var logger = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), "switch-mute-policy.log");
        var writes = new List<bool>();
        await PostSwitchCoordinator.ExecuteAsync(() => false, logger.Logger, null!, "switch", "selected-output", NRole.Console,
            null, PostSwitchCoordinator.ResolveSwitchMuteState(keepMuteState, sourceMuted, forceMute), false,
            false, false, false, null, TestContext.Current.CancellationToken,
            runMuteApplyWorkAsync: (microphone, playback, deafen, _) =>
            {
                Assert.Null(microphone);
                Assert.False(deafen);
                writes.Add(playback!.Value);
                return Task.CompletedTask;
            });
        if (expectedMute.HasValue) Assert.Equal(expectedMute.Value, Assert.Single(writes));
        else Assert.Empty(writes);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, null)]
    [InlineData(false, null, null)]
    public async Task InputState_RestoresMuteAndVolumeIndependently(bool preserveVolume, bool? sourceMuted, bool? expectedMute)
    {
        var writes = new List<(string Id, float? Volume, bool? Muted)>();
        await PostSwitchCoordinator.RestoreInputStateAsync(
            preserveVolume ? Task.FromResult(new SessionVolumeSnapshot { MicVolumePercent = 23 }) : null,
            "selected-input", (id, volume, muted) => writes.Add((id, volume, muted)),
            () => true, TestContext.Current.CancellationToken, sourceMuted);
        if (preserveVolume || sourceMuted.HasValue)
            Assert.Equal(("selected-input", preserveVolume ? (float?)23 : null, expectedMute), Assert.Single(writes));
        else Assert.Empty(writes);
    }

    [Fact]
    public async Task InputState_MuteStillApplies_WhenVolumeCaptureFails()
    {
        var writes = new List<(float? Volume, bool? Muted)>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostSwitchCoordinator.RestoreInputStateAsync(
            Task.FromException<SessionVolumeSnapshot>(new InvalidOperationException("Volume capture failed")),
            "selected-input", (_, volume, muted) => writes.Add((volume, muted)),
            () => true, TestContext.Current.CancellationToken, muted: true));
        Assert.Equal(((float?)null, (bool?)true), Assert.Single(writes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutineSwitch_PreservesEndpointMuteUnlessDeafened(bool deafen)
    {
        using var logger = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), "routine-mute.log");
        int writes = 0;
        await PostSwitchCoordinator.ExecuteAsync(() => false, logger.Logger, null!, "routine", "out", NRole.Console,
            false, false, deafen, false, false, false, null, TestContext.Current.CancellationToken,
            runMuteApplyWorkAsync: (_, _, _, _) => { writes++; return Task.CompletedTask; }, preserveEndpointMute: true);
        Assert.Equal(deafen ? 1 : 0, writes);
    }

    [Theory]
    [InlineData("switch-request")]
    [InlineData(null)]
    public async Task DetachedRestorationRetainsRequestTrace(string? requestTrace)
    {
        Func<CancellationToken, Task>? queued = null;
        Task completion;
        string? restoredTrace = null;
        using (AudioPilot.Logging.OperationTrace.Attach(requestTrace))
            completion = PostSwitchCoordinator.RunTrackedAsync(_ =>
            {
                restoredTrace = AudioPilot.Logging.OperationTrace.CurrentId;
                return Task.CompletedTask;
            }, work => { queued = work; return true; }, TestContext.Current.CancellationToken);
        Assert.Null(AudioPilot.Logging.OperationTrace.CurrentId);
        using (AudioPilot.Logging.OperationTrace.Attach("unrelated-worker-request"))
        {
            await queued!(TestContext.Current.CancellationToken);
            Assert.Equal("unrelated-worker-request", AudioPilot.Logging.OperationTrace.CurrentId);
        }
        await completion;
        Assert.NotNull(restoredTrace);
        Assert.NotEqual("unrelated-worker-request", restoredTrace);
        if (requestTrace != null)
            Assert.Equal(requestTrace, restoredTrace);
        Assert.Null(AudioPilot.Logging.OperationTrace.CurrentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputRestore_SkipsSupersededWork_BeforeOrAfterMutePass(bool supersededBeforeMute)
    {
        using var logger = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), "superseded-output.log");
        bool current = !supersededBeforeMute;
        int muteCalls = 0;
        await PostSwitchCoordinator.ExecuteAsync(() => false, logger.Logger, null!, "superseded", "intended-output", NRole.Console,
            false, false, false, true, true, false, new SessionVolumeSnapshot { MasterVolumePercent = 40 }, TestContext.Current.CancellationToken,
            runMuteApplyWorkAsync: (_, _, _, _) => { muteCalls++; current = false; return Task.CompletedTask; }, shouldContinue: () => current);
        Assert.Equal(supersededBeforeMute ? 0 : 1, muteCalls);
    }

    [Fact]
    public async Task TrackedCompletion_WaitsForQueuedRestoration_BeforeCallerCanDispose()
    {
        Func<CancellationToken, Task>? queued = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool restored = false;
        Task completion = PostSwitchCoordinator.RunTrackedAsync(async _ => { await release.Task; restored = true; }, work => { queued = work; return true; }, TestContext.Current.CancellationToken);
        Assert.False(completion.IsCompleted);
        Task worker = queued!(TestContext.Current.CancellationToken);
        Assert.False(completion.IsCompleted);
        release.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await worker;
        Assert.True(restored);
    }

    [Fact]
    public async Task TrackedCompletion_PropagatesFailureAndQueueRejection()
    {
        Func<CancellationToken, Task>? queued = null;
        Task completion = PostSwitchCoordinator.RunTrackedAsync(_ => throw new InvalidOperationException("Restore failed"), work => { queued = work; return true; }, TestContext.Current.CancellationToken);
        await queued!(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostSwitchCoordinator.RunTrackedAsync(_ => Task.CompletedTask, _ => false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrackedCompletion_DoesNotHang_WhenShutdownCancelsWorkBeforeItStarts()
    {
        using var cancellation = new CancellationTokenSource();
        Task completion = PostSwitchCoordinator.RunTrackedAsync(_ => Task.CompletedTask, _ => true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreInputStateAsync_ObservesSnapshotFailure_EvenWhenSuperseded()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostSwitchCoordinator.RestoreInputStateAsync(
            Task.FromException<SessionVolumeSnapshot>(new InvalidOperationException("Capture failed")),
            "old-target", (_, _, _) => Assert.Fail("A superseded request must not restore volume"),
            () => false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreInputStateAsync_UsesSelectedInput_WithoutPlaybackDependencies()
    {
        var applied = new List<(string Id, float Volume)>();
        await PostSwitchCoordinator.RestoreInputStateAsync(
            Task.FromResult(new SessionVolumeSnapshot { MicVolumePercent = 37f }),
            "selected-microphone", (id, volume, muted) => { Assert.Null(muted); applied.Add((id, volume!.Value)); },
            () => true, TestContext.Current.CancellationToken);

        Assert.Equal(("selected-microphone", 37f), Assert.Single(applied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreInputStateAsync_SkipsDelayedSnapshot_AfterSupersessionOrShutdown(bool shutdown)
    {
        using var cts = new CancellationTokenSource();
        var snapshot = new TaskCompletionSource<SessionVolumeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool current = true;
        int writes = 0;
        Task restore = PostSwitchCoordinator.RestoreInputStateAsync(
            snapshot.Task, "old-target", (_, _, _) => writes++, () => current, cts.Token, muted: true);
        if (shutdown) cts.Cancel();
        else current = false;
        snapshot.SetResult(new SessionVolumeSnapshot { MicVolumePercent = 80f });
        await restore.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsImmediately_WhenShutdownAlreadyCancelled()
    {
        using var loggerScope = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), "post-switch-cancelled.log");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await PostSwitchCoordinator.ExecuteAsync(
            shutdownToken: cts.Token,
            isDisposed: () => false,
            logger: loggerScope.Logger,
            volumeService: null!,
            opId: "testop",
            targetDeviceId: "unused",
            inputDetectionRole: NRole.Console,
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: false,
            restoreMasterVolume: true,
            restoreMicVolume: true,
            snapshot: null);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsImmediately_WhenServiceIsDisposed()
    {
        using var loggerScope = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), "post-switch-disposed.log");

        await PostSwitchCoordinator.ExecuteAsync(
            shutdownToken: CancellationToken.None,
            isDisposed: () => true,
            logger: loggerScope.Logger,
            volumeService: null!,
            opId: "testop",
            targetDeviceId: "unused",
            inputDetectionRole: NRole.Console,
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: true,
            restoreMasterVolume: true,
            restoreMicVolume: true,
            snapshot: new SessionVolumeSnapshot());
    }

    [Fact]
    public async Task ExecuteAsync_WhenMuteApplyBlocks_DoesNotBlockCoreAudioWorker()
    {
        await RunBlockedMuteApplyScenarioAsync("post-switch-coreaudio-isolation.log");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMuteApplyBlocks_RemainsStableAcrossRepeatedRuns()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await RunBlockedMuteApplyScenarioAsync($"post-switch-coreaudio-isolation-{iteration}.log");
        }
    }

    private static async Task RunBlockedMuteApplyScenarioAsync(string logFileName)
    {
        await ComThreadingHelper.WaitForCoreAudioWorkerReadyForTestsAsync();

        using var loggerScope = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), logFileName);
        using var muteApplyStarted = new ManualResetEventSlim(false);
        using var allowMuteApplyToFinish = new ManualResetEventSlim(false);

        Task postSwitchTask = Task.Run(() => PostSwitchCoordinator.ExecuteAsync(
            shutdownToken: CancellationToken.None,
            isDisposed: () => false,
            logger: loggerScope.Logger,
            volumeService: null!,
            opId: "testop",
            targetDeviceId: "unused",
            inputDetectionRole: NRole.Console,
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: false,
            restoreMasterVolume: true,
            restoreMicVolume: true,
            snapshot: null,
            runMuteApplyWorkAsync: (_, _, _, cancellationToken) =>
            {
                muteApplyStarted.Set();
                Assert.True(allowMuteApplyToFinish.Wait(TimeSpan.FromSeconds(5), cancellationToken));
                return Task.CompletedTask;
            }));

        Assert.True(muteApplyStarted.Wait(TimeSpan.FromSeconds(5)));

        await ComThreadingHelper.RunOnCoreAudioThreadAsync(() => { })
            .WaitAsync(TimeSpan.FromSeconds(1));

        allowMuteApplyToFinish.Set();
        await postSwitchTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

