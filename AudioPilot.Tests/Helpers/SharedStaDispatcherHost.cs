using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace AudioPilot.Tests.Helpers;

internal static class SharedStaDispatcherHost
{
    private static readonly Lock SyncLock = new();
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

    public static async Task RunAsync(Func<Task> action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        HostState host = GetOrCreateHost();
        Task work = host.Dispatcher.InvokeAsync(
            async () => await action().ConfigureAwait(true),
            DispatcherPriority.Send).Task.Unwrap();

        try
        {
            await work.WaitAsync(effectiveTimeout, TestContext.Current.CancellationToken).ConfigureAwait(false);
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
