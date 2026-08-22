using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioEndpointTestServiceTests
{
    private static readonly AudioEndpointReference OutputA = new("output-a", "Output A");
    private static readonly AudioEndpointReference OutputB = new("output-b", "Output B");
    private static readonly AudioEndpointReference InputA = new("input-a", "Input A");

    [Fact]
    public async Task StartingNewTest_DrainsPreviousSessionAndIgnoresItsCompletion()
    {
        var first = new FakeOutputSession(OutputA);
        var second = new FakeOutputSession(OutputB);
        var factory = new FakeFactory { OutputSessions = new Queue<FakeOutputSession>([first, second]) };
        await using var service = new AudioEndpointTestService(factory);

        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);
        await service.StartOutputTestAsync(OutputB, TestContext.Current.CancellationToken);
        first.Complete();
        await Task.Yield();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(OutputB, service.CurrentState.Endpoint);
        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
    }

    [Fact]
    public async Task StopDuringActivation_CancelsFactoryAndLeavesServiceIdle()
    {
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory
        {
            CreateOutput = async (_, token) =>
            {
                activationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            },
        };
        await using var service = new AudioEndpointTestService(factory);

        Task start = service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);
        await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(AudioEndpointTestStopReason.User, TestContext.Current.CancellationToken);
        await start;

        Assert.Equal(AudioEndpointTestPhase.Idle, service.CurrentState.Phase);
    }

    [Fact]
    public async Task CanceledActivationThatStillReturnsSession_DisposesUnpublishedSession()
    {
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpublished = new FakeOutputSession(OutputA);
        var factory = new FakeFactory
        {
            CreateOutput = async (_, _) =>
            {
                activationStarted.TrySetResult();
                await releaseActivation.Task;
                return unpublished;
            },
        };
        await using var service = new AudioEndpointTestService(factory);
        Task start = service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);
        await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Task stop = service.StopAsync(AudioEndpointTestStopReason.User, TestContext.Current.CancellationToken);
        releaseActivation.TrySetResult();
        await Task.WhenAll(start, stop);

        Assert.Equal(1, unpublished.DisposeCount);
        Assert.Equal(AudioEndpointTestPhase.Idle, service.CurrentState.Phase);
    }

    [Fact]
    public async Task InputMonitoring_ReconfiguresPlaybackWithoutReplacingCapture()
    {
        var input = new FakeInputSession(InputA);
        var factory = new FakeFactory { InputSession = input };
        await using var service = new AudioEndpointTestService(factory);
        var monitor = new AudioEndpointReference("monitor", "Monitor");

        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);
        await service.ConfigureInputMonitoringAsync(true, monitor, 0.4f, TestContext.Current.CancellationToken);
        await service.ConfigureInputMonitoringAsync(false, monitor, 0.2f, TestContext.Current.CancellationToken);

        Assert.Equal(2, input.ConfigurationCount);
        Assert.False(service.CurrentState.MonitoringEnabled);
        Assert.Equal(0.2f, service.CurrentState.MonitorVolume);
        Assert.Equal(0, input.DisposeCount);
    }

    [Fact]
    public async Task StartingInputTest_DefaultsMonitorVolumeToFiftyPercent()
    {
        var input = new FakeInputSession(InputA);
        await using var service = new AudioEndpointTestService(new FakeFactory { InputSession = input });

        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);

        Assert.Equal(0.5f, service.CurrentState.MonitorVolume);
    }

    [Fact]
    public async Task ReadInputLevel_UsesActiveInputAndReturnsSilenceAfterStop()
    {
        var input = new FakeInputSession(InputA)
        {
            Level = new AudioInputLevelSnapshot(42, 73, -12, 9),
        };
        await using var service = new AudioEndpointTestService(new FakeFactory { InputSession = input });

        Assert.Equal(AudioInputLevelSnapshot.Silence, service.ReadInputLevel());
        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);
        Assert.Equal(input.Level, service.ReadInputLevel());

        await service.StopAsync(AudioEndpointTestStopReason.User, TestContext.Current.CancellationToken);
        Assert.Equal(AudioInputLevelSnapshot.Silence, service.ReadInputLevel());
    }

    [Fact]
    public async Task ClassifiedActivationFailure_IsPublishedWithoutEscaping()
    {
        var expected = new AudioEndpointTestException(AudioEndpointTestFailureKind.Muted, "Output is muted.");
        var factory = new FakeFactory
        {
            CreateOutput = (_, _) => Task.FromException<IAudioOutputTestSession>(expected),
        };
        await using var service = new AudioEndpointTestService(factory);

        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        Assert.Equal(AudioEndpointTestPhase.Failed, service.CurrentState.Phase);
        Assert.Equal(AudioEndpointTestFailureKind.Muted, service.CurrentState.FailureKind);
        Assert.Equal(expected.Message, service.CurrentState.Status);
    }

    [Fact]
    public async Task UnexpectedInputActivationFailure_IsClassifiedAndPublished()
    {
        var factory = new FakeFactory
        {
            CreateInput = (_, _, _) => Task.FromException<IAudioInputTestSession>(new InvalidOperationException("synthetic")),
        };
        await using var service = new AudioEndpointTestService(factory);

        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);

        Assert.Equal(AudioEndpointTestPhase.Failed, service.CurrentState.Phase);
        Assert.Equal(AudioEndpointTestFailureKind.Unexpected, service.CurrentState.FailureKind);
        Assert.Equal("The microphone test failed unexpectedly.", service.CurrentState.Status);
    }

    [Fact]
    public async Task MonitoringClassifiedFailure_PreservesCaptureAndPublishesFailureDetails()
    {
        var input = new FakeInputSession(InputA)
        {
            ConfigureFailure = new AudioEndpointTestException(
                AudioEndpointTestFailureKind.Unavailable,
                "Monitor output is unavailable."),
        };
        await using var service = new AudioEndpointTestService(new FakeFactory { InputSession = input });
        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);

        await service.ConfigureInputMonitoringAsync(
            true,
            new AudioEndpointReference("monitor", "Monitor"),
            0.75f,
            TestContext.Current.CancellationToken);

        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
        Assert.Equal(AudioEndpointTestFailureKind.Unavailable, service.CurrentState.FailureKind);
        Assert.Equal("Monitor output is unavailable.", service.CurrentState.Status);
        Assert.Equal(0, input.DisposeCount);
    }

    [Fact]
    public async Task MonitoringUnexpectedFailure_IsContainedAndClassified()
    {
        var input = new FakeInputSession(InputA) { ConfigureFailure = new InvalidOperationException("synthetic") };
        await using var service = new AudioEndpointTestService(new FakeFactory { InputSession = input });
        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);

        await service.ConfigureInputMonitoringAsync(true, null, 2f, TestContext.Current.CancellationToken);

        Assert.Equal(AudioEndpointTestFailureKind.Unexpected, service.CurrentState.FailureKind);
        Assert.Equal("AudioPilot could not update microphone monitoring.", service.CurrentState.Status);
    }

    [Fact]
    public async Task InputCompletion_IsReportedAsUnexpectedFailure()
    {
        var input = new FakeInputSession(InputA);
        await using var service = new AudioEndpointTestService(new FakeFactory { InputSession = input });
        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);

        input.Complete();
        await WaitUntilAsync(
            () => service.CurrentState.Phase == AudioEndpointTestPhase.Failed,
            "Completed input session was not reported as failed.");

        Assert.Equal(AudioEndpointTestFailureKind.Unexpected, service.CurrentState.FailureKind);
        Assert.Equal("The microphone stopped unexpectedly.", service.CurrentState.Status);
    }

    [Fact]
    public async Task OutputCompletionFailure_IsClassifiedAndPublished()
    {
        var output = new FakeOutputSession(OutputA);
        await using var service = new AudioEndpointTestService(
            new FakeFactory { OutputSessions = new Queue<FakeOutputSession>([output]) });
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        output.Fail(new AudioEndpointTestException(
            AudioEndpointTestFailureKind.ExclusiveUse,
            "Output became unavailable."));
        await WaitUntilAsync(
            () => service.CurrentState.Phase == AudioEndpointTestPhase.Failed,
            "Failed output completion was not published.");

        Assert.Equal(AudioEndpointTestFailureKind.ExclusiveUse, service.CurrentState.FailureKind);
        Assert.Equal("Output became unavailable.", service.CurrentState.Status);
    }

    [Fact]
    public async Task ThrowingStateSubscriber_DoesNotBlockOtherSubscribersOrStartup()
    {
        var output = new FakeOutputSession(OutputA);
        await using var service = new AudioEndpointTestService(
            new FakeFactory { OutputSessions = new Queue<FakeOutputSession>([output]) });
        int observed = 0;
        service.StateChanged += _ => throw new InvalidOperationException("synthetic subscriber failure");
        service.StateChanged += _ => observed++;

        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        Assert.True(observed >= 2);
        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
    }

    [Fact]
    public async Task SessionDisposeFailure_DoesNotPreventStopFromPublishingIdle()
    {
        var output = new FakeOutputSession(OutputA) { DisposeFailure = new InvalidOperationException("synthetic") };
        await using var service = new AudioEndpointTestService(
            new FakeFactory { OutputSessions = new Queue<FakeOutputSession>([output]) });
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        await service.StopAsync(AudioEndpointTestStopReason.User, TestContext.Current.CancellationToken);

        Assert.Equal(1, output.DisposeCount);
        Assert.Equal(AudioEndpointTestPhase.Idle, service.CurrentState.Phase);
    }

    [Fact]
    public async Task RelevantDeviceRemoval_StopsSessionAndPublishesClassifiedFailure()
    {
        var input = new FakeInputSession(InputA);
        var factory = new FakeFactory { InputSession = input, EndpointActive = false };
        await using var service = new AudioEndpointTestService(factory);
        await service.StartInputTestAsync(InputA, null, TestContext.Current.CancellationToken);

        await service.ReconcileActiveEndpointsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, input.DisposeCount);
        Assert.Equal(AudioEndpointTestPhase.Failed, service.CurrentState.Phase);
        Assert.Equal(AudioEndpointTestFailureKind.DeviceRemoved, service.CurrentState.FailureKind);
    }

    [Fact]
    public async Task StaleDeviceReconciliation_DoesNotStopReplacementTest()
    {
        using var probeStarted = new ManualResetEventSlim(false);
        using var releaseProbe = new ManualResetEventSlim(false);
        var first = new FakeOutputSession(OutputA);
        var second = new FakeOutputSession(OutputB);
        var factory = new FakeFactory
        {
            OutputSessions = new Queue<FakeOutputSession>([first, second]),
            IsEndpointActiveOverride = (endpoint, _) =>
            {
                if (endpoint != OutputA)
                {
                    return true;
                }

                probeStarted.Set();
                if (!releaseProbe.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException("Timed out while waiting to release the endpoint probe.");
                }

                return false;
            },
        };
        await using var service = new AudioEndpointTestService(factory);
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        Task reconciliation = service.ReconcileActiveEndpointsAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(probeStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            await service.StartOutputTestAsync(OutputB, TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseProbe.Set();
        }

        await reconciliation;

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
        Assert.Equal(OutputB, service.CurrentState.Endpoint);
        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
    }

    [Fact]
    public async Task ReplacingCompletedOutput_DisposesCanceledStatusResetSource()
    {
        var resetDisposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeOutputSession(OutputA);
        var second = new FakeOutputSession(OutputB);
        var factory = new FakeFactory { OutputSessions = new Queue<FakeOutputSession>([first, second]) };
        await using var service = new AudioEndpointTestService(
            factory,
            statusResetDisposed: source =>
            {
                bool disposed;
                try
                {
                    _ = source.Token;
                    disposed = false;
                }
                catch (ObjectDisposedException)
                {
                    disposed = true;
                }

                resetDisposed.TrySetResult(disposed);
            });
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);
        first.Complete();
        await WaitUntilAsync(
            () => service.CurrentState.Phase == AudioEndpointTestPhase.Completed,
            "The first output test did not reach its completed status.");

        await service.StartOutputTestAsync(OutputB, TestContext.Current.CancellationToken);
        bool disposed = await resetDisposed.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(disposed);
        Assert.Equal(OutputB, service.CurrentState.Endpoint);
        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
    }

    [Fact]
    public async Task CompletedStatusReset_SerializesConditionalPublishWithReplacementTest()
    {
        var resetDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResetDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetReadyToPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseResetPublish = new ManualResetEventSlim(false);
        var resetDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementActivationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacementActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeOutputSession(OutputA);
        var replacement = new FakeOutputSession(OutputB);
        int createCount = 0;
        var factory = new FakeFactory
        {
            CreateOutput = async (_, _) =>
            {
                if (Interlocked.Increment(ref createCount) == 1)
                {
                    return first;
                }

                replacementActivationStarted.TrySetResult();
                await releaseReplacementActivation.Task;
                return replacement;
            },
        };
        await using var service = new AudioEndpointTestService(
            factory,
            statusResetDisposed: _ => resetDisposed.TrySetResult(),
            delayAsync: async (_, token) =>
            {
                resetDelayEntered.TrySetResult();
                await releaseResetDelay.Task.WaitAsync(token);
            },
            statusResetBeforePublish: () =>
            {
                resetReadyToPublish.TrySetResult();
                if (!releaseResetPublish.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException("Timed out waiting to release the completed-status reset.");
                }
            });
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);
        first.Complete();
        await resetDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => service.CurrentState.Phase == AudioEndpointTestPhase.Completed,
            "The first output test did not reach completed state.");

        releaseResetDelay.TrySetResult();
        await resetReadyToPublish.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Task replacementStart = service.StartOutputTestAsync(OutputB, TestContext.Current.CancellationToken);
        await Task.Yield();
        bool replacementStartedBeforeResetPublished = replacementActivationStarted.Task.IsCompleted;
        releaseResetPublish.Set();
        await replacementActivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        releaseReplacementActivation.TrySetResult();
        await replacementStart;
        await resetDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(replacementStartedBeforeResetPublished);
        Assert.Equal(OutputB, service.CurrentState.Endpoint);
        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
        Assert.Equal(0, replacement.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentTopologySignals_CoalesceToOneProbeAndOneTrailingPass()
    {
        using var firstProbeStarted = new ManualResetEventSlim(false);
        using var releaseFirstProbe = new ManualResetEventSlim(false);
        int probeCount = 0;
        var output = new FakeOutputSession(OutputA);
        var factory = new FakeFactory
        {
            OutputSessions = new Queue<FakeOutputSession>([output]),
            IsEndpointActiveOverride = (_, _) =>
            {
                int current = Interlocked.Increment(ref probeCount);
                if (current == 1)
                {
                    firstProbeStarted.Set();
                    if (!releaseFirstProbe.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
                    {
                        throw new TimeoutException("Timed out waiting to release the first topology probe.");
                    }
                }

                return true;
            },
        };
        await using var service = new AudioEndpointTestService(factory);
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        Task first = service.ReconcileActiveEndpointsAsync(TestContext.Current.CancellationToken);
        Assert.True(firstProbeStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Task[] burst = [.. Enumerable.Range(0, 19).Select(_ =>
            service.ReconcileActiveEndpointsAsync(TestContext.Current.CancellationToken))];
        releaseFirstProbe.Set();
        await Task.WhenAll([first, .. burst]);

        Assert.Equal(2, probeCount);
        Assert.Equal(AudioEndpointTestPhase.Running, service.CurrentState.Phase);
    }

    [Fact]
    public async Task RepeatedDispose_IsIdempotent()
    {
        var session = new FakeOutputSession(OutputA);
        var service = new AudioEndpointTestService(new FakeFactory { OutputSessions = new Queue<FakeOutputSession>([session]) });
        await service.StartOutputTestAsync(OutputA, TestContext.Current.CancellationToken);

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, session.DisposeCount);
    }

    private sealed class FakeFactory : IAudioEndpointTestSessionFactory
    {
        public Queue<FakeOutputSession> OutputSessions { get; init; } = [];
        public FakeInputSession? InputSession { get; init; }
        public Func<AudioEndpointReference, CancellationToken, Task<IAudioOutputTestSession>>? CreateOutput { get; init; }
        public Func<AudioEndpointReference, AudioEndpointReference?, CancellationToken, Task<IAudioInputTestSession>>? CreateInput { get; init; }
        public Func<AudioEndpointReference, bool, bool>? IsEndpointActiveOverride { get; init; }
        public bool EndpointActive { get; set; } = true;

        public Task<IAudioOutputTestSession> CreateOutputAsync(AudioEndpointReference endpoint, CancellationToken cancellationToken) =>
            CreateOutput?.Invoke(endpoint, cancellationToken) ?? Task.FromResult<IAudioOutputTestSession>(OutputSessions.Dequeue());

        public Task<IAudioInputTestSession> CreateInputAsync(
            AudioEndpointReference endpoint,
            AudioEndpointReference? initialMonitorEndpoint,
            CancellationToken cancellationToken) =>
            CreateInput?.Invoke(endpoint, initialMonitorEndpoint, cancellationToken) ??
            Task.FromResult<IAudioInputTestSession>(InputSession ?? throw new InvalidOperationException("No input session configured."));

        public bool IsEndpointActive(AudioEndpointReference endpoint, bool output) =>
            IsEndpointActiveOverride?.Invoke(endpoint, output) ?? EndpointActive;
    }

    private sealed class FakeOutputSession(AudioEndpointReference endpoint) : IAudioOutputTestSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioEndpointReference Endpoint { get; } = endpoint;
        public Task Completion => _completion.Task;
        public int DisposeCount { get; private set; }
        public Exception? DisposeFailure { get; init; }
        public void Complete() => _completion.TrySetResult();
        public void Fail(Exception exception) => _completion.TrySetException(exception);
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _completion.TrySetResult();
            if (DisposeFailure != null) return ValueTask.FromException(DisposeFailure);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeInputSession(AudioEndpointReference endpoint) : IAudioInputTestSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AudioEndpointReference Endpoint { get; } = endpoint;
        public Task Completion => _completion.Task;
        public bool MonitoringEnabled { get; private set; }
        public AudioEndpointReference? MonitorEndpoint { get; private set; }
        public float MonitorVolume { get; private set; } = 0.5f;
        public int ConfigurationCount { get; private set; }
        public int DisposeCount { get; private set; }
        public AudioInputLevelSnapshot Level { get; init; } = new(50, 75, -30, 1);
        public Exception? ConfigureFailure { get; init; }
        public AudioInputLevelSnapshot ReadLevel() => Level;
        public void Complete() => _completion.TrySetResult();
        public Task ConfigureMonitoringAsync(bool enabled, AudioEndpointReference? monitorEndpoint, float volume, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConfigureFailure != null) return Task.FromException(ConfigureFailure);
            ConfigurationCount++;
            MonitoringEnabled = enabled;
            MonitorEndpoint = monitorEndpoint;
            MonitorVolume = volume;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        long deadline = Environment.TickCount64 + 2000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(failureMessage);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
