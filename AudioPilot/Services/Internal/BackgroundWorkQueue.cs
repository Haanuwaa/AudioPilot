using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.Services.Internal
{
    internal sealed class BackgroundWorkQueue(
        Logger logger,
        Func<bool> isCleaningUp,
        int maxActiveTasks = AppConstants.Limits.MaxConcurrentBackgroundTasks,
        int maxDeferredOperations = AppConstants.Limits.MaxDeferredBackgroundOperations)
    {
        private readonly record struct DeferredOperation(
            Func<CancellationToken, Task> Operation, string Name, string? CoalescingKey, Action? OnDiscarded);

        private readonly Logger _logger = logger;
        private readonly Func<bool> _isCleaningUp = isCleaningUp;
        private readonly int _maxActiveTasks = Math.Max(1, maxActiveTasks);
        private readonly int _maxDeferredOperations = Math.Max(1, maxDeferredOperations);
        private readonly Lock _deferredGate = new();
        private readonly LinkedList<DeferredOperation> _deferredOperations = new();
        private int _backgroundTaskId;
        private bool _isDrainingDeferredOperations;
        private bool _saturationLogged;
        private bool _overflowLogged;

        internal int DeferredOperationCount
        {
            get
            {
                lock (_deferredGate)
                {
                    return _deferredOperations.Count;
                }
            }
        }

        /// <summary>Retains independent requests unless a coalescing key opts into replacement; discarded work is settled outside the queue lock.</summary>
        public bool TryQueue(
            ConcurrentDictionary<int, Task> backgroundTasks,
            CancellationTokenSource backgroundWorkCts,
            Func<CancellationToken, Task> operation,
            string operationName,
            string? coalescingKey = null,
            Action? onDiscarded = null)
        {
            var request = new DeferredOperation(operation, operationName, coalescingKey, onDiscarded);
            CancellationToken token;
            try { token = backgroundWorkCts.Token; }
            catch (ObjectDisposedException) { NotifyDiscarded(request); return false; }

            DeferredOperation? discarded = null;
            bool accepted = false;
            bool logSaturation = false;
            bool logOverflow = false;
            bool coalesced = false;
            bool drainDeferred;
            lock (_deferredGate)
            {
                // Synchronous task completion must defer draining until admission releases the queue lock.
                bool wasDraining = _isDrainingDeferredOperations;
                _isDrainingDeferredOperations = true;
                try
                {
                    if (!_isCleaningUp() && !token.IsCancellationRequested)
                    {
                        bool queueSaturated = false;
                        accepted = TryQueueCore(backgroundTasks, backgroundWorkCts, operation, operationName,
                            () => queueSaturated = true);
                        if (!accepted && queueSaturated)
                        {
                            if (_deferredOperations.Count < _maxDeferredOperations)
                            {
                                _deferredOperations.AddLast(request);
                                accepted = true;
                            }
                            else
                            {
                                LinkedListNode<DeferredOperation>? candidate = coalescingKey == null ? null : _deferredOperations.Last;
                                while (candidate != null && !string.Equals(candidate.Value.CoalescingKey, coalescingKey, StringComparison.Ordinal))
                                    candidate = candidate.Previous;
                                if (candidate != null)
                                {
                                    discarded = candidate.Value;
                                    candidate.Value = request;
                                    accepted = true;
                                    coalesced = true;
                                }
                                if (!_overflowLogged) { _overflowLogged = true; logOverflow = true; }
                            }
                            if (!_saturationLogged) { _saturationLogged = true; logSaturation = true; }
                        }
                    }
                    if (!accepted) discarded = request;
                }
                finally { _isDrainingDeferredOperations = wasDraining; }
                drainDeferred = _deferredOperations.Count > 0;
            }

            if (discarded is { } dropped) NotifyDiscarded(dropped);
            if (logSaturation)
                _logger.Warning("BackgroundWorkQueue", () => $"background-queue-saturated | action=defer maxActive={_maxActiveTasks}");
            if (logOverflow)
                _logger.Warning("BackgroundWorkQueue", () => $"background-deferred-queue-saturated | operation={operationName} action={(coalesced ? "coalesce-latest" : "drop-newest")} maxDeferred={_maxDeferredOperations}");
            if (drainDeferred && (accepted || token.IsCancellationRequested || _isCleaningUp()))
                DrainDeferredOperations(backgroundTasks, backgroundWorkCts);
            return accepted;
        }

        public void ClearDeferredOperations()
        {
            DeferredOperation[] discarded;
            lock (_deferredGate)
            {
                discarded = [.. _deferredOperations];
                _deferredOperations.Clear();
                _saturationLogged = false;
                _overflowLogged = false;
            }
            foreach (DeferredOperation operation in discarded) NotifyDiscarded(operation);
        }

        private bool TryQueueCore(
            ConcurrentDictionary<int, Task> backgroundTasks,
            CancellationTokenSource backgroundWorkCts,
            Func<CancellationToken, Task> operation,
            string operationName,
            Action? onQueueSaturated = null)
        {
            return BackgroundTaskHelper.TryQueueWithPolicy(
                backgroundTasks,
                ref _backgroundTaskId,
                backgroundWorkCts,
                _isCleaningUp,
                operation,
                operationName,
                (name, ex) =>
                {
                    if (!_isCleaningUp())
                    {
                        _logger.Error("BackgroundWorkQueue", () => $"background-operation-failed | operation={name} error={ex.GetType().Name}", name, ex);
                    }
                },
                _maxActiveTasks,
                _ => onQueueSaturated?.Invoke(),
                () => DrainDeferredOperations(backgroundTasks, backgroundWorkCts));
        }

        private void NotifyDiscarded(DeferredOperation operation)
        {
            try { operation.OnDiscarded?.Invoke(); }
            catch (Exception ex)
            {
                _logger.Error("BackgroundWorkQueue", () => $"background-discard-callback-failed | operation={operation.Name} error={ex.GetType().Name}", operation.Name, ex);
            }
        }

        private void DrainDeferredOperations(
            ConcurrentDictionary<int, Task> backgroundTasks,
            CancellationTokenSource backgroundWorkCts)
        {
            List<DeferredOperation>? discarded = null;
            lock (_deferredGate)
            {
                if (_isDrainingDeferredOperations)
                {
                    return;
                }

                _isDrainingDeferredOperations = true;
                try
                {
                    while (_deferredOperations.First is LinkedListNode<DeferredOperation> first)
                    {
                        if (_isCleaningUp() || backgroundWorkCts.IsCancellationRequested)
                        {
                            (discarded ??= []).AddRange(_deferredOperations);
                            _deferredOperations.Clear();
                            break;
                        }

                        DeferredOperation deferred = first.Value;
                        bool queueSaturated = false;
                        if (!TryQueueCore(
                            backgroundTasks,
                            backgroundWorkCts,
                            deferred.Operation,
                            deferred.Name,
                            () => queueSaturated = true))
                        {
                            if (!queueSaturated)
                            {
                                _deferredOperations.RemoveFirst();
                                (discarded ??= []).Add(deferred);
                                continue;
                            }
                            break;
                        }

                        _deferredOperations.RemoveFirst();
                    }

                    if (_deferredOperations.Count == 0)
                    {
                        _saturationLogged = false;
                        _overflowLogged = false;
                    }
                }
                finally
                {
                    _isDrainingDeferredOperations = false;
                }
            }
            if (discarded != null)
                foreach (DeferredOperation operation in discarded) NotifyDiscarded(operation);
        }
    }
}
