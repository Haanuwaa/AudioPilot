namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakePerAppAudioRoutingResetter : IPerAppAudioRoutingResetter
{
    public int ResetAllCalls { get; private set; }
    public PerAppAudioRoutingResetResult Result { get; set; }

    public PerAppAudioRoutingResetResult TryResetAll()
    {
        ResetAllCalls++;
        return Result;
    }
}

internal sealed class FakeProcessLifecycleMonitor : IProcessLifecycleMonitor
{
    private event Action<int>? ProcessStartedHandlers;
    private event Action<int>? ProcessStoppedHandlers;

    public event Action<int>? ProcessStarted
    {
        add => ProcessStartedHandlers += value;
        remove => ProcessStartedHandlers -= value;
    }

    public event Action<int>? ProcessStopped
    {
        add => ProcessStoppedHandlers += value;
        remove => ProcessStoppedHandlers -= value;
    }

    public bool IsRunning { get; private set; }
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }
    public ProcessLifecycleMonitorStartResult StartResult { get; set; } = new(true, "active");

    public ProcessLifecycleMonitorStartResult Start()
    {
        StartCallCount++;
        IsRunning = StartResult.Success;
        return StartResult;
    }

    public void Stop()
    {
        StopCallCount++;
        IsRunning = false;
    }

    public void Dispose()
    {
        DisposeCallCount++;
        IsRunning = false;
    }

    public void FireProcessStarted(int processId)
    {
        ProcessStartedHandlers?.Invoke(processId);
    }

    public void FireProcessStopped(int processId)
    {
        ProcessStoppedHandlers?.Invoke(processId);
    }
}

internal sealed class FakeSteamBigPictureSignalMonitor : ISteamBigPictureSignalMonitor
{
    private event Action<SteamBigPictureSignal>? SignaledHandlers;

    public event Action<SteamBigPictureSignal>? Signaled
    {
        add => SignaledHandlers += value;
        remove => SignaledHandlers -= value;
    }

    public bool IsRunning { get; private set; }
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }
    public SteamBigPictureSignalMonitorStartResult StartResult { get; set; } = new(true, "active");

    public SteamBigPictureSignalMonitorStartResult Start()
    {
        StartCallCount++;
        IsRunning = StartResult.Success;
        return StartResult;
    }

    public void Stop()
    {
        StopCallCount++;
        IsRunning = false;
    }

    public void Dispose()
    {
        DisposeCallCount++;
        IsRunning = false;
    }

    public void FireSignal(SteamBigPictureSignal? signal = null)
    {
        SignaledHandlers?.Invoke(signal ?? new SteamBigPictureSignal(
            SteamBigPictureSignalKind.Foreground,
            Hwnd: (nint)1,
            ProcessId: 0,
            ProcessExecutablePath: string.Empty,
            Title: string.Empty,
            ClassName: string.Empty));
    }
}

internal sealed class BlockingProcessLifecycleMonitor : IProcessLifecycleMonitor
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);
    private readonly ManualResetEventSlim _disposeGate = new(initialState: false);
    private readonly ManualResetEventSlim _disposeEntered = new(initialState: false);
    private Action<int>? _processStartedHandlers;
    private Action<int>? _processStoppedHandlers;

    public event Action<int>? ProcessStarted
    {
        add => _processStartedHandlers += value;
        remove => _processStartedHandlers -= value;
    }

    public event Action<int>? ProcessStopped
    {
        add => _processStoppedHandlers += value;
        remove => _processStoppedHandlers -= value;
    }

    public bool IsRunning { get; private set; }

    public ProcessLifecycleMonitorStartResult Start()
    {
        IsRunning = true;
        return new ProcessLifecycleMonitorStartResult(true, "active");
    }

    public void Stop()
    {
        IsRunning = false;
    }

    public void Dispose()
    {
        _disposeEntered.Set();
        if (!_disposeGate.Wait(DisposeTimeout))
        {
            throw new TimeoutException("Timed out waiting for the test to release the process-lifecycle disposal gate.");
        }

        IsRunning = false;
    }

    public void WaitForDisposeEntry(TimeSpan timeout)
    {
        if (!_disposeEntered.Wait(timeout))
        {
            throw new TimeoutException("Dispose was not entered within the expected time.");
        }
    }

    public void ReleaseDispose()
    {
        _disposeGate.Set();
    }
}

internal sealed class ThrowingProcessLifecycleMonitor : IProcessLifecycleMonitor
{
    public event Action<int>? ProcessStarted
    {
        add { }
        remove { }
    }

    public event Action<int>? ProcessStopped
    {
        add { }
        remove { }
    }

    public bool IsRunning => false;

    public ProcessLifecycleMonitorStartResult Start() => new(false, "not-started");

    public void Stop()
    {
    }

    public void Dispose()
    {
        throw new InvalidOperationException("process-monitor-dispose-failed");
    }

}
