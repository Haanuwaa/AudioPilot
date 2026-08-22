using AudioPilot.Logging;

namespace AudioPilot.Services.Audio.Testing;

internal sealed class AudioEndpointTestService : IAudioEndpointTestService
{
    private readonly IAudioEndpointTestSessionFactory _sessionFactory;
    private readonly Logger _logger;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly Lock _requestLock = new();
    private readonly Lock _reconcileLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Action<CancellationTokenSource>? _statusResetDisposed;
    private readonly Action? _statusResetBeforePublish;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private AudioEndpointTestState _state = AudioEndpointTestState.Idle;
    private IAudioEndpointTestSession? _activeSession;
    private CancellationTokenSource? _activeRequestCts;
    private CancellationTokenSource? _statusResetCts;
    private Task _completionObserver = Task.CompletedTask;
    private Task? _reconcileWorker;
    private long _reconcileRequestRevision;
    private long _revision;
    private int _disposed;

    public AudioEndpointTestService(
        IAudioEndpointTestSessionFactory? sessionFactory = null,
        Logger? logger = null,
        Action<CancellationTokenSource>? statusResetDisposed = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action? statusResetBeforePublish = null)
    {
        _logger = logger ?? Logger.Instance;
        _sessionFactory = sessionFactory ?? new WasapiAudioEndpointTestSessionFactory(_logger);
        _statusResetDisposed = statusResetDisposed;
        _delayAsync = delayAsync ?? Task.Delay;
        _statusResetBeforePublish = statusResetBeforePublish;
    }

    public event Action<AudioEndpointTestState>? StateChanged;

    public AudioEndpointTestState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public AudioInputLevelSnapshot ReadInputLevel() =>
        Volatile.Read(ref _activeSession) is IAudioInputTestSession input
            ? input.ReadLevel()
            : AudioInputLevelSnapshot.Silence;

    public async Task StartOutputTestAsync(
        AudioEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource requestCts = BeginRequest(cancellationToken);
        try
        {
            await _transitionGate.WaitAsync(requestCts.Token);
            try
            {
                ThrowIfDisposed();
                if (!IsCurrentRequest(requestCts))
                {
                    return;
                }

                await StopCoreAsync(AudioEndpointTestStopReason.Replaced, publishIdle: false);
                long revision = Interlocked.Increment(ref _revision);
                Publish(new AudioEndpointTestState(
                    revision,
                    AudioEndpointTestKind.Output,
                    AudioEndpointTestPhase.Starting,
                    endpoint,
                    $"Opening {endpoint.Name}…"));

                IAudioOutputTestSession? createdSession = null;
                try
                {
                    createdSession = await _sessionFactory.CreateOutputAsync(endpoint, requestCts.Token);
                    requestCts.Token.ThrowIfCancellationRequested();
                    IAudioOutputTestSession session = createdSession;
                    _activeSession = session;
                    createdSession = null;
                    Publish(new AudioEndpointTestState(
                        revision,
                        AudioEndpointTestKind.Output,
                        AudioEndpointTestPhase.Running,
                        endpoint,
                        "Testing left, right, and both channels…",
                        StartedAt: DateTimeOffset.UtcNow));
                    _completionObserver = ObserveCompletionAsync(session, revision, AudioEndpointTestKind.Output);
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
                {
                    if (IsCurrentRevision(revision))
                    {
                        Publish(AudioEndpointTestState.Idle with { Revision = revision });
                    }
                }
                catch (AudioEndpointTestException ex)
                {
                    PublishFailure(revision, AudioEndpointTestKind.Output, endpoint, ex);
                }
                catch (Exception ex)
                {
                    PublishFailure(revision, AudioEndpointTestKind.Output, endpoint,
                        new AudioEndpointTestException(AudioEndpointTestFailureKind.Unexpected, "The output test failed unexpectedly.", ex));
                }
                finally
                {
                    if (createdSession != null)
                    {
                        await DisposeSessionSafelyAsync(createdSession, AudioEndpointTestStopReason.Replaced);
                    }
                }
            }
            finally
            {
                _transitionGate.Release();
            }
        }
        finally
        {
            CompleteRequest(requestCts);
        }
    }

    public async Task StartInputTestAsync(
        AudioEndpointReference endpoint,
        AudioEndpointReference? initialMonitorEndpoint,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource requestCts = BeginRequest(cancellationToken);
        try
        {
            await _transitionGate.WaitAsync(requestCts.Token);
            try
            {
                ThrowIfDisposed();
                if (!IsCurrentRequest(requestCts))
                {
                    return;
                }

                await StopCoreAsync(AudioEndpointTestStopReason.Replaced, publishIdle: false);
                long revision = Interlocked.Increment(ref _revision);
                Publish(new AudioEndpointTestState(
                    revision,
                    AudioEndpointTestKind.Input,
                    AudioEndpointTestPhase.Starting,
                    endpoint,
                    $"Opening {endpoint.Name}…",
                    MonitorEndpoint: initialMonitorEndpoint));

                IAudioInputTestSession? createdSession = null;
                try
                {
                    createdSession = await _sessionFactory.CreateInputAsync(
                        endpoint,
                        initialMonitorEndpoint,
                        requestCts.Token);
                    requestCts.Token.ThrowIfCancellationRequested();
                    IAudioInputTestSession session = createdSession;
                    _activeSession = session;
                    createdSession = null;
                    Publish(new AudioEndpointTestState(
                        revision,
                        AudioEndpointTestKind.Input,
                        AudioEndpointTestPhase.Running,
                        endpoint,
                        "Microphone test is active",
                        MonitorEndpoint: initialMonitorEndpoint,
                        MonitorVolume: 0.5f,
                        StartedAt: DateTimeOffset.UtcNow));
                    _completionObserver = ObserveCompletionAsync(session, revision, AudioEndpointTestKind.Input);
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
                {
                    if (IsCurrentRevision(revision))
                    {
                        Publish(AudioEndpointTestState.Idle with { Revision = revision });
                    }
                }
                catch (AudioEndpointTestException ex)
                {
                    PublishFailure(revision, AudioEndpointTestKind.Input, endpoint, ex);
                }
                catch (Exception ex)
                {
                    PublishFailure(revision, AudioEndpointTestKind.Input, endpoint,
                        new AudioEndpointTestException(AudioEndpointTestFailureKind.Unexpected, "The microphone test failed unexpectedly.", ex));
                }
                finally
                {
                    if (createdSession != null)
                    {
                        await DisposeSessionSafelyAsync(createdSession, AudioEndpointTestStopReason.Replaced);
                    }
                }
            }
            finally
            {
                _transitionGate.Release();
            }
        }
        finally
        {
            CompleteRequest(requestCts);
        }
    }

    public async Task ConfigureInputMonitoringAsync(
        bool enabled,
        AudioEndpointReference? monitorEndpoint,
        float volume,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_activeSession is not IAudioInputTestSession input || CurrentState.Phase != AudioEndpointTestPhase.Running)
            {
                return;
            }

            try
            {
                await input.ConfigureMonitoringAsync(enabled, monitorEndpoint, Math.Clamp(volume, 0, 1), cancellationToken);
                AudioEndpointTestState current = CurrentState;
                Publish(current with
                {
                    MonitoringEnabled = input.MonitoringEnabled,
                    MonitorEndpoint = input.MonitorEndpoint,
                    MonitorVolume = input.MonitorVolume,
                    FailureKind = AudioEndpointTestFailureKind.None,
                    Status = input.MonitoringEnabled ? "Microphone test and live monitoring are active" : "Microphone test is active",
                });
            }
            catch (AudioEndpointTestException ex)
            {
                AudioEndpointTestState current = CurrentState;
                Publish(current with
                {
                    MonitoringEnabled = input.MonitoringEnabled,
                    MonitorEndpoint = input.MonitorEndpoint,
                    MonitorVolume = input.MonitorVolume,
                    FailureKind = ex.FailureKind,
                    Status = ex.Message,
                });
                LogFailure(current.Revision, current.Kind, current.Endpoint, ex);
            }
            catch (Exception ex)
            {
                AudioEndpointTestState current = CurrentState;
                var classified = new AudioEndpointTestException(
                    AudioEndpointTestFailureKind.Unexpected,
                    "AudioPilot could not update microphone monitoring.",
                    ex);
                Publish(current with { FailureKind = classified.FailureKind, Status = classified.Message });
                LogFailure(current.Revision, current.Kind, current.Endpoint, classified);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task StopAsync(
        AudioEndpointTestStopReason reason,
        CancellationToken cancellationToken = default)
    {
        CancelCurrentRequest();
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(reason, publishIdle: true);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task ReconcileActiveEndpointsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _reconcileRequestRevision);
        Task worker;
        lock (_reconcileLock)
        {
            if (_reconcileWorker is null || _reconcileWorker.IsCompleted)
            {
                _reconcileWorker = RunReconciliationWorkerAsync();
            }

            worker = _reconcileWorker;
        }

        await worker.WaitAsync(cancellationToken);
    }

    private async Task RunReconciliationWorkerAsync()
    {
        await Task.Yield();
        while (true)
        {
            long requestRevision = Interlocked.Read(ref _reconcileRequestRevision);
            await ReconcileActiveEndpointsCoreAsync(_lifetimeCts.Token);

            lock (_reconcileLock)
            {
                if (requestRevision == Interlocked.Read(ref _reconcileRequestRevision))
                {
                    _reconcileWorker = null;
                    return;
                }
            }
        }
    }

    private async Task ReconcileActiveEndpointsCoreAsync(CancellationToken cancellationToken)
    {
        AudioEndpointTestState state = CurrentState;
        if (state.Phase is AudioEndpointTestPhase.Idle or AudioEndpointTestPhase.Completed or AudioEndpointTestPhase.Failed)
        {
            return;
        }

        bool endpointActive = await Task.Run(
            () => _sessionFactory.IsEndpointActive(state.Endpoint, state.Kind == AudioEndpointTestKind.Output),
            cancellationToken);
        bool monitorActive = !state.MonitoringEnabled || state.MonitorEndpoint is not { Id.Length: > 0 } monitor ||
            await Task.Run(() => _sessionFactory.IsEndpointActive(monitor, output: true), cancellationToken);
        if (endpointActive && monitorActive)
        {
            return;
        }

        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            AudioEndpointTestState current = CurrentState;
            if (!IsSameReconciledOperation(state, current))
            {
                return;
            }

            CancelCurrentRequest();
            await StopCoreAsync(AudioEndpointTestStopReason.DeviceRemoved, publishIdle: true);
            if (CurrentState.Phase == AudioEndpointTestPhase.Idle)
            {
                Publish(new AudioEndpointTestState(
                    Interlocked.Increment(ref _revision),
                    state.Kind,
                    AudioEndpointTestPhase.Failed,
                    state.Endpoint,
                    endpointActive ? "The microphone monitor output was disconnected." : $"{state.Endpoint.Name} was disconnected.",
                    AudioEndpointTestFailureKind.DeviceRemoved));
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private static bool IsSameReconciledOperation(
        AudioEndpointTestState captured,
        AudioEndpointTestState current)
    {
        return current.Phase is AudioEndpointTestPhase.Starting or AudioEndpointTestPhase.Running &&
            current.Revision == captured.Revision &&
            current.Kind == captured.Kind &&
            current.Endpoint == captured.Endpoint &&
            current.MonitoringEnabled == captured.MonitoringEnabled &&
            current.MonitorEndpoint == captured.MonitorEndpoint;
    }

    private CancellationTokenSource BeginRequest(CancellationToken externalToken)
    {
        ThrowIfDisposed();
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _lifetimeCts.Token);
        CancellationTokenSource? previous;
        lock (_requestLock)
        {
            previous = _activeRequestCts;
            _activeRequestCts = requestCts;
        }

        TryCancel(previous);
        CancelStatusReset();
        return requestCts;
    }

    private void CompleteRequest(CancellationTokenSource requestCts)
    {
        lock (_requestLock)
        {
            if (ReferenceEquals(_activeRequestCts, requestCts))
            {
                _activeRequestCts = null;
            }
        }

        requestCts.Dispose();
    }

    private bool IsCurrentRequest(CancellationTokenSource requestCts)
    {
        lock (_requestLock)
        {
            return ReferenceEquals(_activeRequestCts, requestCts) && !requestCts.IsCancellationRequested;
        }
    }

    private void CancelCurrentRequest()
    {
        CancellationTokenSource? current;
        lock (_requestLock)
        {
            current = _activeRequestCts;
            _activeRequestCts = null;
        }
        TryCancel(current);
    }

    private async Task StopCoreAsync(AudioEndpointTestStopReason reason, bool publishIdle)
    {
        CancelStatusReset();
        IAudioEndpointTestSession? session = Interlocked.Exchange(ref _activeSession, null);
        AudioEndpointTestState current = CurrentState;
        if (session != null)
        {
            Publish(current with { Phase = AudioEndpointTestPhase.Stopping, Status = "Stopping audio test…" });
            await DisposeSessionSafelyAsync(session, reason);
        }

        if (publishIdle)
        {
            Publish(AudioEndpointTestState.Idle with { Revision = Interlocked.Increment(ref _revision) });
        }

        if (session != null)
        {
            _logger.Info("AudioEndpointTestService", () =>
                $"audio-test-stopped | kind={current.Kind} reason={reason} endpoint={LogPrivacy.Device(current.Endpoint.Name)}");
        }
    }

    private async Task ObserveCompletionAsync(
        IAudioEndpointTestSession session,
        long revision,
        AudioEndpointTestKind kind)
    {
        Exception? failure = null;
        try
        {
            await session.Completion;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await _transitionGate.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(_activeSession, session) || !IsCurrentRevision(revision))
            {
                return;
            }

            _activeSession = null;
            await DisposeSessionSafelyAsync(session, AudioEndpointTestStopReason.User);
            AudioEndpointTestState current = CurrentState;
            if (failure != null)
            {
                AudioEndpointTestException classified = failure as AudioEndpointTestException ??
                    AudioEndpointTestFailureClassifier.ClassifyActivation(current.Endpoint, failure);
                PublishFailure(revision, kind, current.Endpoint, classified);
                return;
            }

            if (kind == AudioEndpointTestKind.Output)
            {
                Publish(current with { Phase = AudioEndpointTestPhase.Completed, Status = "Output test complete" });
                ScheduleStatusReset(revision);
            }
            else
            {
                PublishFailure(revision, kind, current.Endpoint,
                    new AudioEndpointTestException(AudioEndpointTestFailureKind.Unexpected, "The microphone stopped unexpectedly."));
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void PublishFailure(
        long revision,
        AudioEndpointTestKind kind,
        AudioEndpointReference endpoint,
        AudioEndpointTestException exception)
    {
        Publish(new AudioEndpointTestState(
            revision,
            kind,
            AudioEndpointTestPhase.Failed,
            endpoint,
            exception.Message,
            exception.FailureKind));
        LogFailure(revision, kind, endpoint, exception);
    }

    private async ValueTask DisposeSessionSafelyAsync(
        IAudioEndpointTestSession session,
        AudioEndpointTestStopReason reason)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning("AudioEndpointTestService", () =>
                $"audio-test-dispose-failed | reason={reason} error={ex.GetType().Name}",
                nameof(DisposeSessionSafelyAsync),
                ex);
        }
    }

    private void LogFailure(
        long revision,
        AudioEndpointTestKind kind,
        AudioEndpointReference endpoint,
        AudioEndpointTestException exception)
    {
        _logger.Warning("AudioEndpointTestService", () =>
            $"audio-test-failed | revision={revision} kind={kind} failure={exception.FailureKind} endpoint={LogPrivacy.Device(endpoint.Name)} error={exception.InnerException?.GetType().Name ?? exception.GetType().Name}",
            nameof(AudioEndpointTestService),
            exception);
    }

    private void ScheduleStatusReset(long revision)
    {
        var resetCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _statusResetCts = resetCts;
        _ = ResetStatusAsync(revision, resetCts);
    }

    private async Task ResetStatusAsync(long revision, CancellationTokenSource resetCts)
    {
        bool gateEntered = false;
        try
        {
            await _delayAsync(TimeSpan.FromSeconds(2), resetCts.Token);
            await _transitionGate.WaitAsync(resetCts.Token);
            gateEntered = true;
            AudioEndpointTestState current = CurrentState;
            if (current.Revision == revision && current.Phase == AudioEndpointTestPhase.Completed)
            {
                _statusResetBeforePublish?.Invoke();
                Publish(AudioEndpointTestState.Idle with { Revision = revision });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (gateEntered)
            {
                _transitionGate.Release();
            }

            Interlocked.CompareExchange(ref _statusResetCts, null, resetCts);
            resetCts.Dispose();
            if (_statusResetDisposed != null)
            {
                try
                {
                    _statusResetDisposed(resetCts);
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "AudioEndpointTestService",
                        "audio-test-status-reset-disposal-observer-failed",
                        nameof(ResetStatusAsync),
                        ex);
                }
            }
        }
    }

    private void CancelStatusReset()
    {
        CancellationTokenSource? resetCts = Interlocked.Exchange(ref _statusResetCts, null);
        TryCancel(resetCts);
    }

    private bool IsCurrentRevision(long revision) => CurrentState.Revision == revision;

    private void Publish(AudioEndpointTestState state)
    {
        lock (_stateLock)
        {
            _state = state;
        }

        Action<AudioEndpointTestState>? handlers = StateChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<AudioEndpointTestState> handler in handlers.GetInvocationList().Cast<Action<AudioEndpointTestState>>())
        {
            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                _logger.Warning("AudioEndpointTestService", "audio-test-state-handler-failed", nameof(Publish), ex);
            }
        }
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();
        CancelCurrentRequest();
        Task? reconciliation;
        lock (_reconcileLock)
        {
            reconciliation = _reconcileWorker;
        }

        if (reconciliation != null)
        {
            try
            {
                await reconciliation;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "AudioEndpointTestService",
                    "audio-test-reconciliation-drain-failed",
                    nameof(DisposeAsync),
                    ex);
            }
        }

        await _transitionGate.WaitAsync();
        try
        {
            await StopCoreAsync(AudioEndpointTestStopReason.Shutdown, publishIdle: true);
        }
        finally
        {
            _transitionGate.Release();
        }

        try
        {
            await _completionObserver;
        }
        catch
        {
        }

        CancelStatusReset();
        _lifetimeCts.Dispose();
        _transitionGate.Dispose();
        StateChanged = null;
    }
}
