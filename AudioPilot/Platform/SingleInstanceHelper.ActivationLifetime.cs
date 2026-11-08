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

    private sealed class ActivationWork(int timeoutMs, TimeProvider timeProvider, CancellationToken shutdownToken) : IDisposable
    {
        internal PipeDeadline Lifetime { get; } = new(timeoutMs, timeProvider, shutdownToken);
        internal Task<bool> Execution { get; set; } = Task.FromResult(false);
        internal int Waiters { get; set; }

        public void Dispose() => Lifetime.Dispose();
    }

    /// <summary>Clients own their pipe waits; one tracked presentation is shared until it completes or all callers leave.</summary>
    private async Task ExecuteActivationConnectionAsync(NamedPipeServerStream server, CancellationToken shutdownToken)
    {
        if (_isHeadless)
        {
            using var writeDeadline = new PipeDeadline(_responseWriteTimeoutMs, _timeProvider, shutdownToken);
            await WriteResponseAsync(server, HeadlessBusyResponse, writeDeadline.Token).ConfigureAwait(false);
            return;
        }

        using var lifetime = new PipeDeadline(_commandTimeoutMs, _timeProvider, shutdownToken);
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        Task disconnect = WatchCommandDisconnectAsync(server, lifetime.Source, stopWatching.Token);
        ActivationWork? work = null;
        try
        {
            SingleInstanceCommandResult response;
            try
            {
                work = await JoinActivationAsync(lifetime.Token, shutdownToken).ConfigureAwait(false);
                using var presentationWait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, work.Lifetime.Token);
                bool shown = await work.Execution.WaitAsync(presentationWait.Token).ConfigureAwait(false);
                response = shown
                    ? new(0, ProtocolVersion: ResponseProtocolVersion)
                    : new(3, ErrorCode: "activation-failed", ErrorMessage: "The running AudioPilot instance could not show its main window.", ProtocolVersion: ResponseProtocolVersion);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested || work?.Lifetime.IsCancellationRequested == true)
            {
                response = new(4, ErrorCode: "activation-expired", ErrorMessage: "The activation request was canceled or expired.", ProtocolVersion: ResponseProtocolVersion);
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation handler failed", nameof(ExecuteActivationConnectionAsync), ex);
                response = new(7, ErrorCode: "activation-handler-failed", ErrorMessage: "The running AudioPilot instance could not process the activation request.", ProtocolVersion: ResponseProtocolVersion);
            }

            using var writeDeadline = new PipeDeadline(_responseWriteTimeoutMs, _timeProvider, shutdownToken);
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
                    if (--work.Waiters == 0)
                    {
                        if (!ReferenceEquals(_activationWork, work)) work.Dispose();
                        else if (!work.Execution.IsCompleted) work.Lifetime.Source.Cancel();
                    }
                }
            }
        }
    }

    private async Task<ActivationWork> JoinActivationAsync(CancellationToken requestToken, CancellationToken shutdownToken)
    {
        while (true)
        {
            Task<bool> draining;
            lock (_activationLock)
            {
                requestToken.ThrowIfCancellationRequested();
                if (_activationWork is { Execution.IsCompleted: true } completed
                    && (completed.Waiters == 0 || completed.Lifetime.IsCancellationRequested))
                {
                    _ = completed.Execution.Exception;
                    if (completed.Waiters == 0) completed.Dispose();
                    _activationWork = null;
                }

                if (_activationWork == null)
                {
                    var fresh = new ActivationWork(_commandTimeoutMs, _timeProvider, shutdownToken);
                    CancellationToken activationToken = fresh.Lifetime.Token;
                    fresh.Execution = Task.Run(() => RaiseActivationRequestedAsync(activationToken), CancellationToken.None);
                    _activationWork = fresh;
                }

                if (!_activationWork.Lifetime.IsCancellationRequested)
                {
                    _activationWork.Waiters++;
                    return _activationWork;
                }
                draining = _activationWork.Execution;
            }

            await Task.WhenAny(draining).WaitAsync(requestToken).ConfigureAwait(false);
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
