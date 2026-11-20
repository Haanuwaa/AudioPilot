using System.Windows.Threading;
using AudioPilot.Models;

namespace AudioPilot.Services.UI;

internal sealed partial class AppMainWindowManager
{
    private readonly CancellationTokenSource _layoutCancellation = new();
    private int _layoutOperations;
    private int _layoutDisposalReady;
    private int _layoutResourcesDisposed;

    public Task<WindowLayoutSnapshot> GetLayoutAsync(CancellationToken cancellationToken = default)
        => RunLayoutAsync(null, cancellationToken);

    public Task<WindowLayoutSnapshot> ChangeLayoutAsync(WindowLayoutChange change, CancellationToken cancellationToken = default)
        => RunLayoutAsync(change, cancellationToken);

    private async Task<WindowLayoutSnapshot> RunLayoutAsync(WindowLayoutChange? change, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _layoutOperations);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _shutdownStarted) != 0 || IsDispatcherUnavailable()) throw new OperationCanceledException("The application is shutting down.");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _layoutCancellation.Token);
            return _dispatcher.CheckAccess() ? await RunLayoutOnDispatcherAsync(change, lifetime.Token)
                : await _dispatcher.InvokeAsync(() => RunLayoutOnDispatcherAsync(change, lifetime.Token), DispatcherPriority.Normal, lifetime.Token).Task.Unwrap();
        }
        finally
        {
            Interlocked.Decrement(ref _layoutOperations);
            TryDisposeLayoutResources();
        }
    }

    private async Task<WindowLayoutSnapshot> RunLayoutOnDispatcherAsync(WindowLayoutChange? change, CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _shutdownStarted) != 0 || IsDispatcherUnavailable()) throw new OperationCanceledException("The application is shutting down.");
            MainWindowPlacementService placement = _placement ?? throw new InvalidOperationException("Window placement is unavailable.");
            return change == null ? await placement.GetLayoutAsync(_window, cancellationToken)
                : await placement.ChangeLayoutAsync(change, _window, cancellationToken);
        }
        finally { _transitionGate.Release(); }
    }

    /// <summary>Bounded shutdown can finish before a placement write; retain its gate until the final caller settles.</summary>
    private void TryDisposeLayoutResources()
    {
        if (Volatile.Read(ref _layoutDisposalReady) == 0 || Volatile.Read(ref _layoutOperations) != 0
            || Interlocked.CompareExchange(ref _layoutResourcesDisposed, 1, 0) != 0) return;
        _layoutCancellation.Dispose();
        _transitionGate.Dispose();
    }
}
