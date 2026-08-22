using System.Diagnostics;
using System.Globalization;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Coordinators
{
    internal sealed partial class AppCliOverlayCoordinator
    {
        private readonly MediaSeekService _mediaSeekService = mediaSeekService ?? new(logger: logger);
        private int _queuedSeeks;

        public Task<MediaSeekResult> MediaSeekAsync(bool backward, int? seconds = null, string source = CliSource)
        {
            lock (_mediaLifecycleLock)
            {
                if (Volatile.Read(ref _mediaShutdownStarted) != 0)
                {
                    return Task.FromResult(new MediaSeekResult(false, "media-seek-canceled", "Seek canceled"));
                }

                int step = seconds ?? MediaSeekStep.Normalize(currentSettingsProvider()?.Hotkeys.Media.SeekStepSeconds ?? MediaSeekStep.DefaultSeconds);
                if (step is < 1 or > MediaSeekStep.MaximumSeconds)
                {
                    return Task.FromResult(new MediaSeekResult(false, "media-seek-invalid-step", "Use a seek step between 1 and 3600 seconds."));
                }

                if (_queuedSeeks >= 16)
                {
                    logger.Info("MediaSeekService", () => $"media-seek-queue-full | capacity=16 source={NormalizeMediaCommandSource(source)}");
                    return Task.FromResult(new MediaSeekResult(false, "media-seek-busy", "Seeking is busy"));
                }

                _queuedSeeks++;
                int version = Interlocked.Increment(ref _latestMediaOverlayRequestVersion);
                lock (_mediaSendOrderLock)
                {
                    Task predecessor = _mediaSendOrderTail;
                    Task<MediaSeekResult> operation = ProcessSeekAsync(predecessor, backward ? -step : step, version, NormalizeMediaCommandSource(source));
                    _mediaSendOrderTail = Task.WhenAll(predecessor, operation).ContinueWith(static _ => { }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    TrackMediaOperation(operation);
                    return operation;
                }
            }
        }

        private async Task<MediaSeekResult> ProcessSeekAsync(Task predecessor, int offset, int version, string source)
        {
            long started = Stopwatch.GetTimestamp();
            using var queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(_mediaShutdownCts.Token);
            queueDeadline.CancelAfter(3000);
            await Task.Yield();
            MediaSeekResult result;
            try
            {
                await predecessor.WaitAsync(queueDeadline.Token).ConfigureAwait(false);
                queueDeadline.Token.ThrowIfCancellationRequested();
                result = await _mediaSeekService.SeekAsync(offset, queueDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = _mediaShutdownCts.IsCancellationRequested
                    ? new(false, "media-seek-canceled", "Seek canceled")
                    : new(false, "media-seek-timeout", "Seeking timed out");
            }
            catch (Exception ex)
            {
                logger.Warning("MediaSeekService", "media-seek-operation-failed", nameof(ProcessSeekAsync), ex);
                result = new(false, "media-seek-failed", "Seeking failed");
            }
            finally
            {
                lock (_mediaLifecycleLock) _queuedSeeks--;
            }

            try
            {
                if (Volatile.Read(ref _mediaShutdownStarted) == 0)
                {
                    if (version == Volatile.Read(ref _latestMediaOverlayRequestVersion))
                    {
                        if (result.PositionChangeText is { } positionChange)
                        {
                            string header = result.Code == "media-seek-limit" ? result.Message : offset > 0 ? "Seek forward" : "Seek backward";
                            overlay.ShowMediaTrack(header, result.Track?.Title ?? "Track information unavailable", result.Track?.Artist, positionChange);
                        }
                        else
                        {
                            overlay.Show(result.Message);
                        }
                    }
                    logger.Log(result.Success ? LogLevel.Debug : LogLevel.Info, "MediaSeekService",
                        () => FormattableString.Invariant($"media-seek-result | code={result.Code} offsetSeconds={offset} previousSeconds={result.PreviousPositionSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "none"} targetSeconds={result.TargetPositionSeconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "none"} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} source={source}"));
                    mediaHistoryRecorder?.Invoke(new ExecutionHistoryEntry(
                        OpId: $"media-seek:{Guid.NewGuid():N}", TimestampUtc: DateTimeOffset.UtcNow,
                        Kind: ExecutionHistoryKind.Media, Source: source,
                        Action: offset > 0 ? "media-seek-forward" : "media-seek-backward",
                        Success: result.Success, Skipped: false, Summary: result.Message,
                        Reason: result.Success ? null : result.Message, DiagCode: result.Code,
                        ElapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                }
            }
            catch (Exception ex)
            {
                logger.Warning("MediaSeekService", "media-seek-feedback-failed", nameof(ProcessSeekAsync), ex);
            }

            return result;
        }
    }
}
