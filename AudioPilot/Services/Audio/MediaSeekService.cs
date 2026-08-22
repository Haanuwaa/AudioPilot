using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Audio
{
    public sealed record MediaSeekResult(bool Success, string Code, string Message, double? TargetPositionSeconds = null, double? PreviousPositionSeconds = null)
    {
        internal MediaSeekTrackIdentity? Track { get; init; }

        internal string? PositionChangeText => Success && PreviousPositionSeconds is double previous && TargetPositionSeconds is double target
            ? $"{FormatPosition(previous)} → {FormatPosition(target)}" : null;

        internal static string FormatPosition(double seconds)
        {
            long totalSeconds = (long)seconds;
            return totalSeconds >= 3600
                ? FormattableString.Invariant($"{totalSeconds / 3600}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}")
                : FormattableString.Invariant($"{totalSeconds / 60}:{totalSeconds % 60:00}");
        }
    }

    internal sealed record MediaSeekTrackIdentity(string Title, string Artist, string Album, int TrackNumber);

    internal readonly record struct MediaSeekTimeline(
        bool CanSeek, bool IsPlaying, double PlaybackRate, TimeSpan Position,
        TimeSpan Minimum, TimeSpan Maximum, DateTimeOffset UpdatedAt, MediaSeekTrackIdentity? TrackIdentity);

    internal interface IMediaSeekSession
    {
        object Identity { get; }
        Task<MediaSeekTimeline> ReadAsync(CancellationToken cancellationToken);
        Task<bool> SeekAsync(long positionTicks);
    }

    /// <summary>Serializes relative seeks and briefly retains accepted positions while a player's timeline catches up.</summary>
    internal sealed class MediaSeekService(
        Func<CancellationToken, Task<IMediaSeekSession?>>? sessionProvider = null,
        TimeProvider? timeProvider = null,
        Logger? logger = null,
        int timeoutMs = 2000)
    {
        private readonly Func<CancellationToken, Task<IMediaSeekSession?>> _sessionProvider = sessionProvider ?? MediaKeyHelper.GetSeekSessionAsync;
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<bool>? _submittedCommand;
        private PendingPosition? _pending;
        private int _generation;

        private sealed record PendingPosition(object Session, MediaSeekTimeline Timeline, TimeSpan Target, DateTimeOffset SentAt, DateTimeOffset EvidenceExpiresAt, int Generation);

        internal void InvalidatePosition()
        {
            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _pending, null);
        }

        public async Task<MediaSeekResult> SeekAsync(int offsetSeconds, CancellationToken cancellationToken = default)
        {
            if (offsetSeconds == 0 || offsetSeconds < -MediaSeekStep.MaximumSeconds || offsetSeconds > MediaSeekStep.MaximumSeconds)
            {
                return new(false, "media-seek-invalid-step", $"Use a seek step between 1 and {MediaSeekStep.MaximumSeconds} seconds.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            bool entered = false;
            bool submitted = false;
            MediaSeekResult result;
            try
            {
                await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (_submittedCommand is { IsCompleted: false })
                {
                    return new(false, "media-seek-busy", "Previous seek is still pending");
                }

                _submittedCommand = null;
                IMediaSeekSession? session = await _sessionProvider(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (session == null)
                {
                    _pending = null;
                    return new(false, "media-seek-no-session", "No media session detected");
                }

                MediaSeekTimeline timeline = await session.ReadAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (!timeline.CanSeek)
                {
                    _pending = null;
                    logger?.Info("MediaSeekService", "media-seek-unavailable | reason=position-control-disabled");
                    return new(false, "media-seek-unavailable", "Seeking unavailable");
                }

                if (timeline.Minimum < TimeSpan.Zero || timeline.Maximum <= timeline.Minimum)
                {
                    _pending = null;
                    logger?.Info("MediaSeekService", () => FormattableString.Invariant(
                        $"media-seek-timeline-unavailable | positionSeconds={timeline.Position.TotalSeconds:F3} minimumSeconds={timeline.Minimum.TotalSeconds:F3} maximumSeconds={timeline.Maximum.TotalSeconds:F3}"));
                    return new(false, "media-seek-timeline-unavailable", "Player timeline unavailable");
                }

                DateTimeOffset now = _time.GetUtcNow();
                int generation = Volatile.Read(ref _generation);
                double position = ProjectPosition(timeline.Position, timeline, timeline.UpdatedAt, now);
                PendingPosition? previous = _pending;
                _pending = null;
                DateTimeOffset evidenceExpiresAt = now.AddSeconds(2);
                if (previous is { } pending && Equals(pending.Session, session.Identity)
                    && pending.Generation == generation && timeline.TrackIdentity != null
                    && timeline.TrackIdentity == pending.Timeline.TrackIdentity
                    && timeline.Minimum == pending.Timeline.Minimum && timeline.Maximum == pending.Timeline.Maximum
                    && timeline.IsPlaying == pending.Timeline.IsPlaying && timeline.PlaybackRate == pending.Timeline.PlaybackRate
                    && timeline.Position == pending.Timeline.Position && timeline.UpdatedAt == pending.Timeline.UpdatedAt
                    && now >= pending.SentAt)
                {
                    _pending = pending;
                    if (now >= pending.EvidenceExpiresAt)
                    {
                        return new(false, "media-seek-position-pending", "Waiting for player position");
                    }

                    evidenceExpiresAt = pending.EvidenceExpiresAt;
                    position = ProjectPosition(pending.Target, timeline, pending.SentAt, now);
                }

                double targetSeconds = Math.Clamp(position + offsetSeconds, timeline.Minimum.TotalSeconds, timeline.Maximum.TotalSeconds);
                TimeSpan target = targetSeconds >= timeline.Maximum.TotalSeconds ? timeline.Maximum
                    : targetSeconds <= timeline.Minimum.TotalSeconds ? timeline.Minimum : TimeSpan.FromSeconds(targetSeconds);
                if (Math.Abs(target.TotalSeconds - position) < 0.001)
                {
                    return new(true, "media-seek-limit", offsetSeconds > 0 ? "End of seekable media" : "Start of seekable media", target.TotalSeconds, position)
                    {
                        Track = timeline.TrackIdentity,
                    };
                }

                deadline.Token.ThrowIfCancellationRequested();
                _submittedCommand = session.SeekAsync(target.Ticks);
                submitted = true;
                _ = _submittedCommand.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                bool accepted = await _submittedCommand.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (accepted) _pending = new(session.Identity, timeline, target, now, evidenceExpiresAt, generation);
                string direction = offsetSeconds > 0 ? "forward" : "backward";
                result = accepted
                    ? new(true, "media-seek-accepted", $"Seek {direction}: {MediaSeekResult.FormatPosition(position)} → {MediaSeekResult.FormatPosition(target.TotalSeconds)}", target.TotalSeconds, position)
                    {
                        Track = timeline.TrackIdentity,
                    }
                    : new(false, "media-seek-rejected", "Player could not seek");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                if (entered) _pending = null;
                result = new(false, submitted ? "media-seek-unconfirmed" : cancellationToken.IsCancellationRequested ? "media-seek-canceled" : "media-seek-timeout",
                    submitted ? "Seek result unknown" : cancellationToken.IsCancellationRequested ? "Seek canceled" : "Seeking timed out");
            }
            catch (NotSupportedException)
            {
                if (entered) _pending = null;
                result = new(false, "media-seek-unavailable", "Seeking unavailable");
            }
            catch (Exception ex)
            {
                if (entered) _pending = null;
                logger?.Warning("MediaSeekService", "media-seek-failed", nameof(SeekAsync), ex);
                result = new(false, "media-seek-failed", "Seeking failed");
            }
            finally
            {
                if (entered) _gate.Release();
            }

            return result;
        }

        private static double ProjectPosition(TimeSpan position, MediaSeekTimeline timeline, DateTimeOffset updatedAt, DateTimeOffset now)
        {
            double elapsed = updatedAt > DateTimeOffset.UnixEpoch && updatedAt <= now ? (now - updatedAt).TotalSeconds : 0;
            double rate = timeline.IsPlaying && double.IsFinite(timeline.PlaybackRate) ? timeline.PlaybackRate : 0;
            return Math.Clamp(position.TotalSeconds + elapsed * rate, timeline.Minimum.TotalSeconds, timeline.Maximum.TotalSeconds);
        }
    }
}
