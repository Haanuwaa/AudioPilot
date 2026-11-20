using AudioPilot.Tests.TestDoubles;
using static AudioPilot.Tests.TestDoubles.FakeMediaPlaybackSession;

namespace AudioPilot.Tests.Services;

public sealed class MediaPlaybackServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlreadyRequestedState_SucceedsWithoutSendingEvenWhenControlIsDisabled(bool playing)
    {
        var session = new FakeMediaPlaybackSession { Status = Desired(playing), CanPlay = false, CanPause = false };
        MediaPlaybackResult result = await session.CreateService().SetPlayingAsync(playing, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Confirmed);
        Assert.False(result.RequestSent);
        Assert.Equal("media-playback-already-in-state", result.Code);
        Assert.Equal(0, session.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendsOnlyExplicitRequestedAction_AndConfirmsReadback(bool playing)
    {
        var session = new FakeMediaPlaybackSession { Status = Desired(!playing) };
        MediaPlaybackResult result = await session.CreateService().SetPlayingAsync(playing, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Confirmed);
        Assert.True(result.RequestSent);
        Assert.Equal(playing, session.LastRequest);
        Assert.Equal(1, session.RequestCount);
        Assert.Equal(Desired(playing).ToString(), result.ObservedState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedRequestedAction_DoesNotSendOppositeAction(bool playing)
    {
        var session = new FakeMediaPlaybackSession { Status = Desired(!playing), CanPlay = !playing, CanPause = playing };
        MediaPlaybackResult result = await session.CreateService().SetPlayingAsync(playing, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("media-playback-unsupported", result.Code);
        Assert.False(result.RequestSent);
        Assert.Equal(0, session.RequestCount);
    }

    [Fact]
    public async Task AcceptedRequestWithoutStateChange_IsNotClaimedAsConfirmed()
    {
        var session = new FakeMediaPlaybackSession { Send = _ => Task.FromResult(true) };
        MediaPlaybackResult result = await session.CreateService().SetPlayingAsync(true, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.RequestSent);
        Assert.False(result.Confirmed);
        Assert.Equal("media-playback-accepted", result.Code);
        Assert.Equal("Paused", result.ObservedState);
    }

    [Fact]
    public async Task ReadbackFailure_PreservesAcceptedRequestWithoutRetry()
    {
        var session = new FakeMediaPlaybackSession { FailReadAfterSend = true };
        MediaPlaybackResult result = await session.CreateService().SetPlayingAsync(true, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.Confirmed);
        Assert.Equal("media-playback-accepted", result.Code);
        Assert.Equal(1, session.RequestCount);
    }

    [Fact]
    public async Task VerificationWaitsForDelayedStateWithoutResending()
    {
        int reads = 0;
        var session = new FakeMediaPlaybackSession
        {
            Send = _ => Task.FromResult(true),
            Read = () => new(Desired(++reads >= 3), true, true),
        };
        var service = new MediaPlaybackService(_ => Task.FromResult<IMediaPlaybackSession?>(session));

        MediaPlaybackResult result = await service.SetPlayingAsync(true, TestContext.Current.CancellationToken);

        Assert.True(result.Confirmed);
        Assert.Equal(3, reads);
        Assert.Equal(1, session.RequestCount);
    }

    [Fact]
    public async Task RejectedRequest_IsFailureWithoutRetry()
    {
        var session = new FakeMediaPlaybackSession { Send = _ => Task.FromResult(false) };
        MediaPlaybackResult result = await session.CreateService().SetPlayingAsync(true, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("media-playback-rejected", result.Code);
        Assert.True(result.RequestSent);
        Assert.Equal(1, session.RequestCount);
    }

    [Fact]
    public async Task NoSessionAndAmbiguousSession_ReturnDistinctFailures()
    {
        var missing = new MediaPlaybackService(_ => Task.FromResult<IMediaPlaybackSession?>(null));
        var ambiguous = new MediaPlaybackService(_ => throw new NotSupportedException());

        Assert.Equal("media-playback-no-session", (await missing.SetPlayingAsync(true, TestContext.Current.CancellationToken)).Code);
        Assert.Equal("media-playback-ambiguous-session", (await ambiguous.SetPlayingAsync(true, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public async Task SessionDiscoveryTimeout_DoesNotClaimRequestWasSent()
    {
        var pending = new TaskCompletionSource<IMediaPlaybackSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MediaPlaybackService(_ => pending.Task, timeoutMs: 50);

        MediaPlaybackResult result = await service.SetPlayingAsync(true, TestContext.Current.CancellationToken);
        pending.SetResult(null);

        Assert.Equal("media-playback-timeout", result.Code);
        Assert.False(result.RequestSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedSessionReleasesPendingPlayback_WithoutUsingItsLateResult(bool lateFault)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        var oldSession = new FakeMediaPlaybackSession { Send = _ => { canceled.Cancel(); return completion.Task; } };
        FakeMediaPlaybackSession selected = oldSession;
        var service = new MediaPlaybackService(_ => Task.FromResult<IMediaPlaybackSession?>(selected), verificationTimeoutMs: 0);
        try
        {
            Assert.Equal("media-playback-canceled", (await service.SetPlayingAsync(true, canceled.Token)).Code);
            selected = new FakeMediaPlaybackSession();
            Assert.Equal("media-playback-busy", (await service.SetPlayingAsync(true, TestContext.Current.CancellationToken)).Code);
            Assert.Equal(0, selected.RequestCount);

            oldSession.IsPresent = false;
            Assert.True((await service.SetPlayingAsync(true, TestContext.Current.CancellationToken)).Confirmed);
            if (lateFault) completion.SetException(new InvalidOperationException("Retired player failed"));
            else completion.SetResult(true);
            Assert.True((await service.SetPlayingAsync(false, TestContext.Current.CancellationToken)).Confirmed);
            Assert.Equal(1, oldSession.RequestCount);
            Assert.Equal(2, selected.RequestCount);
        }
        finally
        {
            completion.TrySetResult(false);
        }
    }

    [Fact]
    public async Task SubmittedTimeout_BlocksFurtherCommandsUntilPendingRequestCompletes()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeMediaPlaybackSession { Send = _ => pending.Task };
        var service = new MediaPlaybackService(_ => Task.FromResult<IMediaPlaybackSession?>(session), timeoutMs: 50, verificationTimeoutMs: 0);

        MediaPlaybackResult first = await service.SetPlayingAsync(true, TestContext.Current.CancellationToken);
        MediaPlaybackResult second = await service.SetPlayingAsync(false, TestContext.Current.CancellationToken);
        pending.SetException(new InvalidOperationException("Late provider failure"));
        session.Send = null;
        MediaPlaybackResult recovered = await service.SetPlayingAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal("media-playback-timeout", first.Code);
        Assert.True(first.RequestSent);
        Assert.False(first.Confirmed);
        Assert.Equal("media-playback-busy", second.Code);
        Assert.False(second.RequestSent);
        Assert.True(recovered.Confirmed);
        Assert.Equal(2, session.RequestCount);
    }

    [Fact]
    public async Task CancellationBeforeDiscovery_DoesNotReadOrSend()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int discoveries = 0;
        var service = new MediaPlaybackService(_ => { discoveries++; return Task.FromResult<IMediaPlaybackSession?>(null); });

        MediaPlaybackResult result = await service.SetPlayingAsync(true, cancellation.Token);

        Assert.Equal("media-playback-canceled", result.Code);
        Assert.Equal(0, discoveries);
        Assert.False(result.RequestSent);
    }

    [Fact]
    public async Task CancellationAfterSubmission_KeepsPendingGuard()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeMediaPlaybackSession { Send = _ => { cancellation.Cancel(); return pending.Task; } };
        var service = session.CreateService();

        MediaPlaybackResult result = await service.SetPlayingAsync(true, cancellation.Token);
        MediaPlaybackResult next = await service.SetPlayingAsync(false, TestContext.Current.CancellationToken);
        pending.SetResult(true);

        Assert.Equal("media-playback-canceled", result.Code);
        Assert.True(result.RequestSent);
        Assert.Equal("media-playback-busy", next.Code);
        Assert.Equal(1, session.RequestCount);
    }

    [Fact]
    public void SelectionPreservesCurrentSession_AndRejectsAmbiguousFallbacks()
    {
        var first = new object();
        var second = new object();
        Assert.Same(first, MediaKeyHelper.SelectSingleMediaSession([first, second], first, _ => true).Session);
        Assert.Same(second, MediaKeyHelper.SelectSingleMediaSession([first, second], null, item => ReferenceEquals(item, second)).Session);
        Assert.Same(first, MediaKeyHelper.SelectSingleMediaSession([first], null, _ => false).Session);
        Assert.Null(MediaKeyHelper.SelectSingleMediaSession<object>([], null, _ => false).Session);
        Assert.Throws<NotSupportedException>(() => MediaKeyHelper.SelectSingleMediaSession([first, second], null, _ => true));
        Assert.Throws<NotSupportedException>(() => MediaKeyHelper.SelectSingleMediaSession([first, second], null, _ => false));
    }
}
