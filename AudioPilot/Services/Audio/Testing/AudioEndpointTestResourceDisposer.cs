using System.Runtime.ExceptionServices;

namespace AudioPilot.Services.Audio.Testing;

internal static class AudioEndpointTestResourceDisposer
{
    public static async Task<TSession> StartOrDisposeAsync<TSession>(
        TSession session,
        Action start,
        Action<Exception> recordDisposeFailure)
        where TSession : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(recordDisposeFailure);

        try
        {
            start();
            return session;
        }
        catch
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                recordDisposeFailure(disposeException);
            }

            throw;
        }
    }

    public static async ValueTask DisposeAsync(
        IEnumerable<Func<ValueTask>> disposeOperations,
        Action<Exception> recordFailure)
    {
        ArgumentNullException.ThrowIfNull(disposeOperations);
        ArgumentNullException.ThrowIfNull(recordFailure);

        foreach (Func<ValueTask> disposeOperation in disposeOperations)
        {
            try
            {
                await disposeOperation();
            }
            catch (Exception ex)
            {
                recordFailure(ex);
            }
        }
    }

    public static void ThrowIfAny(IReadOnlyList<Exception>? failures)
    {
        if (failures is not { Count: > 0 })
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("One or more audio-test resources failed to dispose.", failures);
    }
}
