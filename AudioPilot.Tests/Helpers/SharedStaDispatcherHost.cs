using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

internal static class SharedStaDispatcherHost
{
    private static readonly Lock SyncLock = new();
    private static readonly SemaphoreSlim WorkGate = new(1, 1);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static HostState? _host;

    public static void Run(Action action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }, timeout).GetAwaiter().GetResult();
    }

    public static async Task RunAsync(Func<Task> action, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = new CancellationTokenSource(effectiveTimeout, timeProvider ?? TimeProvider.System);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, TestContext.Current.CancellationToken);
        try
        {
            HostState host = GetOrCreateHost();
            Task work;
            if (host.Dispatcher.CheckAccess())
            {
                work = action();
            }
            else
            {
                await WorkGate.WaitAsync(wait.Token).ConfigureAwait(false);
                try
                {
                    wait.Token.ThrowIfCancellationRequested();
                    work = host.Dispatcher.InvokeAsync(
                        async () => await action().ConfigureAwait(true),
                        DispatcherPriority.Send).Task.Unwrap();
                }
                catch
                {
                    WorkGate.Release();
                    throw;
                }

                // A caller timing out does not release a dispatcher still owned by its unfinished test.
                _ = work.ContinueWith(static completed =>
                {
                    _ = completed.Exception;
                    WorkGate.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            await work.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while waiting for shared STA dispatcher ownership or work completion.");
        }
        catch (TaskCanceledException ex) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Shared STA dispatcher work was canceled unexpectedly.", ex);
        }
    }

    private static HostState GetOrCreateHost()
    {
        lock (SyncLock)
        {
            if (_host is { IsAlive: true, HasShutdownStarted: false, HasShutdownFinished: false })
            {
                return _host;
            }

            _host = HostState.Start();
            return _host;
        }
    }

    private sealed class HostState(Thread thread, Dispatcher dispatcher)
    {
        public Thread Thread { get; } = thread;
        public Dispatcher Dispatcher { get; } = dispatcher;
        public bool IsAlive => Thread.IsAlive;
        public bool HasShutdownStarted => Dispatcher.HasShutdownStarted;
        public bool HasShutdownFinished => Dispatcher.HasShutdownFinished;

        public static HostState Start()
        {
            Dispatcher? dispatcher = null;
            Exception? startupFailure = null;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    DispatcherSynchronizationContext context = new(dispatcher);
                    SynchronizationContext.SetSynchronizationContext(context);
                }
                catch (Exception ex)
                {
                    startupFailure = ex;
                }
                finally
                {
                    ready.TrySetResult();
                }

                if (dispatcher != null)
                {
                    Dispatcher.Run();
                }
            })
            {
                IsBackground = true,
                Name = "AudioPilot.Tests.SharedStaDispatcherHost",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Task.Wait(DefaultTimeout))
            {
                throw new TimeoutException($"Timed out while starting shared STA dispatcher host after {DefaultTimeout.TotalSeconds:0} seconds. ThreadState={thread.ThreadState}; IsAlive={thread.IsAlive}.");
            }

            if (startupFailure != null)
            {
                ExceptionDispatchInfo.Capture(startupFailure).Throw();
            }

            return new HostState(thread, dispatcher!);
        }
    }
}
