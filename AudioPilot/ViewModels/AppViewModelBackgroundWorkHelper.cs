using System.Collections.Concurrent;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;

namespace AudioPilot.ViewModels
{
    internal sealed class AppViewModelBackgroundWorkHelper(
        Logger logger,
        Func<bool> isCleaningUp,
        int maxActiveTasks = AppConstants.Limits.MaxConcurrentBackgroundTasks,
        int maxDeferredOperations = AppConstants.Limits.MaxDeferredBackgroundOperations)
    {
        private readonly record struct DeferredOperation(Func<CancellationToken, Task> Operation, string Name);

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

        internal int DeferredOperationCountForTests
        {
            get
            {
                lock (_deferredGate)
                {
                    return _deferredOperations.Count;
                }
            }
        }

        public bool TryQueue(
            ConcurrentDictionary<int, Task> backgroundTasks,
            CancellationTokenSource backgroundWorkCts,
            Func<CancellationToken, Task> operation,
            string operationName)
        {
            lock (_deferredGate)
            {
                bool queueSaturated = false;
                bool queued = TryQueueCore(
                    backgroundTasks,
                    backgroundWorkCts,
                    operation,
                    operationName,
                    () => queueSaturated = true);

                if (queued)
                {
                    return true;
                }

                return queueSaturated && TryDeferOperation(
                    backgroundTasks,
                    backgroundWorkCts,
                    operation,
                    operationName);
            }
        }

        public void ClearDeferredOperations()
        {
            lock (_deferredGate)
            {
                _deferredOperations.Clear();
                _saturationLogged = false;
                _overflowLogged = false;
            }
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
                        _logger.Error("AppViewModel", () => $"background-operation-failed | operation={name} error={ex.GetType().Name}", name, ex);
                    }
                },
                _maxActiveTasks,
                _ => onQueueSaturated?.Invoke(),
                () => DrainDeferredOperations(backgroundTasks, backgroundWorkCts));
        }

        private bool TryDeferOperation(
            ConcurrentDictionary<int, Task> backgroundTasks,
            CancellationTokenSource backgroundWorkCts,
            Func<CancellationToken, Task> operation,
            string operationName)
        {
            bool accepted = false;
            bool logSaturation = false;
            bool logOverflow = false;
            bool coalesced = false;

            lock (_deferredGate)
            {
                if (_isCleaningUp())
                {
                    return false;
                }

                if (_deferredOperations.Count < _maxDeferredOperations)
                {
                    _deferredOperations.AddLast(new DeferredOperation(operation, operationName));
                    accepted = true;
                }
                else
                {
                    LinkedListNode<DeferredOperation>? candidate = _deferredOperations.Last;
                    while (candidate != null && !string.Equals(candidate.Value.Name, operationName, StringComparison.Ordinal))
                    {
                        candidate = candidate.Previous;
                    }

                    if (candidate != null)
                    {
                        candidate.Value = new DeferredOperation(operation, operationName);
                        accepted = true;
                        coalesced = true;
                    }

                    if (!_overflowLogged)
                    {
                        _overflowLogged = true;
                        logOverflow = true;
                    }
                }

                if (!_saturationLogged)
                {
                    _saturationLogged = true;
                    logSaturation = true;
                }
            }

            if (logSaturation && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.Warning(
                    "AppViewModel",
                    () => $"background-queue-saturated | action=defer maxActive={_maxActiveTasks}");
            }

            if (logOverflow && _logger.IsEnabled(LogLevel.Warning))
            {
                string action = coalesced ? "coalesce-latest" : "drop-newest";
                _logger.Warning(
                    "AppViewModel",
                    () => $"background-deferred-queue-saturated | operation={operationName} action={action} maxDeferred={_maxDeferredOperations}");
            }

            if (accepted)
            {
                DrainDeferredOperations(backgroundTasks, backgroundWorkCts);
            }

            return accepted;
        }

        private void DrainDeferredOperations(
            ConcurrentDictionary<int, Task> backgroundTasks,
            CancellationTokenSource backgroundWorkCts)
        {
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
                        if (_isCleaningUp())
                        {
                            _deferredOperations.Clear();
                            return;
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
                            }

                            return;
                        }

                        _deferredOperations.RemoveFirst();
                    }

                    _saturationLogged = false;
                    _overflowLogged = false;
                }
                finally
                {
                    _isDrainingDeferredOperations = false;
                }
            }
        }
    }
}
