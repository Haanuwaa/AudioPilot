using System.Runtime.CompilerServices;

namespace AudioPilot.Services.UI.MediaOverlay;

/// <summary>Bounds metadata waits and keeps at most one unfinished read per session. Late results are discarded.</summary>
internal sealed class MediaOverlayMetadataReader<TSession, TMetadata>(
    Func<TSession, Task<TMetadata>> readMetadata,
    TimeSpan timeout)
    where TSession : class
    where TMetadata : class
{
    private readonly ConditionalWeakTable<TSession, PendingRead> _reads = [];

    internal async Task<TMetadata?> ReadAsync(TSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingRead entry = _reads.GetOrCreateValue(session);
        Task<TMetadata> request;
        lock (entry.Sync)
        {
            if (entry.Request is { IsCompleted: false })
                return null;
            entry.Request = request = readMetadata(session);
            _ = request.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return await request.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private sealed class PendingRead
    {
        internal readonly Lock Sync = new();
        internal Task<TMetadata>? Request;
    }
}
