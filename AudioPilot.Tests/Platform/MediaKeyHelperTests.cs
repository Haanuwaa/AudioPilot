using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

[Collection("MediaKeyHelperIsolation")]
public sealed class MediaKeyHelperTests
{
    [Theory]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, true, true, true, "Toggle")]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, false, true, true, "Pause")]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, false, true, false, "None")]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, false, true, true, "Play")]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused, false, false, true, "None")]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped, false, false, true, "None")]
    [InlineData(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed, false, false, false, "None")]
    public void SelectPlayPauseOperation_UsesAdvertisedExplicitCapability(
        Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus status,
        bool canToggle,
        bool canPlay,
        bool canPause,
        string expected)
    {
        Assert.Equal(expected, MediaKeyHelper.SelectPlayPauseOperation(status, canToggle, canPlay, canPause).ToString());
    }

    [Fact]
    public void TryPressNextTrack_UsesSystemMediaCommandBeforeNativeSend()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static command =>
                command == MediaKeyHelper.SystemMediaCommand.NextTrack;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            bool sent = MediaKeyHelper.TryPressNextTrack();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public async Task TryPressNextTrackAsync_UsesSystemMediaCommandBeforeNativeSend()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static command =>
                command == MediaKeyHelper.SystemMediaCommand.NextTrack;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            bool sent = await MediaKeyHelper.TryPressNextTrackAsync(TestContext.Current.CancellationToken);

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public async Task TryPressNextTrackDetailedAsync_WhenCallerAlreadyCancelled_DoesNotInvokeAnyRoute()
    {
        int systemCommandCount = 0;
        int nativeFallbackCount = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = _ =>
            {
                Interlocked.Increment(ref systemCommandCount);
                return false;
            };
            MediaKeyHelper.SendInputOverrideForTests = _ =>
            {
                Interlocked.Increment(ref nativeFallbackCount);
                return (2u, 0);
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => MediaKeyHelper.TryPressNextTrackDetailedAsync(cancellation.Token));

            Assert.Equal(0, systemCommandCount);
            Assert.Equal(0, nativeFallbackCount);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrackDetailed_ReportsSystemMediaOverrideRoute()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static command =>
                command == MediaKeyHelper.SystemMediaCommand.NextTrack;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressNextTrackDetailed();

            Assert.True(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.TestOverride, outcome.Route);
            Assert.False(outcome.UsedSendInputFallback);
            Assert.Null(outcome.FailureReason);
            Assert.True(outcome.ElapsedMs >= 0);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrackDetailed_HonorsExplicitFallbackSuppression_WhenDetailedCommandRequestsIt()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.ControllableGsmc,
                SuppressFallback: true,
                CandidateSourceAppUserModelId: "Spotify.exe",
                FailureReason: "fallback-suppressed");
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("native fallback should not run");

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressNextTrackDetailed();

            Assert.False(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.ControllableGsmc, outcome.Route);
            Assert.True(outcome.SuppressFallback);
            Assert.False(outcome.UsedSendInputFallback);
            Assert.Equal("fallback-suppressed", outcome.FailureReason);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrackDetailed_UsesNativeFallback_WhenNoSystemMediaCandidateExists()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressNextTrackDetailed();

            Assert.True(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.SendInputFallback, outcome.Route);
            Assert.True(outcome.UsedSendInputFallback);
            Assert.Null(outcome.FailureReason);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void NativeFallback_StampsSyntheticInputMarker()
    {
        nuint observedMarker = 0;
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.DetailedSendInputOverrideForTests = (_, marker) =>
            {
                observedMarker = marker;
                return (2u, 0);
            };

            Assert.True(MediaKeyHelper.TryPressPlayPause());
            Assert.Equal(AppConstants.Hotkeys.SyntheticMediaInputMarker, observedMarker);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPlayPauseDetailed_UsesNativeFallback_WhenNoSystemMediaCandidateExists()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressPlayPauseDetailed();

            Assert.True(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.SendInputFallback, outcome.Route);
            Assert.True(outcome.UsedSendInputFallback);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPreviousTrackDetailed_ReportsNativeFallbackFailure()
    {
        try
        {
            MediaKeyHelper.DetailedSystemMediaCommandOverrideForTests = static _ => new MediaKeyHelper.MediaCommandSendOutcome(
                Sent: false,
                MediaKeyHelper.MediaCommandRouteKind.None,
                FailureReason: "no-system-media-candidate");
            MediaKeyHelper.SendInputOverrideForTests = static _ => (1u, 5);

            MediaKeyHelper.MediaCommandSendOutcome outcome = MediaKeyHelper.TryPressPreviousTrackDetailed();

            Assert.False(outcome.Sent);
            Assert.Equal(MediaKeyHelper.MediaCommandRouteKind.SendInputFallback, outcome.Route);
            Assert.True(outcome.UsedSendInputFallback);
            Assert.Equal("sendinput-partial", outcome.FailureReason);
            Assert.Equal(5, outcome.ErrorCode);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public async Task ProbeSystemMediaManagerAsync_ReacquiresManagerAfterCachedManagerBecomesUnusable()
    {
        int managerRequestCount = 0;
        try
        {
            MediaKeyHelper.SystemMediaManagerRequestOverrideForTests = () =>
            {
                managerRequestCount++;
                return Task.FromResult<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager>(null!);
            };
            bool firstProbeSucceeded = await MediaKeyHelper.ProbeSystemMediaManagerForTestsAsync();
            bool secondProbeSucceeded = await MediaKeyHelper.ProbeSystemMediaManagerForTestsAsync();

            Assert.False(firstProbeSucceeded);
            Assert.False(secondProbeSucceeded);
            Assert.Equal(2, managerRequestCount);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public async Task ProbeSystemMediaManagerAsync_ReacquiresAfterPendingRequestIsCancelled()
    {
        int managerRequestCount = 0;
        try
        {
            MediaKeyHelper.SystemMediaManagerRequestOverrideForTests = () =>
            {
                managerRequestCount++;
                return new TaskCompletionSource<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager>(
                    TaskCreationOptions.RunContinuationsAsynchronously).Task;
            };

            Assert.False(await MediaKeyHelper.ProbeCancelledSystemMediaManagerForTestsAsync());
            Assert.False(await MediaKeyHelper.ProbeCancelledSystemMediaManagerForTestsAsync());
            Assert.Equal(2, managerRequestCount);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPlayPause_FallsBackToNativeSend_WhenSystemMediaCommandDoesNotHandle()
    {
        try
        {
            MediaKeyHelper.SystemMediaCommandOverrideForTests = static _ => false;
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            bool sent = MediaKeyHelper.TryPressPlayPause();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPlayPause_ReturnsTrue_WhenNativeSendSucceeds()
    {
        try
        {
            MediaKeyHelper.SendInputOverrideForTests = static _ => (2u, 0);

            bool sent = MediaKeyHelper.TryPressPlayPause();

            Assert.True(sent);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressNextTrack_LogsFailure_WhenNativeSendReturnsPartialCount()
    {
        using var logScope = new TestLoggerScope(nameof(TryPressNextTrack_LogsFailure_WhenNativeSendReturnsPartialCount), "mediakey.log");

        try
        {
            MediaKeyHelper.LoggerOverrideForTests = logScope.Logger;
            MediaKeyHelper.SendInputOverrideForTests = static _ => (1u, 5);

            bool sent = MediaKeyHelper.TryPressNextTrack();

            Assert.False(sent);

            string logText = logScope.DisposeAndReadLogText();
            Assert.Contains("media-key-send-failed:NextTrack", logText, StringComparison.Ordinal);
            Assert.Contains("Win32Exception", logText, StringComparison.Ordinal);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

    [Fact]
    public void TryPressPreviousTrack_LogsException_WhenNativeSendThrows()
    {
        using var logScope = new TestLoggerScope(nameof(TryPressPreviousTrack_LogsException_WhenNativeSendThrows), "mediakey.log");

        try
        {
            MediaKeyHelper.LoggerOverrideForTests = logScope.Logger;
            MediaKeyHelper.SendInputOverrideForTests = static _ => throw new InvalidOperationException("boom");

            bool sent = MediaKeyHelper.TryPressPreviousTrack();

            Assert.False(sent);

            string logText = logScope.DisposeAndReadLogText();
            Assert.Contains("media-key-send-exception:PreviousTrack", logText, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", logText, StringComparison.Ordinal);
        }
        finally
        {
            MediaKeyHelper.ResetTestHooks();
        }
    }

}
