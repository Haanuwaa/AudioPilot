using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class EndpointVolumeStepGateTests
{
    [Fact]
    public async Task Execute_SerializesOperationsForTheSameEndpointKind()
    {
        var gate = new EndpointVolumeStepGate();
        using var firstEntered = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        using var secondAttempting = new ManualResetEventSlim(false);
        using var secondEntered = new ManualResetEventSlim(false);

        Task first = Task.Run(() => gate.Execute(AudioMixerMode.Output, () =>
        {
            firstEntered.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Task second = Task.Run(() =>
        {
            secondAttempting.Set();
            return gate.Execute(AudioMixerMode.Output, () =>
            {
                secondEntered.Set();
                return true;
            });
        });
        Assert.True(secondAttempting.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));

        releaseFirst.Set();
        await Task.WhenAll(first, second);

        Assert.True(secondEntered.IsSet);
    }

    [Fact]
    public async Task Execute_AllowsPlaybackAndRecordingOperationsToProceedIndependently()
    {
        var gate = new EndpointVolumeStepGate();
        using var playbackEntered = new ManualResetEventSlim(false);
        using var releasePlayback = new ManualResetEventSlim(false);
        using var recordingEntered = new ManualResetEventSlim(false);

        Task playback = Task.Run(() => gate.Execute(AudioMixerMode.Output, () =>
        {
            playbackEntered.Set();
            Assert.True(releasePlayback.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }));
        Assert.True(playbackEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Task recording = Task.Run(() => gate.Execute(AudioMixerMode.Input, () =>
        {
            recordingEntered.Set();
            return true;
        }));

        Assert.True(recordingEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        releasePlayback.Set();
        await Task.WhenAll(playback, recording);
    }
}
