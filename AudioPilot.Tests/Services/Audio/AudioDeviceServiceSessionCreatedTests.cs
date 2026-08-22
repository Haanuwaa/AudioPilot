using System.Reflection;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Audio;

public sealed partial class AudioDeviceServiceSessionCreatedTests
{
    [Theory]
    [InlineData(AudioMixerMode.Output, AudioSessionState.AudioSessionStateExpired, 42u)]
    [InlineData(AudioMixerMode.Input, AudioSessionState.AudioSessionStateExpired, 42u)]
    [InlineData(AudioMixerMode.Output, AudioSessionState.AudioSessionStateActive, 0u)]
    [InlineData(AudioMixerMode.Input, AudioSessionState.AudioSessionStateActive, 0u)]
    [InlineData(AudioMixerMode.Output, AudioSessionState.AudioSessionStateActive, 42u)]
    [InlineData(AudioMixerMode.Input, AudioSessionState.AudioSessionStateActive, 42u)]
    public async Task SessionCreated_UnrestorableSessionStillNotifiesAndReleasesLease(
        AudioMixerMode mixerMode, AudioSessionState state, uint processId)
    {
        using var logger = TestLoggerScope.CreateInMemory("session-restore-guards.log");
        using var service = new AudioDeviceService(
            new FakeInputListenPropertyWriter(), logger: logger.Logger, deviceCacheAccessor: static () => null);
        var resolver = new UnresolvedProcessResolver();
        TestPrivateAccess.SetField(service, "_sessionProcessResolver", resolver);
        var session = new UnrestorableSession(state, processId);
        List<AudioMixerMode> notifications = [];
        service.AudioSessionCreated += notifications.Add;
        MethodInfo? method = typeof(AudioDeviceService).GetMethod("OnSessionCreated", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var onSessionCreated = method.CreateDelegate<Action<AudioMixerMode, object?, ISessionMonitorSession>>(service);

        onSessionCreated(mixerMode, null, session);
        await session.Released.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([mixerMode], notifications);
        Assert.Equal(1, session.AcquireCount);
        Assert.Equal(1, session.NativeCallbackCount);
        Assert.Equal(1, session.LeaseDisposeCount);
        Assert.Equal(state == AudioSessionState.AudioSessionStateExpired ? 0 : 1, session.ProcessIdReadCount);
        Assert.Equal(state != AudioSessionState.AudioSessionStateExpired && processId != 0 ? 1 : 0, resolver.Calls);
        Assert.Equal(0, session.DisplayNameReadCount);
        Assert.Equal(0, session.SessionDisposeCount);
    }

    [Fact]
    public void RunSessionCreatedErrorBoundary_ExecutesBody_WhenNoExceptionOccurs()
    {
        bool ran = false;

        AudioDeviceService.RunSessionCreatedErrorBoundary(
            () => ran = true,
            static _ => throw new InvalidOperationException("should not log"));

        Assert.True(ran);
    }

    [Fact]
    public void RunSessionCreatedErrorBoundary_LogsException_WhenBodyThrows()
    {
        Exception? logged = null;

        AudioDeviceService.RunSessionCreatedErrorBoundary(
            () => throw new InvalidOperationException("boom"),
            ex => logged = ex);

        Assert.NotNull(logged);
        Assert.IsType<InvalidOperationException>(logged);
    }

    [Fact]
    public void TryQueueSessionCreatedWork_ReturnsFalse_WhenDisposed()
    {
        bool queued = false;

        bool result = AudioDeviceService.TryQueueSessionCreatedWork(
            disposed: true,
            newSession: null,
            runBackgroundWork: (_, _) => queued = true,
            backgroundHandler: static _ => Task.CompletedTask,
            context: "OnSessionCreated");

        Assert.False(result);
        Assert.False(queued);
    }

    [Fact]
    public void TryQueueSessionCreatedWork_ReturnsFalse_WhenSessionIsNull()
    {
        bool queued = false;

        bool result = AudioDeviceService.TryQueueSessionCreatedWork(
            disposed: false,
            newSession: null,
            runBackgroundWork: (_, _) => queued = true,
            backgroundHandler: static _ => Task.CompletedTask,
            context: "OnSessionCreated");

        Assert.False(result);
        Assert.False(queued);
    }

    [Fact]
    public void TryQueueSessionCreatedWork_QueuesBackgroundWork_WhenInputsAreValid()
    {
        bool queued = false;
        string? queuedContext = null;
        ISessionMonitorSessionLease session = new StubAudioSessionLease();

        bool result = AudioDeviceService.TryQueueSessionCreatedWork(
            disposed: false,
            newSession: session,
            runBackgroundWork: (_, context) =>
            {
                queued = true;
                queuedContext = context;
                return true;
            },
            backgroundHandler: static _ => Task.CompletedTask,
            context: "OnSessionCreated");

        Assert.True(result);
        Assert.True(queued);
        Assert.Equal("OnSessionCreated", queuedContext);
    }

    [Fact]
    public async Task RunSessionCreatedHandlerAsync_Notifies_WhenBackgroundWorkRequestsNotification()
    {
        bool notified = false;

        await AudioDeviceService.RunSessionCreatedHandlerAsync(
            _ => Task.FromResult(true),
            () => notified = true,
            static _ => { },
            CancellationToken.None);

        Assert.True(notified);
    }

    [Fact]
    public async Task RunSessionCreatedHandlerAsync_SuppressesNotification_WhenBackgroundWorkReturnsFalse()
    {
        bool notified = false;

        await AudioDeviceService.RunSessionCreatedHandlerAsync(
            _ => Task.FromResult(false),
            () => notified = true,
            static _ => { },
            CancellationToken.None);

        Assert.False(notified);
    }

    [Fact]
    public async Task RunSessionCreatedHandlerAsync_LogsFailure_WhenBackgroundWorkThrows()
    {
        Exception? logged = null;

        await AudioDeviceService.RunSessionCreatedHandlerAsync(
            _ => throw new InvalidOperationException("boom"),
            static () => { },
            ex => logged = ex,
            CancellationToken.None);

        Assert.NotNull(logged);
        Assert.IsType<InvalidOperationException>(logged);
    }

    [Fact]
    public async Task TryRunSessionCreatedWorkBeforeNotifyAsync_ReturnsFalse_WhenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool restoreCalled = false;

        bool shouldNotify = await AudioDeviceService.TryRunSessionCreatedWorkBeforeNotifyAsync(
            static () => false,
            static _ => Task.CompletedTask,
            _ =>
            {
                restoreCalled = true;
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.False(shouldNotify);
        Assert.False(restoreCalled);
    }

    [Fact]
    public async Task TryRunSessionCreatedWorkBeforeNotifyAsync_ReturnsFalse_WhenDisposedAfterDelay()
    {
        bool disposed = false;
        bool restoreCalled = false;

        bool shouldNotify = await AudioDeviceService.TryRunSessionCreatedWorkBeforeNotifyAsync(
            () => disposed,
            _ =>
            {
                disposed = true;
                return Task.CompletedTask;
            },
            _ =>
            {
                restoreCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(shouldNotify);
        Assert.False(restoreCalled);
    }

    [Fact]
    public async Task TryRunSessionCreatedWorkBeforeNotifyAsync_ReturnsTrue_WhenRestoreRuns()
    {
        bool restoreCalled = false;

        bool shouldNotify = await AudioDeviceService.TryRunSessionCreatedWorkBeforeNotifyAsync(
            static () => false,
            static _ => Task.CompletedTask,
            _ =>
            {
                restoreCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(shouldNotify);
        Assert.True(restoreCalled);
    }

    private sealed class UnresolvedProcessResolver() : AudioDeviceSessionProcessResolver(null!, null!, null!)
    {
        public int Calls { get; private set; }

        public override bool TryResolveProcessName(uint pid, out string processName)
        {
            Calls++;
            processName = string.Empty;
            return false;
        }
    }

    private sealed class UnrestorableSession(AudioSessionState state, uint processId)
        : ISessionMonitorSession
    {
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcquireCount { get; private set; }
        public int NativeCallbackCount { get; private set; }
        public int LeaseDisposeCount { get; private set; }
        public int SessionDisposeCount { get; private set; }
        public int ProcessIdReadCount { get; private set; }
        public int DisplayNameReadCount { get; private set; }
        public string SessionInstanceId => "unrestorable-instance";
        public string SessionId => "unrestorable-session";
        public bool NoLease { get; init; }
        public Exception? AcquisitionFailure { get; init; }
        public Exception? StateFailure { get; init; }
        public Exception? ProcessIdFailure { get; init; }
        public AudioSessionState State => StateFailure == null ? state : throw StateFailure;
        public uint ProcessId { get { ProcessIdReadCount++; return ProcessIdFailure == null ? processId : throw ProcessIdFailure; } }
        public string DisplayName { get { DisplayNameReadCount++; throw new InvalidOperationException("An unrestorable session must not reach volume application."); } }

        public ISessionMonitorSessionLease? TryAcquireLease()
        {
            AcquireCount++;
            if (AcquisitionFailure != null) throw AcquisitionFailure;
            return NoLease ? null : new Lease(this);
        }

        public void UseNativeControl(Action<AudioSessionControl> action)
        {
            NativeCallbackCount++;
            action(null!);
        }

        public void RegisterEventClient(IAudioSessionEventsHandler eventClient) => throw new NotSupportedException();
        public void UnregisterEventClient(IAudioSessionEventsHandler eventClient) => throw new NotSupportedException();
        public void Dispose() => SessionDisposeCount++;

        private sealed class Lease(UnrestorableSession owner) : ISessionMonitorSessionLease
        {
            public AudioSessionState State => owner.State;
            public uint ProcessId => owner.ProcessId;
            public string DisplayName => owner.DisplayName;
            public void UseNativeControl(Action<AudioSessionControl> action) => owner.UseNativeControl(action);
            public void Dispose()
            {
                owner.LeaseDisposeCount++;
                owner.Released.TrySetResult();
            }
        }
    }

    private sealed class StubAudioSessionLease : ISessionMonitorSessionLease
    {
        public AudioSessionState State => AudioSessionState.AudioSessionStateInactive;
        public uint ProcessId => 42;
        public string DisplayName => string.Empty;

        public void UseNativeControl(Action<AudioSessionControl> action) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
