using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    public static partial class ComThreadingHelper
    {
        private const int COINIT_MULTITHREADED = 0x0;

        private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        private const int E_FAIL = unchecked((int)0x80004005);

        private const int S_OK = 0;
        private const int S_FALSE = 1;

        [LibraryImport("ole32.dll")]
        private static partial int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [ThreadStatic]
        private static bool t_comInitialized;

        [ThreadStatic]
        private static bool t_loggedChangedMode;

        [ThreadStatic]
        private static bool t_isCoreAudioWorker;

        private static readonly Lazy<CoreAudioComExecutor> s_coreAudioExecutor =
            new(() => new CoreAudioComExecutor("AudioPilot.CoreAudioCOM"));

        private static int s_coreAudioExecutorDisposed;

        internal static AudioWorkerHealthSnapshot CaptureRuntimeHealth() => s_coreAudioExecutor.IsValueCreated
            ? s_coreAudioExecutor.Value.CaptureHealth()
            : new(false, false, Volatile.Read(ref s_coreAudioExecutorDisposed) != 0, 0, CoreAudioComExecutor.MaxPendingWorkItems, false);

        public static bool EnsureComInitialized()
        {
            if (t_comInitialized)
                return true;

            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            if (hr == S_OK || hr == S_FALSE)
            {
                t_comInitialized = true;
                return true;
            }

            if (hr == RPC_E_CHANGED_MODE)
            {
                t_comInitialized = true;
                if (!t_loggedChangedMode)
                {
                    Logger.Instance.Warning("ComThreadingHelper",
                        "CoInitializeEx returned RPC_E_CHANGED_MODE; continuing on existing apartment configuration");
                    t_loggedChangedMode = true;
                }
                return true;
            }

            Logger.Instance.Warning("ComThreadingHelper",
                () => $"CoInitializeEx returned unexpected HRESULT: 0x{hr:X8}");
            return false;
        }

        public static void ThrowIfComInitializationFailed(string operationName)
        {
            if (EnsureComInitialized())
                return;

            throw new COMException($"Failed to initialize COM for operation '{operationName}'", E_FAIL);
        }

        public static void RunOnCoreAudioThread(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            ObjectDisposedException.ThrowIf(Volatile.Read(ref s_coreAudioExecutorDisposed) != 0, "CoreAudioComExecutor");

            if (t_isCoreAudioWorker)
            {
                ThrowIfComInitializationFailed(nameof(RunOnCoreAudioThread));
                action();
                return;
            }

            s_coreAudioExecutor.Value.Invoke(action, CancellationToken.None);
        }

        public static T RunOnCoreAudioThread<T>(Func<T> function)
        {
            ArgumentNullException.ThrowIfNull(function);

            ObjectDisposedException.ThrowIf(Volatile.Read(ref s_coreAudioExecutorDisposed) != 0, "CoreAudioComExecutor");

            if (t_isCoreAudioWorker)
            {
                ThrowIfComInitializationFailed(nameof(RunOnCoreAudioThread));
                return function();
            }

            return s_coreAudioExecutor.Value.Invoke(function, CancellationToken.None);
        }

        public static Task RunOnCoreAudioThreadAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (Volatile.Read(ref s_coreAudioExecutorDisposed) != 0)
                return Task.FromException(new ObjectDisposedException("CoreAudioComExecutor"));

            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            if (t_isCoreAudioWorker)
            {
                ThrowIfComInitializationFailed(nameof(RunOnCoreAudioThreadAsync));
                action();
                return Task.CompletedTask;
            }

            return s_coreAudioExecutor.Value.Enqueue(action, cancellationToken);
        }

        /// <summary>
        /// Executes a function on the dedicated CoreAudio COM worker and returns its result asynchronously.
        /// </summary>
        /// <remarks>
        /// If the caller is already running on the CoreAudio worker, the function executes inline after COM state is
        /// validated. Otherwise the work is queued onto the shared executor. Disposal is checked before enqueue so
        /// shutdown paths fail fast instead of silently dropping work.
        /// </remarks>
        public static Task<T> RunOnCoreAudioThreadAsync<T>(Func<T> function, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(function);

            if (Volatile.Read(ref s_coreAudioExecutorDisposed) != 0)
                return Task.FromException<T>(new ObjectDisposedException("CoreAudioComExecutor"));

            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);

            if (t_isCoreAudioWorker)
            {
                ThrowIfComInitializationFailed(nameof(RunOnCoreAudioThreadAsync));
                return Task.FromResult(function());
            }

            return s_coreAudioExecutor.Value.Enqueue(function, cancellationToken);
        }

        public static void DisposeCoreAudioExecutor()
        {
            if (Interlocked.Exchange(ref s_coreAudioExecutorDisposed, 1) != 0)
                return;

            if (!s_coreAudioExecutor.IsValueCreated)
                return;

            s_coreAudioExecutor.Value.Dispose();
        }

        internal static Task ForceCoreAudioWorkerFailureForTestsAsync()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref s_coreAudioExecutorDisposed) != 0, "CoreAudioComExecutor");

            return s_coreAudioExecutor.Value.EnqueueWorkerFailureForTests();
        }

        internal static Task WaitForCoreAudioWorkerReadyForTestsAsync()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref s_coreAudioExecutorDisposed) != 0, "CoreAudioComExecutor");

            return s_coreAudioExecutor.Value.WaitForWorkerReadyForTestsAsync();
        }

        /// <summary>
        /// Queues CoreAudio work onto a dedicated COM-initialized worker thread.
        /// </summary>
        /// <remarks>
        /// The executor starts its worker lazily, bounds queued work to avoid unbounded growth, and drains queued
        /// operations during disposal so COM-sensitive audio work stays serialized on one thread.
        /// </remarks>
        internal sealed class CoreAudioComExecutor : IDisposable
        {
            internal const int MaxPendingWorkItems = 2048;
            private readonly BlockingCollection<IWorkItem> _queue =
                new(new ConcurrentQueue<IWorkItem>(), MaxPendingWorkItems);
            private readonly Lock _workerLock = new();
            private readonly Lock _healthLock = new();
            private readonly TimeProvider _timeProvider;
            private bool _busy;
            private long _activeStarted;
            private DateTimeOffset? _lastCompletedUtc;
            private readonly TaskCompletionSource<bool> _shutdownSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Thread? _worker;
            private volatile bool _disposed;
            private readonly string _threadName;
            private Exception? _lastWorkerFailure;
            private TaskCompletionSource<bool> _workerReadySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<bool> _workerExitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CoreAudioComExecutor(string threadName, TimeProvider? timeProvider = null)
            {
                _threadName = threadName;
                _timeProvider = timeProvider ?? TimeProvider.System;
                EnsureWorkerStarted();
            }

            internal AudioWorkerHealthSnapshot CaptureHealth()
            {
                lock (_healthLock)
                {
                    int queued;
                    try { queued = _queue.Count; }
                    catch (ObjectDisposedException) { queued = 0; }
                    return new(true, Volatile.Read(ref _worker)?.IsAlive == true, _disposed, queued, MaxPendingWorkItems,
                        _busy, _busy ? Math.Round(_timeProvider.GetElapsedTime(_activeStarted).TotalMilliseconds, 1) : null, _lastCompletedUtc);
                }
            }

            public void Invoke(Action action, CancellationToken cancellationToken)
            {
                ObserveWorkTaskAsync(Enqueue(action, cancellationToken)).GetAwaiter().GetResult();
            }

            public T Invoke<T>(Func<T> function, CancellationToken cancellationToken)
            {
                return ObserveWorkTaskAsync(Enqueue(function, cancellationToken)).GetAwaiter().GetResult();
            }

            public Task Enqueue(Action action, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromCanceled(cancellationToken);

                lock (_workerLock)
                {
                    EnsureWorkerStarted();

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var item = new ActionWorkItem(action, tcs, cancellationToken);
                    item.RegisterCancellation();
                    if (item.IsPending && !_queue.TryAdd(item))
                    {
                        item.Fail(new InvalidOperationException("CoreAudio COM executor queue is full"));
                    }

                    return tcs.Task;
                }
            }

            private Task ObserveWorkTaskAsync(Task workTask)
            {
                if (workTask.IsCompleted)
                {
                    return workTask;
                }

                Task shutdownTask = _shutdownSource.Task;
                if (shutdownTask.IsCompleted)
                {
                    return shutdownTask;
                }

                return ObserveWorkTaskCoreAsync(workTask, shutdownTask);
            }

            private Task<T> ObserveWorkTaskAsync<T>(Task<T> workTask)
            {
                if (workTask.IsCompleted)
                {
                    return workTask;
                }

                Task shutdownTask = _shutdownSource.Task;
                if (shutdownTask.IsCompleted)
                {
                    return AwaitShutdownAndThrowAsync<T>(shutdownTask);
                }

                return ObserveWorkTaskCoreAsync(workTask, shutdownTask);
            }

            private static async Task ObserveWorkTaskCoreAsync(Task workTask, Task shutdownTask)
            {
                Task completedTask = await Task.WhenAny(workTask, shutdownTask).ConfigureAwait(false);
                await completedTask.ConfigureAwait(false);
            }

            private static async Task<T> ObserveWorkTaskCoreAsync<T>(Task<T> workTask, Task shutdownTask)
            {
                Task completedTask = await Task.WhenAny(workTask, shutdownTask).ConfigureAwait(false);
                if (ReferenceEquals(completedTask, workTask))
                {
                    return await workTask.ConfigureAwait(false);
                }

                await shutdownTask.ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(CoreAudioComExecutor));
            }

            private static async Task<T> AwaitShutdownAndThrowAsync<T>(Task shutdownTask)
            {
                await shutdownTask.ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(CoreAudioComExecutor));
            }

            public Task<T> Enqueue<T>(Func<T> function, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromCanceled<T>(cancellationToken);

                lock (_workerLock)
                {
                    EnsureWorkerStarted();

                    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var item = new FuncWorkItem<T>(function, tcs, cancellationToken);
                    item.RegisterCancellation();
                    if (item.IsPending && !_queue.TryAdd(item))
                    {
                        item.Fail(new InvalidOperationException("CoreAudio COM executor queue is full"));
                    }

                    return tcs.Task;
                }
            }

            private void EnsureWorkerStarted()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                lock (_workerLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    if (_worker != null && _worker.IsAlive)
                        return;

                    if (_lastWorkerFailure != null)
                    {
                        Logger.Instance.Warning("ComThreadingHelper", "Restarting CoreAudio COM worker after failure", nameof(CoreAudioComExecutor), _lastWorkerFailure);
                        _lastWorkerFailure = null;
                    }

                    TaskCompletionSource<bool> readySource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<bool> exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

                    _workerReadySource = readySource;
                    _workerExitSource = exitSource;

                    _worker = new Thread(() => WorkerLoop(readySource, exitSource))
                    {
                        IsBackground = true,
                        Name = _threadName
                    };

                    _worker.SetApartmentState(ApartmentState.MTA);
                    using var trace = OperationTrace.Attach(null);
                    _worker.Start();
                }
            }

            private void ThrowIfDisposed()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            private void WorkerLoop(TaskCompletionSource<bool> readySource, TaskCompletionSource<bool> exitSource)
            {
                t_isCoreAudioWorker = true;
                Exception? failure = null;

                try
                {
                    ThrowIfComInitializationFailed(nameof(CoreAudioComExecutor));
                    readySource.TrySetResult(true);

                    foreach (var workItem in _queue.GetConsumingEnumerable())
                    {
                        if (!workItem.TryStart()) continue;
                        lock (_healthLock) { _activeStarted = _timeProvider.GetTimestamp(); _busy = true; }
                        try { workItem.Execute(); }
                        finally { lock (_healthLock) { _busy = false; _lastCompletedUtc = _timeProvider.GetUtcNow(); } }
                    }
                }
                catch (Exception ex)
                {
                    readySource.TrySetException(ex);
                    failure = ex;
                    Logger.Instance.Error("ComThreadingHelper", "CoreAudio COM worker terminated unexpectedly", nameof(CoreAudioComExecutor), ex);
                }
                finally
                {
                    lock (_workerLock)
                    {
                        // Queue admission and worker retirement must be atomic: accepted work
                        // either belongs to this worker or starts a replacement after retirement.
                        _lastWorkerFailure = failure;
                        if (failure != null)
                        {
                            while (_queue.TryTake(out var pendingItem))
                                pendingItem.Fail(failure);
                        }

                        if (ReferenceEquals(_worker, Thread.CurrentThread))
                        {
                            _worker = null;
                        }
                    }

                    t_isCoreAudioWorker = false;
                    exitSource.TrySetResult(true);
                }
            }

            internal Task<bool> EnqueueWorkerFailureForTests()
            {
                ThrowIfDisposed();
                lock (_workerLock)
                {
                    EnsureWorkerStarted();

                    TaskCompletionSource<bool> workerExitSource = _workerExitSource;
                    if (!_queue.TryAdd(new FatalWorkItem()))
                        return Task.FromException<bool>(new InvalidOperationException("CoreAudio COM executor queue is full"));
                    return workerExitSource.Task;
                }
            }

            internal Task WaitForWorkerReadyForTestsAsync()
            {
                return WaitForWorkerReadyCoreAsync();
            }

            private async Task WaitForWorkerReadyCoreAsync()
            {
                while (true)
                {
                    ThrowIfDisposed();
                    EnsureWorkerStarted();

                    Task readyTask = _workerReadySource.Task;
                    try
                    {
                        await readyTask;
                        return;
                    }
                    catch when (readyTask.IsFaulted)
                    {
                        await Task.Yield();
                    }
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                Thread? worker;
                lock (_workerLock)
                {
                    if (_disposed)
                        return;

                    worker = _worker;
                    _disposed = true;
                    _shutdownSource.TrySetException(new ObjectDisposedException(nameof(CoreAudioComExecutor)));
                    _queue.CompleteAdding();
                }

                while (_queue.TryTake(out var pendingItem))
                {
                    pendingItem.Fail(new ObjectDisposedException(nameof(CoreAudioComExecutor)));
                }

                bool workerExited = worker == null || !worker.IsAlive;
                if (!workerExited)
                {
                    workerExited = worker!.Join(AppConstants.Timing.CleanupWaitMs);
                }

                if (workerExited)
                {
                    _queue.Dispose();
                }
                else
                {
                    Logger.Instance.Warning(
                        "ComThreadingHelper",
                        () => $"CoreAudio COM worker did not exit within {AppConstants.Timing.CleanupWaitMs}ms; queue disposal deferred",
                        nameof(CoreAudioComExecutor));
                }
            }

            private interface IWorkItem
            {
                bool TryStart();
                void Execute();
                void Fail(Exception ex);
            }

            /// <summary>Cancellation owns only queued work; once execution starts its actual result remains authoritative.</summary>
            private abstract class CancelableWorkItem(CancellationToken cancellationToken) : IWorkItem
            {
                private int _state;
                private CancellationToken _cancellationToken = cancellationToken;
                private CancellationTokenRegistration _registration;
                private readonly string? _traceId = OperationTrace.CurrentId;

                internal bool IsPending => Volatile.Read(ref _state) == 0;

                internal void RegisterCancellation()
                {
                    CancellationToken token = _cancellationToken;
                    var registration = token.UnsafeRegister(static state => ((CancelableWorkItem)state!).CancelPending(), this);
                    _registration = registration;
                    if (!IsPending) { registration.Unregister(); _registration = default; }
                }

                public bool TryStart()
                {
                    if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
                    _registration.Unregister();
                    _registration = default;
                    CancellationToken token = _cancellationToken;
                    _cancellationToken = default;
                    if (token.IsCancellationRequested)
                    {
                        Interlocked.Exchange(ref _state, 2);
                        ClearOperation();
                        SetCanceled(token);
                        return false;
                    }
                    return true;
                }

                private void CancelPending()
                {
                    if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
                    CancellationToken token = _cancellationToken;
                    _cancellationToken = default;
                    ClearOperation();
                    SetCanceled(token);
                    _registration.Unregister();
                    _registration = default;
                }

                public void Execute()
                {
                    using var trace = OperationTrace.Attach(_traceId);
                    try { ExecuteCore(); }
                    catch (Exception ex) { SetException(ex); }
                    finally { Interlocked.Exchange(ref _state, 2); ClearOperation(); }
                }

                public void Fail(Exception ex)
                {
                    if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
                    _registration.Unregister();
                    _registration = default;
                    _cancellationToken = default;
                    ClearOperation();
                    SetException(ex);
                }

                protected abstract void ExecuteCore();
                protected abstract void ClearOperation();
                protected abstract void SetCanceled(CancellationToken token);
                protected abstract void SetException(Exception exception);
            }

            private sealed class ActionWorkItem(Action action, TaskCompletionSource<bool> tcs, CancellationToken cancellationToken) : CancelableWorkItem(cancellationToken)
            {
                private Action? _action = action;
                private TaskCompletionSource<bool>? _completion = tcs;
                protected override void ExecuteCore() { Interlocked.Exchange(ref _action, null)!(); Interlocked.Exchange(ref _completion, null)!.TrySetResult(true); }
                protected override void ClearOperation() => Interlocked.Exchange(ref _action, null);
                protected override void SetCanceled(CancellationToken token) => Interlocked.Exchange(ref _completion, null)!.TrySetCanceled(token);
                protected override void SetException(Exception exception) => Interlocked.Exchange(ref _completion, null)!.TrySetException(exception);
            }

            private sealed class FuncWorkItem<T>(Func<T> function, TaskCompletionSource<T> tcs, CancellationToken cancellationToken) : CancelableWorkItem(cancellationToken)
            {
                private Func<T>? _function = function;
                private TaskCompletionSource<T>? _completion = tcs;
                protected override void ExecuteCore()
                {
                    T result = Interlocked.Exchange(ref _function, null)!();
                    Interlocked.Exchange(ref _completion, null)!.TrySetResult(result);
                }
                protected override void ClearOperation() => Interlocked.Exchange(ref _function, null);
                protected override void SetCanceled(CancellationToken token) => Interlocked.Exchange(ref _completion, null)!.TrySetCanceled(token);
                protected override void SetException(Exception exception) => Interlocked.Exchange(ref _completion, null)!.TrySetException(exception);
            }

            private sealed class FatalWorkItem : IWorkItem
            {
                public bool TryStart() => true;
                private readonly string? _traceId = OperationTrace.CurrentId;

                public void Execute()
                {
                    using var trace = OperationTrace.Attach(_traceId);
                    throw new InvalidOperationException("Forced CoreAudio worker failure for tests");
                }

                public void Fail(Exception ex)
                {
                }
            }
        }
    }
}
