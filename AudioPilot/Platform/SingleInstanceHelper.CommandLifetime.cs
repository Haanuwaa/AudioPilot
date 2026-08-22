using System.IO;
using System.IO.Pipes;

namespace AudioPilot.Platform;

public partial class SingleInstanceHelper
{
    /// <summary>
    /// Cancels abandoned or expired requests without releasing serialization while an accepted operation is still running.
    /// The response can finish before native work drains; in that case its outcome is explicitly unknown.
    /// </summary>
    private async Task ExecuteCommandConnectionAsync(NamedPipeServerStream server, string payload, SemaphoreSlim gate, CancellationToken shutdownToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        lifetime.CancelAfter(ResolveCommandTimeoutMs(payload, _commandTimeoutMs));
        Task disconnect = WatchCommandDisconnectAsync(server, lifetime, stopWatching.Token);
        Task<SingleInstanceCommandResult>? execution = null;
        bool entered = false;
        string requestId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            SingleInstanceCommandResult response;
            try
            {
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                entered = true;
                lifetime.Token.ThrowIfCancellationRequested();
                var handler = CommandRequested;
                if (handler == null)
                {
                    response = new(3, ErrorCode: "ui-host-unavailable", ErrorMessage: "No command handler is available.", ProtocolVersion: ResponseProtocolVersion);
                }
                else
                {
                    _logger.Debug("SingleInstanceHelper", () => $"forwarded-command-start | requestId={requestId} action={GetRequestAction(payload)}");
                    execution = Task.Run(async () =>
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        return await handler(payload, lifetime.Token).ConfigureAwait(false);
                    }, CancellationToken.None);
                    response = await execution.WaitAsync(lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                bool dispatched = execution != null;
                response = new(4, ErrorCode: dispatched ? "forwarded-command-outcome-unknown" : "forwarded-command-expired",
                    ErrorMessage: dispatched
                        ? "The request expired or was canceled after dispatch; its outcome is unknown. Check current state before retrying."
                        : "The request expired or was canceled before execution began; no command was executed.",
                    ProtocolVersion: ResponseProtocolVersion);
                _logger.Info("SingleInstanceHelper", $"forwarded-command-canceled | requestId={requestId} dispatched={dispatched} shutdown={shutdownToken.IsCancellationRequested}");
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", $"forwarded-command-failed | requestId={requestId}", nameof(ExecuteCommandConnectionAsync), ex);
                response = new(7, ErrorCode: "forwarded-runtime-failed", ErrorMessage: "Command execution failed in the running UI host.", ProtocolVersion: ResponseProtocolVersion);
            }

            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            writeDeadline.CancelAfter(_responseWriteTimeoutMs);
            try
            {
                await WriteResponseAsync(server, response, writeDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
            {
                _responseWriteTimedOutSource?.TrySetResult();
                _logger.Warning("SingleInstanceHelper", $"forwarded-response-timeout | requestId={requestId}");
            }
            catch (IOException)
            {
                _logger.Debug("SingleInstanceHelper", $"forwarded-response-disconnected | requestId={requestId}");
            }
        }
        finally
        {
            try
            {
                await stopWatching.CancelAsync().ConfigureAwait(false);
                await disconnect.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug("SingleInstanceHelper", $"forwarded-disconnect-watch-failed | requestId={requestId} error={ex.GetType().Name}");
            }
            if (execution != null)
            {
                try
                {
                    await execution.ConfigureAwait(false);
                    _logger.Debug("SingleInstanceHelper", $"forwarded-command-drained | requestId={requestId}");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger.Debug("SingleInstanceHelper", $"forwarded-command-drain-failed | requestId={requestId} error={ex.GetType().Name}");
                }
            }
            if (entered) gate.Release();
        }
    }

    private static async Task WatchCommandDisconnectAsync(NamedPipeServerStream server, CancellationTokenSource lifetime, CancellationToken stopToken)
    {
        try
        {
            _ = await server.ReadAsync(new byte[1], stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return;
        }
        catch (IOException) { }
        if (!stopToken.IsCancellationRequested)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }
}
