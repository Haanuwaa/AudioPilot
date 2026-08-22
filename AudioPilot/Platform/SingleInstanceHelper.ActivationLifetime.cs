using System.IO;
using System.IO.Pipes;

namespace AudioPilot.Platform;

public partial class SingleInstanceHelper
{
    private readonly Lock _activationLock = new();
    private ActivationWork? _activationWork;

    internal int ActivationWaiterCountForTests
    {
        get { lock (_activationLock) return _activationWork?.Waiters ?? 0; }
    }

    private sealed class ActivationWork(int timeoutMs, CancellationToken shutdownToken) : IDisposable
    {
        internal CancellationTokenSource Lifetime { get; } = CreateLifetime(timeoutMs, shutdownToken);
        internal Task<bool> Execution { get; set; } = Task.FromResult(false);
        internal int Waiters { get; set; }

        private static CancellationTokenSource CreateLifetime(int timeoutMs, CancellationToken shutdownToken)
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            lifetime.CancelAfter(timeoutMs);
            return lifetime;
        }

        public void Dispose() => Lifetime.Dispose();
    }

    /// <summary>Clients own their pipe waits; one tracked presentation is shared until it completes or all callers leave.</summary>
    private async Task ExecuteActivationConnectionAsync(NamedPipeServerStream server, CancellationToken shutdownToken)
    {
        if (_isHeadless)
        {
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            writeDeadline.CancelAfter(_responseWriteTimeoutMs);
            await WriteResponseAsync(server, HeadlessBusyResponse, writeDeadline.Token).ConfigureAwait(false);
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        lifetime.CancelAfter(_commandTimeoutMs);
        Task disconnect = WatchCommandDisconnectAsync(server, lifetime, stopWatching.Token);
        ActivationWork? work = null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            lock (_activationLock)
            {
                shutdownToken.ThrowIfCancellationRequested();
                if (_activationWork is { Execution.IsCompleted: true, Waiters: 0 })
                {
                    _ = _activationWork.Execution.Exception;
                    _activationWork.Dispose();
                    _activationWork = null;
                }
                if (_activationWork == null)
                {
                    work = new ActivationWork(_commandTimeoutMs, shutdownToken);
                    CancellationToken activationToken = work.Lifetime.Token;
                    work.Execution = Task.Run(() => RaiseActivationRequestedAsync(activationToken), CancellationToken.None);
                    _activationWork = work;
                }
                work = _activationWork;
                work.Waiters++;
            }

            using var presentationWait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, work.Lifetime.Token);
            SingleInstanceCommandResult response;
            try
            {
                bool shown = await work.Execution.WaitAsync(presentationWait.Token).ConfigureAwait(false);
                response = shown
                    ? new(0, ProtocolVersion: ResponseProtocolVersion)
                    : new(3, ErrorCode: "activation-failed", ErrorMessage: "The running AudioPilot instance could not show its main window.", ProtocolVersion: ResponseProtocolVersion);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested || work.Lifetime.IsCancellationRequested)
            {
                response = new(4, ErrorCode: "activation-expired", ErrorMessage: "The activation request was canceled or expired.", ProtocolVersion: ResponseProtocolVersion);
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation handler failed", nameof(ExecuteActivationConnectionAsync), ex);
                response = new(7, ErrorCode: "activation-handler-failed", ErrorMessage: "The running AudioPilot instance could not process the activation request.", ProtocolVersion: ResponseProtocolVersion);
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
                _logger.Warning("SingleInstanceHelper", () => $"Response write timed out | timeoutMs={_responseWriteTimeoutMs}");
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            await stopWatching.CancelAsync().ConfigureAwait(false);
            await disconnect.ConfigureAwait(false);
            if (work != null)
            {
                lock (_activationLock)
                {
                    if (--work.Waiters == 0 && !work.Execution.IsCompleted)
                    {
                        work.Lifetime.Cancel();
                    }
                }
            }
        }
    }

    private async Task DrainActivationAsync()
    {
        ActivationWork? work;
        lock (_activationLock) work = _activationWork;
        if (work == null) return;
        try
        {
            await work.Execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (work.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning("SingleInstanceHelper", "Activation handler failed while draining", nameof(DrainActivationAsync), ex);
        }
        finally
        {
            work.Dispose();
        }
    }
}
