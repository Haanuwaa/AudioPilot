using System.Diagnostics;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppCliOverlayCoordinator
    {
        private readonly MediaPlaybackService _mediaPlaybackService = mediaPlaybackService ?? new(logger: logger);
        private int _queuedPlaybackChanges;

        public Task<MediaPlaybackResult> MediaSetPlayingAsync(bool playing)
        {
            lock (_mediaLifecycleLock)
            {
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return Task.FromResult(new MediaPlaybackResult(false, "media-playback-canceled", "Playback request canceled.", playing ? "Playing" : "Paused"));
                }

                if (_queuedPlaybackChanges >= 16)
                {
                    logger.Info("MediaPlaybackService", "media-playback-queue-full | capacity=16 source=cli");
                    return Task.FromResult(new MediaPlaybackResult(false, "media-playback-busy", "Playback request queue is full.", playing ? "Playing" : "Paused"));
                }

                _queuedPlaybackChanges++;
                Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
                lock (_mediaSendOrderLock)
                {
                    Task predecessor = _mediaSendOrderTail;
                    Task<MediaPlaybackResult> operation = Task.Run(() => ProcessPlaybackChangeAsync(predecessor, playing));
                    _mediaSendOrderTail = Task.WhenAll(predecessor, operation).ContinueWith(static _ => { }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    TrackMediaOperation(operation);
                    return operation;
                }
            }
        }

        private async Task<MediaPlaybackResult> ProcessPlaybackChangeAsync(Task predecessor, bool playing)
        {
            long started = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_mediaShutdownCts.Token);
            deadline.CancelAfter(3000);
            MediaPlaybackResult result;
            try
            {
                await predecessor.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                result = await _mediaPlaybackService.SetPlayingAsync(playing, deadline.Token).ConfigureAwait(false);
                if (result.RequestSent) _mediaSeekService.InvalidatePosition();
                if (result.Code == "media-playback-canceled" && !_mediaShutdownCts.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    result = result with { Code = "media-playback-timeout", Message = result.RequestSent ? "Playback request timed out after submission; its outcome is unknown." : "Playback request timed out." };
                }
            }
            catch (OperationCanceledException)
            {
                result = new(false, _mediaShutdownCts.IsCancellationRequested ? "media-playback-canceled" : "media-playback-timeout",
                    _mediaShutdownCts.IsCancellationRequested ? "Playback request canceled." : "Playback request timed out in the queue.", playing ? "Playing" : "Paused");
            }
            catch (Exception ex)
            {
                logger.Warning("MediaPlaybackService", "media-playback-queue-failed", nameof(ProcessPlaybackChangeAsync), ex);
                result = new(false, "media-playback-failed", "Playback request failed.", playing ? "Playing" : "Paused");
            }
            finally
            {
                lock (_mediaLifecycleLock) _queuedPlaybackChanges--;
            }

            try
            {
                logger.Log(result.Success ? LogLevel.Debug : LogLevel.Info, "MediaPlaybackService",
                    () => FormattableString.Invariant($"media-playback-result | code={result.Code} requested={result.RequestedState} observed={result.ObservedState ?? "unknown"} requestSent={result.RequestSent} confirmed={result.Confirmed} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} source=cli"));
                mediaHistoryRecorder?.Invoke(new ExecutionHistoryEntry(
                    OpId: $"media-playback:{Guid.NewGuid():N}", TimestampUtc: DateTimeOffset.UtcNow,
                    Kind: ExecutionHistoryKind.Media, Source: CliSource, Action: playing ? "media-play" : "media-pause",
                    Success: result.Success, Skipped: result.Success && !result.RequestSent, Summary: result.Message,
                    Reason: result.Success ? null : result.Message, DiagCode: result.Code, ElapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
            catch (Exception ex)
            {
                logger.Warning("MediaPlaybackService", "media-playback-history-failed", nameof(ProcessPlaybackChangeAsync), ex);
            }

            return result;
        }
    }
}
