using System.Diagnostics;
using AudioPilot.Logging;
using Windows.Media.Control;

namespace AudioPilot.Services.Audio
{
    public sealed record MediaPlaybackResult(
        bool Success, string Code, string Message, string RequestedState,
        string? ObservedState = null, bool RequestSent = false, bool Confirmed = false);

    internal readonly record struct MediaPlaybackState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus Status, bool CanPlay, bool CanPause);

    internal interface IMediaPlaybackSession
    {
        MediaPlaybackState ReadState();
        Task<bool> SetPlayingAsync(bool playing);
    }

    /// <summary>Serializes explicit playback requests and prevents resubmission while a previous request is pending.</summary>
    internal sealed class MediaPlaybackService(
        Func<CancellationToken, Task<IMediaPlaybackSession?>>? sessionProvider = null,
        Logger? logger = null,
        int timeoutMs = 2000,
        int verificationTimeoutMs = 300)
    {
        private readonly Func<CancellationToken, Task<IMediaPlaybackSession?>> _sessionProvider = sessionProvider ?? MediaKeyHelper.GetPlaybackSessionAsync;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<bool>? _submittedRequest;

        public async Task<MediaPlaybackResult> SetPlayingAsync(bool playing, CancellationToken cancellationToken = default)
        {
            string requested = playing ? "Playing" : "Paused";
            string action = playing ? "Play" : "Pause";
            var desired = playing ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            string? observed = null;
            bool sent = false;
            bool entered = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);

            MediaPlaybackResult Result(bool success, string code, string message, bool confirmed = false) =>
                new(success, $"media-playback-{code}", message, requested, observed, sent, confirmed);

            try
            {
                await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (_submittedRequest is { IsCompleted: false })
                {
                    return Result(false, "busy", "A previous playback request is still pending.");
                }

                _submittedRequest = null;
                IMediaPlaybackSession? session;
                try
                {
                    session = await _sessionProvider(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    return Result(false, "ambiguous-session", "Windows has not designated an unambiguous media session.");
                }

                if (session == null)
                {
                    return Result(false, "no-session", "No media session detected.");
                }

                deadline.Token.ThrowIfCancellationRequested();
                MediaPlaybackState state = session.ReadState();
                observed = state.Status.ToString();
                if (state.Status == desired)
                {
                    return Result(true, "already-in-state", $"Playback is already {requested.ToLowerInvariant()}.", confirmed: true);
                }

                if (!(playing ? state.CanPlay : state.CanPause))
                {
                    return Result(false, "unsupported", $"The selected media session does not support explicit {action.ToLowerInvariant()}.");
                }

                deadline.Token.ThrowIfCancellationRequested();
                _submittedRequest = session.SetPlayingAsync(playing);
                sent = true;
                _ = _submittedRequest.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (!await _submittedRequest.WaitAsync(deadline.Token).ConfigureAwait(false))
                {
                    return Result(false, "rejected", $"The media session rejected the {action.ToLowerInvariant()} request.");
                }

                long verificationStarted = Stopwatch.GetTimestamp();
                try
                {
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        observed = session.ReadState().Status.ToString();
                        if (observed == requested)
                        {
                            return Result(true, "confirmed", $"Playback {requested.ToLowerInvariant()}.", confirmed: true);
                        }

                        double remaining = verificationTimeoutMs - Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds;
                        if (remaining <= 0) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, remaining)), deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger?.Debug("MediaPlaybackService", () => $"media-playback-verification-unavailable | error={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
                }

                return Result(true, "accepted", $"{action} request accepted; playback state has not been confirmed.");
            }
            catch (OperationCanceledException)
            {
                return cancellationToken.IsCancellationRequested
                    ? Result(false, "canceled", sent ? "Playback request canceled after submission; its outcome is unknown." : "Playback request canceled.")
                    : Result(false, "timeout", sent ? "Playback request timed out after submission; its outcome is unknown." : "Playback request timed out.");
            }
            catch (Exception ex)
            {
                logger?.Warning("MediaPlaybackService", $"media-playback-operation-failed | error={ex.GetType().Name} hresult=0x{ex.HResult:X8}");
                return Result(false, "failed", sent ? "Playback request failed after submission; its outcome is unknown." : "Playback request failed.");
            }
            finally
            {
                if (entered)
                {
                    if (_submittedRequest is { IsCompleted: true }) _submittedRequest = null;
                    _gate.Release();
                }
            }
        }
    }
}
