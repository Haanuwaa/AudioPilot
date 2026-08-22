using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceServicePerAppResetTests
{
    [Fact]
    public async Task Reset_RejectsDisposedAndCanceledRequestsWithoutChangingAssignments()
    {
        using var logger = new TestLoggerScope(nameof(AudioDeviceServicePerAppResetTests), "reset-admission.log");
        var resetter = new FakePerAppAudioRoutingResetter();
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter(), perAppAudioRoutingResetter: resetter, logger: logger.Logger);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audio.ResetAllPerAppAudioRoutingAsync(canceled.Token));
        }
        finally { await audio.DisposeAsync(); }

        Assert.Throws<ObjectDisposedException>(() => audio.ResetAllPerAppAudioRouting());
        Assert.Equal(0, resetter.ResetAllCalls);
    }

    [Fact]
    public async Task Disposal_JoinsAnAlreadyStartedReset()
    {
        using var logger = new TestLoggerScope(nameof(AudioDeviceServicePerAppResetTests), "reset-drain.log");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetter = new FakePerAppAudioRoutingResetter
        {
            ResetOverride = () =>
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                return new PerAppAudioRoutingResetResult(true, true);
            },
        };
        var audio = new AudioDeviceService(new FakeInputListenPropertyWriter(), perAppAudioRoutingResetter: resetter, logger: logger.Logger);
        Task<PerAppAudioRoutingResetResult> reset = audio.ResetAllPerAppAudioRoutingAsync(TestContext.Current.CancellationToken);
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Contains(audio.BackgroundTasksForTests.Values, task => !task.IsCompleted);
            disposal = audio.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.Set();
            await (disposal ?? audio.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        Assert.True((await reset).Success);
        Assert.Equal(1, resetter.ResetAllCalls);
    }
}
