using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceServiceSessionCreatedTests
{
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

    private sealed class StubAudioSessionLease : ISessionMonitorSessionLease
    {
        public AudioSessionState State => AudioSessionState.AudioSessionStateInactive;
        public uint ProcessId => 42;
        public string DisplayName => string.Empty;

        public void UseNativeControl(Action<AudioSessionControl> action) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
