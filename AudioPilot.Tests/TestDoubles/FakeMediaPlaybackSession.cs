using Windows.Media.Control;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeMediaPlaybackSession : IMediaPlaybackSession
{
    public bool IsPresent { get; set; } = true;
    public Task<bool> IsPresentAsync(CancellationToken cancellationToken) => Task.FromResult(IsPresent);
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status { get; set; } = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
    public bool CanPlay { get; set; } = true;
    public bool CanPause { get; set; } = true;
    public bool FailReadAfterSend { get; set; }
    public Func<bool, Task<bool>>? Send { get; set; }
    public Func<MediaPlaybackState>? Read { get; set; }
    public int RequestCount { get; private set; }
    public bool? LastRequest { get; private set; }

    public MediaPlaybackService CreateService() => new(_ => Task.FromResult<IMediaPlaybackSession?>(this), verificationTimeoutMs: 0);

    public static GlobalSystemMediaTransportControlsSessionPlaybackStatus Desired(bool playing) => playing
        ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

    public MediaPlaybackState ReadState()
    {
        if (FailReadAfterSend && RequestCount > 0) throw new InvalidOperationException("Session disappeared");
        return Read?.Invoke() ?? new(Status, CanPlay, CanPause);
    }

    public Task<bool> SetPlayingAsync(bool playing)
    {
        RequestCount++;
        LastRequest = playing;
        if (Send != null) return Send(playing);
        Status = Desired(playing);
        return Task.FromResult(true);
    }
}
