using AudioPilot.Logging;
using Windows.Foundation;
using Windows.Media.Control;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed partial class MediaOverlayEngine
    {
        private async Task<bool> DelayWithEventAssistIfWithinBudgetAsync(
            int delayMs,
            string? preferredSourceAppUserModelId,
            DateTimeOffset deadlineUtc,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            MediaOverlayDelayAssistResult result = await DelayWithEventAssistOutcomeIfWithinBudgetAsync(
                delayMs,
                preferredSourceAppUserModelId,
                deadlineUtc,
                commandSequence,
                cancellationToken);
            return result.CompletedWithinBudget;
        }

        private async Task<MediaOverlayDelayAssistResult> DelayWithEventAssistOutcomeIfWithinBudgetAsync(
            int delayMs,
            string? preferredSourceAppUserModelId,
            DateTimeOffset deadlineUtc,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            ThrowIfSuperseded(commandSequence, cancellationToken);
            _commandSnapshotCache.InvalidateSnapshots(commandSequence);

            if (_utcNow().AddMilliseconds(delayMs) > deadlineUtc)
            {
                return new MediaOverlayDelayAssistResult(false, false);
            }

            MediaEventAssistOutcome eventAssistOutcome = await WaitForRelevantMediaEventAsync(
                preferredSourceAppUserModelId,
                delayMs,
                commandSequence,
                cancellationToken);
            ThrowIfSuperseded(commandSequence, cancellationToken);

            if (eventAssistOutcome.ObservedEvent)
            {
                MarkRecentlySignaledSource(eventAssistOutcome.SignaledSourceAppUserModelId);
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"Observed GSMTC event during post-command wait source={LogPrivacy.Id(preferredSourceAppUserModelId)} signaledSource={LogPrivacy.Id(eventAssistOutcome.SignaledSourceAppUserModelId)} eventKind={eventAssistOutcome.EventKind} waitMs={delayMs}",
                    nameof(DelayWithEventAssistIfWithinBudgetAsync));
            }

            return new MediaOverlayDelayAssistResult(true, eventAssistOutcome.ObservedEvent, eventAssistOutcome);
        }

        private async Task<MediaEventAssistOutcome> WaitForRelevantMediaEventAsync(
            string? preferredSourceAppUserModelId,
            int maxWaitMs,
            long commandSequence,
            CancellationToken cancellationToken)
        {
            if (maxWaitMs <= 0)
            {
                return new MediaEventAssistOutcome(false, null);
            }

            if (_eventWaitOverride != null)
            {
                return await _eventWaitOverride(preferredSourceAppUserModelId, maxWaitMs, commandSequence, cancellationToken);
            }

            try
            {
                GsmtcEventWaiterRegistration registration = await GetOrCreateGsmtcEventWaiterRegistrationAsync(
                    commandSequence,
                    cancellationToken);

                MediaEventAssistOutcome outcome = await registration.Waiter.WaitAsync(
                    preferredSourceAppUserModelId,
                    maxWaitMs,
                    cancellationToken);
                ThrowIfSuperseded(commandSequence, cancellationToken);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance?.Trace(
                    "MediaOverlayHelper",
                    $"Failed to wait for GSMTC event assist source={LogPrivacy.Id(preferredSourceAppUserModelId)}. {ex.GetType().Name}",
                    nameof(WaitForRelevantMediaEventAsync));
                return new MediaEventAssistOutcome(false, null);
            }
        }

        private async Task<GsmtcEventWaiterRegistration> GetOrCreateGsmtcEventWaiterRegistrationAsync(
            long commandSequence,
            CancellationToken cancellationToken)
        {
            lock (_eventWaiterLock)
            {
                if (_eventWaitersByCommandSequence.TryGetValue(commandSequence, out GsmtcEventWaiterRegistration? existing))
                {
                    return existing;
                }
            }

            ThrowIfSuperseded(commandSequence, cancellationToken);
            GlobalSystemMediaTransportControlsSessionManager manager = await _commandSnapshotCache.GetManagerAsync(commandSequence, cancellationToken);
            ThrowIfSuperseded(commandSequence, cancellationToken);

            var created = new GsmtcEventWaiterRegistration(manager);
            lock (_eventWaiterLock)
            {
                if (_eventWaitersByCommandSequence.TryGetValue(commandSequence, out GsmtcEventWaiterRegistration? existing))
                {
                    created.Dispose();
                    return existing;
                }

                _eventWaitersByCommandSequence[commandSequence] = created;
                return created;
            }
        }

        private void ClearGsmtcEventWaiter(long commandSequence)
        {
            GsmtcEventWaiterRegistration? registration;
            lock (_eventWaiterLock)
            {
                if (_eventWaitersByCommandSequence.TryGetValue(commandSequence, out registration))
                {
                    _eventWaitersByCommandSequence.Remove(commandSequence);
                }
            }

            registration?.Dispose();
        }

        private sealed class GsmtcEventWaiterRegistration : IDisposable
        {
            private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
            private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = [];
            private readonly Lock _subscriptionLock = new();
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _currentSessionChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _sessionsChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _mediaPropertiesChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _playbackInfoChangedHandler;
            private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _timelinePropertiesChangedHandler;
            private int _disposed;

            public GsmtcEventWaiterRegistration(GlobalSystemMediaTransportControlsSessionManager manager)
            {
                _manager = manager;
                Waiter = new MediaOverlayCommandEventWaiter();

                _currentSessionChangedHandler = OnCurrentSessionChanged;
                _sessionsChangedHandler = OnSessionsChanged;
                _mediaPropertiesChangedHandler = OnMediaPropertiesChanged;
                _playbackInfoChangedHandler = OnPlaybackInfoChanged;
                _timelinePropertiesChangedHandler = OnTimelinePropertiesChanged;

                try
                {
                    _manager.CurrentSessionChanged += _currentSessionChangedHandler;
                    _manager.SessionsChanged += _sessionsChangedHandler;
                    RefreshSessionSubscriptions();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public MediaOverlayCommandEventWaiter Waiter { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                lock (_subscriptionLock)
                {
                    TryDetach("manager-current-session", () => _manager.CurrentSessionChanged -= _currentSessionChangedHandler);
                    TryDetach("manager-sessions", () => _manager.SessionsChanged -= _sessionsChangedHandler);
                    foreach (GlobalSystemMediaTransportControlsSession session in _sessions)
                    {
                        DetachSession(session);
                    }

                    _sessions.Clear();
                }

                Waiter.Dispose();
            }

            private void OnCurrentSessionChanged(
                GlobalSystemMediaTransportControlsSessionManager changedManager,
                CurrentSessionChangedEventArgs _args)
            {
                HandleCallback(
                    "current-session-changed",
                    () => Signal(MediaEventAssistKind.CurrentSessionChanged, CleanValue(changedManager.GetCurrentSession()?.SourceAppUserModelId)));
            }

            private void OnSessionsChanged(
                GlobalSystemMediaTransportControlsSessionManager changedManager,
                SessionsChangedEventArgs _args)
            {
                HandleCallback(
                    "sessions-changed",
                    () =>
                    {
                        RefreshSessionSubscriptions();
                        Signal(MediaEventAssistKind.SessionsChanged, CleanValue(changedManager.GetCurrentSession()?.SourceAppUserModelId));
                    });
            }

            private void OnMediaPropertiesChanged(
                GlobalSystemMediaTransportControlsSession session,
                MediaPropertiesChangedEventArgs _args)
            {
                HandleCallback(
                    "media-properties-changed",
                    () => Signal(MediaEventAssistKind.MediaPropertiesChanged, CleanValue(session.SourceAppUserModelId)));
            }

            private void OnPlaybackInfoChanged(
                GlobalSystemMediaTransportControlsSession session,
                PlaybackInfoChangedEventArgs _args)
            {
                HandleCallback(
                    "playback-info-changed",
                    () => Signal(MediaEventAssistKind.PlaybackInfoChanged, CleanValue(session.SourceAppUserModelId)));
            }

            private void OnTimelinePropertiesChanged(
                GlobalSystemMediaTransportControlsSession session,
                TimelinePropertiesChangedEventArgs _args)
            {
                HandleCallback(
                    "timeline-properties-changed",
                    () => Signal(MediaEventAssistKind.TimelinePropertiesChanged, CleanValue(session.SourceAppUserModelId)));
            }

            private void Signal(MediaEventAssistKind eventKind, string? sourceAppUserModelId)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                Waiter.Signal(new MediaEventAssistOutcome(true, sourceAppUserModelId, eventKind));
            }

            private void RefreshSessionSubscriptions()
            {
                lock (_subscriptionLock)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    IReadOnlyList<GlobalSystemMediaTransportControlsSession> currentSessions = _manager.GetSessions();
                    var currentSet = new HashSet<GlobalSystemMediaTransportControlsSession>(
                        currentSessions,
                        ReferenceEqualityComparer.Instance);

                    for (int index = _sessions.Count - 1; index >= 0; index--)
                    {
                        GlobalSystemMediaTransportControlsSession existing = _sessions[index];
                        if (currentSet.Contains(existing))
                        {
                            continue;
                        }

                        DetachSession(existing);
                        _sessions.RemoveAt(index);
                    }

                    var subscribedSet = new HashSet<GlobalSystemMediaTransportControlsSession>(
                        _sessions,
                        ReferenceEqualityComparer.Instance);
                    foreach (GlobalSystemMediaTransportControlsSession session in currentSessions)
                    {
                        if (subscribedSet.Add(session))
                        {
                            try
                            {
                                AttachSession(session);
                                _sessions.Add(session);
                            }
                            catch (Exception ex)
                            {
                                Logger.Instance?.Trace(
                                    "MediaOverlayHelper",
                                    $"media-event-waiter-session-attach-failed | reason={ex.GetType().Name}",
                                    nameof(RefreshSessionSubscriptions));
                            }
                        }
                    }
                }
            }

            private void AttachSession(GlobalSystemMediaTransportControlsSession session)
            {
                bool mediaAttached = false;
                bool playbackAttached = false;
                try
                {
                    session.MediaPropertiesChanged += _mediaPropertiesChangedHandler;
                    mediaAttached = true;
                    session.PlaybackInfoChanged += _playbackInfoChangedHandler;
                    playbackAttached = true;
                    session.TimelinePropertiesChanged += _timelinePropertiesChangedHandler;
                }
                catch
                {
                    if (playbackAttached)
                    {
                        TryDetach("session-playback-info", () => session.PlaybackInfoChanged -= _playbackInfoChangedHandler);
                    }

                    if (mediaAttached)
                    {
                        TryDetach("session-media-properties", () => session.MediaPropertiesChanged -= _mediaPropertiesChangedHandler);
                    }

                    throw;
                }
            }

            private void DetachSession(GlobalSystemMediaTransportControlsSession session)
            {
                TryDetach("session-media-properties", () => session.MediaPropertiesChanged -= _mediaPropertiesChangedHandler);
                TryDetach("session-playback-info", () => session.PlaybackInfoChanged -= _playbackInfoChangedHandler);
                TryDetach("session-timeline-properties", () => session.TimelinePropertiesChanged -= _timelinePropertiesChangedHandler);
            }

            private static void HandleCallback(string eventName, Action callback)
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    Logger.Instance?.Trace(
                        "MediaOverlayHelper",
                        $"media-event-waiter-callback-failed | event={eventName} reason={ex.GetType().Name}",
                        nameof(GsmtcEventWaiterRegistration));
                }
            }

            private static void TryDetach(string eventName, Action detach)
            {
                try
                {
                    detach();
                }
                catch (Exception ex)
                {
                    Logger.Instance?.Trace(
                        "MediaOverlayHelper",
                        $"media-event-waiter-detach-failed | event={eventName} reason={ex.GetType().Name}",
                        nameof(GsmtcEventWaiterRegistration));
                }
            }
        }
    }
}
