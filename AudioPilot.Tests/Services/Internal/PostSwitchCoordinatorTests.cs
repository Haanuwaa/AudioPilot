using AudioPilot.Tests.Helpers;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Internal;

[Collection("CoreAudioWorkerIsolation")]
public sealed class PostSwitchCoordinatorTests
{
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

