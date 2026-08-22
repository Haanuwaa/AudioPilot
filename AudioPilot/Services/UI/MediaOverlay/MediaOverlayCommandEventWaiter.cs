namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlayCommandEventWaiter : IDisposable
    {
        private const int MaxObservedEvents = 32;

        private readonly Lock _lock = new();
        private readonly List<MediaEventAssistOutcome> _observedEvents = [];
        private readonly List<WaitRegistration> _waiters = [];
        private bool _disposed;

        internal int PendingWaiterCountForTests
        {
            get
            {
                lock (_lock)
                {
                    return _waiters.Count;
                }
            }
        }

        internal int ObservedEventCountForTests
        {
            get
            {
                lock (_lock)
                {
                    return _observedEvents.Count;
                }
            }
        }

        public void Signal(MediaEventAssistOutcome outcome)
        {
            if (!outcome.ObservedEvent)
            {
                return;
            }

            List<WaitRegistration> matchedWaiters = [];
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                for (int index = _waiters.Count - 1; index >= 0; index--)
                {
                    WaitRegistration waiter = _waiters[index];
                    if (!IsRelevantForPreferredSource(outcome, waiter.PreferredSourceAppUserModelId))
                    {
                        continue;
                    }

                    _waiters.RemoveAt(index);
                    matchedWaiters.Add(waiter);
                }

                if (matchedWaiters.Count == 0)
                {
                    _observedEvents.Add(outcome);
                    if (_observedEvents.Count > MaxObservedEvents)
                    {
                        _observedEvents.RemoveAt(0);
                    }
                }
            }

            foreach (WaitRegistration waiter in matchedWaiters)
            {
                waiter.Completion.TrySetResult(outcome);
            }
        }

        public async Task<MediaEventAssistOutcome> WaitAsync(
            string? preferredSourceAppUserModelId,
            int maxWaitMs,
            CancellationToken cancellationToken)
        {
            if (maxWaitMs <= 0)
            {
                return new MediaEventAssistOutcome(false, null);
            }

            WaitRegistration registration;
            lock (_lock)
            {
                if (_disposed)
                {
                    return new MediaEventAssistOutcome(false, null);
                }

                MediaEventAssistOutcome? observed = TryTakeObservedEventUnderLock(preferredSourceAppUserModelId);
                if (observed.HasValue)
                {
                    return observed.Value;
                }

                registration = new WaitRegistration(
                    preferredSourceAppUserModelId,
                    new TaskCompletionSource<MediaEventAssistOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
                _waiters.Add(registration);
            }

            try
            {
                Task completed = await Task.WhenAny(
                    registration.Completion.Task,
                    Task.Delay(maxWaitMs, cancellationToken)).ConfigureAwait(false);

                if (completed == registration.Completion.Task &&
                    registration.Completion.Task.IsCompletedSuccessfully)
                {
                    return registration.Completion.Task.Result;
                }

                return new MediaEventAssistOutcome(false, null);
            }
            finally
            {
                lock (_lock)
                {
                    _waiters.Remove(registration);
                }
            }
        }

        public void Dispose()
        {
            List<WaitRegistration> pending;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                pending = [.. _waiters];
                _waiters.Clear();
                _observedEvents.Clear();
            }

            foreach (WaitRegistration waiter in pending)
            {
                waiter.Completion.TrySetResult(new MediaEventAssistOutcome(false, null));
            }
        }

        internal static bool IsRelevantForPreferredSource(MediaEventAssistOutcome outcome, string? preferredSourceAppUserModelId)
        {
            return string.IsNullOrWhiteSpace(preferredSourceAppUserModelId)
                || string.Equals(outcome.SignaledSourceAppUserModelId, preferredSourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
        }

        private MediaEventAssistOutcome? TryTakeObservedEventUnderLock(string? preferredSourceAppUserModelId)
        {
            for (int index = _observedEvents.Count - 1; index >= 0; index--)
            {
                MediaEventAssistOutcome observed = _observedEvents[index];
                if (IsRelevantForPreferredSource(observed, preferredSourceAppUserModelId))
                {
                    _observedEvents.RemoveAt(index);
                    return observed;
                }
            }

            return null;
        }

        private readonly record struct WaitRegistration(
            string? PreferredSourceAppUserModelId,
            TaskCompletionSource<MediaEventAssistOutcome> Completion);
    }
}
