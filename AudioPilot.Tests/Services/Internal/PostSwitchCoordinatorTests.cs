using AudioPilot.Tests.Helpers;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Internal;

[Collection("CoreAudioWorkerIsolation")]
public sealed class PostSwitchCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutineSwitch_PreservesEndpointMuteUnlessDeafened(bool deafen)
    {
        using var logger = new TestLoggerScope(nameof(PostSwitchCoordinatorTests), "routine-mute.log");
        int writes = 0;
        await PostSwitchCoordinator.ExecuteAsync(() => false, logger.Logger, null!, "routine", "out", NRole.Console,
            false, false, deafen, false, false, false, null, TestContext.Current.CancellationToken,
            runMuteApplyWorkAsync: _ => { writes++; return Task.CompletedTask; }, preserveEndpointMute: true);
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
            runMuteApplyWorkAsync: _ => { muteCalls++; current = false; return Task.CompletedTask; }, shouldContinue: () => current);
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
    public async Task RestoreInputVolumeAsync_ObservesSnapshotFailure_EvenWhenSuperseded()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostSwitchCoordinator.RestoreInputVolumeAsync(
            Task.FromException<SessionVolumeSnapshot>(new InvalidOperationException("Capture failed")),
            "old-target", (_, _) => Assert.Fail("A superseded request must not restore volume"),
            () => false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestoreInputVolumeAsync_UsesSelectedInput_WithoutPlaybackDependencies()
    {
        var applied = new List<(string Id, float Volume)>();
        await PostSwitchCoordinator.RestoreInputVolumeAsync(
            Task.FromResult(new SessionVolumeSnapshot { MicVolumePercent = 37f }),
            "selected-microphone", (id, volume) => applied.Add((id, volume)),
            () => true, TestContext.Current.CancellationToken);

        Assert.Equal(("selected-microphone", 37f), Assert.Single(applied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreInputVolumeAsync_SkipsDelayedSnapshot_AfterSupersessionOrShutdown(bool shutdown)
    {
        using var cts = new CancellationTokenSource();
        var snapshot = new TaskCompletionSource<SessionVolumeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool current = true;
        int writes = 0;
        Task restore = PostSwitchCoordinator.RestoreInputVolumeAsync(
            snapshot.Task, "old-target", (_, _) => writes++, () => current, cts.Token);
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
            runMuteApplyWorkAsync: cancellationToken =>
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

