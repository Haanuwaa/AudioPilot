using System.Windows.Threading;

namespace AudioPilot.Coordinators;

/// <summary>Finishes priority work before WPF returns from the session-ending notification.</summary>
internal static class AppSessionEndingCoordinator
{
    internal static bool Prepare(bool cancelled, Dispatcher dispatcher, Func<Task> prepare,
        TimeSpan timeout, Action<Exception> reportFailure)
    {
        if (cancelled) return true;
        dispatcher.VerifyAccess();
        if (AppDispatcherHelper.IsDispatcherUnavailable(dispatcher)) return false;

        // Queue before entering the frame so even synchronously completed preparation
        // has the same completion path. Pumping permits settings' UI continuations.
        Task preparation = dispatcher.InvokeAsync(prepare, DispatcherPriority.Send).Task.Unwrap();
        var frame = new DispatcherFrame();
        using var stopWaiting = new CancellationTokenSource();
        Task deadline = Task.Delay(timeout, stopWaiting.Token);
        _ = Task.WhenAny(preparation, deadline).ContinueWith(_ => frame.Continue = false,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!preparation.IsCompleted) Dispatcher.PushFrame(frame);
        stopWaiting.Cancel();
        if (!preparation.IsCompleted)
        {
            _ = preparation.ContinueWith(task => reportFailure(task.Exception!.GetBaseException()),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return false;
        }
        try { preparation.GetAwaiter().GetResult(); }
        catch (Exception ex) { reportFailure(ex); }
        return true;
    }
}
