using System.Reflection;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Audio;

public sealed partial class AudioDeviceServiceSessionCreatedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionCreated_NoLeaseDoesNotQueueOrNotify(bool acquisitionThrows)
    {
        using var logger = TestLoggerScope.CreateInMemory("session-acquire.log");
        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), logger: logger.Logger);
        var session = new UnrestorableSession(AudioSessionState.AudioSessionStateActive, 42)
        {
            NoLease = true,
            AcquisitionFailure = acquisitionThrows ? new InvalidOperationException("acquire-failed") : null,
        };
        int notifications = 0;
        service.AudioSessionCreated += _ => notifications++;

        InvokeSessionCreated(service, AudioMixerMode.Output, session);

        Assert.Empty(service.BackgroundTasksForTests);
        Assert.Equal(0, notifications);
        Assert.Equal(1, session.AcquireCount);
        Assert.Equal(0, session.LeaseDisposeCount);
        Assert.Equal(0, session.SessionDisposeCount);
        string log = logger.DisposeAndReadLogText();
        Assert.Equal(acquisitionThrows, log.Contains("Error in OnSessionCreated handler", StringComparison.Ordinal));
        if (acquisitionThrows) Assert.Contains("acquire-failed", log);
    }

    [Theory]
    [InlineData(AudioMixerMode.Output, true)]
    [InlineData(AudioMixerMode.Input, true)]
    [InlineData(AudioMixerMode.Output, false)]
    [InlineData(AudioMixerMode.Input, false)]
    public async Task SessionCreated_PropertyFailureReleasesLeaseAndSuppressesNotification(AudioMixerMode mode, bool stateFails)
    {
        using var logger = TestLoggerScope.CreateInMemory("session-property.log");
        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), logger: logger.Logger);
        var session = new UnrestorableSession(AudioSessionState.AudioSessionStateActive, 42)
        {
            StateFailure = stateFails ? new InvalidOperationException("state-failed") : null,
            ProcessIdFailure = stateFails ? null : new InvalidOperationException("pid-failed"),
        };
        int notifications = 0;
        service.AudioSessionCreated += _ => notifications++;

        InvokeSessionCreated(service, mode, session);
        await session.Released.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, session.LeaseDisposeCount);
        Assert.Equal(0, session.SessionDisposeCount);
        Assert.Equal(0, notifications);
        string log = logger.DisposeAndReadLogText();
        Assert.Contains("Error handling new session", log);
        Assert.Contains(stateFails ? "state-failed" : "pid-failed", log);
    }

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public async Task SessionCreated_ShutdownDuringInitializationReleasesLeaseWithoutError(AudioMixerMode mode)
    {
        using var logger = TestLoggerScope.CreateInMemory("session-shutdown.log");
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), logger: logger.Logger,
            waitForSessionInitializationAsync: token =>
            {
                delayEntered.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });
        var session = new UnrestorableSession(AudioSessionState.AudioSessionStateActive, 42);
        int notifications = 0;
        service.AudioSessionCreated += _ => notifications++;

        InvokeSessionCreated(service, mode, session);
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        service.Dispose();
        await session.Released.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, session.LeaseDisposeCount);
        Assert.Equal(0, session.NativeCallbackCount);
        Assert.Equal(0, notifications);
        Assert.DoesNotContain("Error handling new session", logger.DisposeAndReadLogText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionCreated_RejectedQueueReleasesLease(bool saturated)
    {
        using var logger = TestLoggerScope.CreateInMemory("session-rejected.log");
        using var service = new AudioDeviceService(new FakeInputListenPropertyWriter(), logger: logger.Logger);
        var session = new UnrestorableSession(AudioSessionState.AudioSessionStateActive, 42);
        int notifications = 0;
        service.AudioSessionCreated += _ => notifications++;
        if (saturated)
        {
            for (int i = 0; i < AppConstants.Limits.MaxConcurrentBackgroundTasks; i++)
                service.BackgroundTasksForTests[i] = Task.CompletedTask;
        }
        else TestPrivateAccess.GetField<CancellationTokenSource>(service, "_backgroundWorkCts").Cancel();
        try
        {
            InvokeSessionCreated(service, AudioMixerMode.Output, session);
            Assert.Equal(1, session.LeaseDisposeCount);
            Assert.Equal(0, session.NativeCallbackCount);
            Assert.Equal(0, notifications);
            Assert.Equal(0, session.SessionDisposeCount);
        }
        finally { service.BackgroundTasksForTests.Clear(); }
        string log = logger.DisposeAndReadLogText();
        Assert.Equal(saturated, log.Contains("queue-saturated", StringComparison.Ordinal));
        Assert.DoesNotContain("Error handling new session", log);
    }

    private static void InvokeSessionCreated(AudioDeviceService service, AudioMixerMode mode, ISessionMonitorSession session)
    {
        MethodInfo? method = typeof(AudioDeviceService).GetMethod("OnSessionCreated", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.CreateDelegate<Action<AudioMixerMode, object?, ISessionMonitorSession>>(service)(mode, null, session);
    }
}
