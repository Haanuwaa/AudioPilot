namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeBluetoothReconnectService : IBluetoothReconnectService
{
    public int Calls { get; private set; }
    public int FallbackCalls { get; private set; }
    public List<string> Kinds { get; } = [];
    public bool NextResult { get; set; }
    public bool NextFallbackResult { get; set; }
    public bool? LastFallbackTokenWasCanceledAtInvocation { get; private set; }

    private readonly Queue<Func<bool>> _responses = new();
    private readonly Queue<Func<CancellationToken, Task<bool>>> _asyncResponses = new();
    private readonly Queue<Func<bool>> _fallbackResponses = new();
    private readonly Queue<Func<CancellationToken, Task<bool>>> _fallbackAsyncResponses = new();

    public void EnqueueResult(bool result)
    {
        _responses.Enqueue(() => result);
    }

    public void EnqueueException(Exception ex)
    {
        _responses.Enqueue(() => throw ex);
    }

    public void EnqueueAsync(Func<CancellationToken, Task<bool>> response)
    {
        _asyncResponses.Enqueue(response);
    }

    public void EnqueueFallbackException(Exception ex)
    {
        _fallbackResponses.Enqueue(() => throw ex);
    }

    public void EnqueueFallbackAsync(Func<CancellationToken, Task<bool>> response)
    {
        _fallbackAsyncResponses.Enqueue(response);
    }

    public Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
    {
        Calls++;
        Kinds.Add(kind);

        if (_asyncResponses.Count > 0)
        {
            return _asyncResponses.Dequeue().Invoke(cancellationToken);
        }

        if (_responses.Count > 0)
        {
            bool value = _responses.Dequeue().Invoke();
            return Task.FromResult(value);
        }

        return Task.FromResult(NextResult);
    }

    public Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, CancellationToken cancellationToken)
    {
        Calls++;

        if (_asyncResponses.Count > 0)
        {
            return _asyncResponses.Dequeue().Invoke(cancellationToken);
        }

        if (_responses.Count > 0)
        {
            bool value = _responses.Dequeue().Invoke();
            return Task.FromResult(value);
        }

        return Task.FromResult(NextResult);
    }

    public Task<bool> TryReconnectUsingAudioEndpointControlAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
    {
        FallbackCalls++;
        LastFallbackTokenWasCanceledAtInvocation = cancellationToken.IsCancellationRequested;

        if (_fallbackAsyncResponses.Count > 0)
        {
            return _fallbackAsyncResponses.Dequeue().Invoke(cancellationToken);
        }

        if (_fallbackResponses.Count > 0)
        {
            bool value = _fallbackResponses.Dequeue().Invoke();
            return Task.FromResult(value);
        }

        return Task.FromResult(NextFallbackResult);
    }
}
