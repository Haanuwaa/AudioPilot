using AudioPilot.Tests.Helpers;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Internal;

[Collection("CoreAudioWorkerIsolation")]
public sealed class PostSwitchCoordinatorTests
{
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

