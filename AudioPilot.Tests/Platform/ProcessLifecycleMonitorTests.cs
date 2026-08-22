using System.Management;
using System.Reflection;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Platform;

public sealed class ProcessLifecycleMonitorTests
{
    [Fact]
    public void WmiProcessLifecycleEventSource_TryConvertProcessId_ParsesSupportedValues()
    {
        Assert.True(WmiProcessLifecycleEventSource.TryConvertProcessId(42u, out int uintProcessId));
        Assert.Equal(42, uintProcessId);

        Assert.True(WmiProcessLifecycleEventSource.TryConvertProcessId("43", out int stringProcessId));
        Assert.Equal(43, stringProcessId);

        Assert.False(WmiProcessLifecycleEventSource.TryConvertProcessId(0, out _));
        Assert.False(WmiProcessLifecycleEventSource.TryConvertProcessId("invalid", out _));
    }

    [Fact]
    public void WmiProcessLifecycleEventSource_ProcessTraceQueries_AreAcceptedByWqlEventQuery()
    {
        WqlEventQuery startQuery = WmiProcessLifecycleEventSource.CreateEventQuery(
            WmiProcessLifecycleEventSource.ProcessStartTraceQueryText);
        WqlEventQuery stopQuery = WmiProcessLifecycleEventSource.CreateEventQuery(
            WmiProcessLifecycleEventSource.ProcessStopTraceQueryText);

        Assert.Equal("select * from Win32_ProcessStartTrace", startQuery.QueryString);
        Assert.Equal("select * from Win32_ProcessStopTrace", stopQuery.QueryString);
    }

    [Fact]
    public void EventDrivenProcessLifecycleMonitor_IgnoresBaselineAndDuplicateTransitions()
    {
        var eventSource = new FakeRawProcessLifecycleEventSource();
        var monitor = new EventDrivenProcessLifecycleMonitor(
            Logger.Instance,
            eventSourceFactory: () => eventSource,
            captureRunningProcessIds: static () => [100, 200]);
        List<int> started = [];
        List<int> stopped = [];
        monitor.ProcessStarted += started.Add;
        monitor.ProcessStopped += stopped.Add;

        ProcessLifecycleMonitorStartResult startResult = monitor.Start();
        eventSource.FireProcessStarted(100);
        eventSource.FireProcessStarted(300);
        eventSource.FireProcessStarted(300);
        eventSource.FireProcessStopped(200);
        eventSource.FireProcessStopped(200);
        eventSource.FireProcessStopped(999);

        Assert.True(startResult.Success);
        Assert.Equal([300], started);
        Assert.Equal([200], stopped);
        Assert.True(monitor.IsRunning);
    }

    [Fact]
    public void EventDrivenProcessLifecycleMonitor_CapturesTransitionsRaisedDuringStart()
    {
        var eventSource = new StartPublishingRawProcessLifecycleEventSource(300, 200);
        var monitor = new EventDrivenProcessLifecycleMonitor(
            Logger.Instance,
            eventSourceFactory: () => eventSource,
            captureRunningProcessIds: static () => [200]);
        List<int> started = [];
        List<int> stopped = [];
        monitor.ProcessStarted += started.Add;
        monitor.ProcessStopped += stopped.Add;

        ProcessLifecycleMonitorStartResult startResult = monitor.Start();

        Assert.True(startResult.Success);
        Assert.Equal([300], started);
        Assert.Equal([200], stopped);
        Assert.True(monitor.IsRunning);
    }

    [Fact]
    public void EventDrivenProcessLifecycleMonitor_ReturnsInactiveWhenEventSourceFailsToStart()
    {
        var monitor = new EventDrivenProcessLifecycleMonitor(
            Logger.Instance,
            eventSourceFactory: static () => new ThrowingRawProcessLifecycleEventSource(),
            captureRunningProcessIds: static () => []);

        ProcessLifecycleMonitorStartResult result = monitor.Start();

        Assert.False(result.Success);
        Assert.Equal("inactive", result.Status);
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void FallbackProcessLifecycleMonitor_UsesFallbackWhenPrimaryStartFails_AndForwardsEvents()
    {
        using var loggerScope = new TestLoggerScope(nameof(ProcessLifecycleMonitorTests), "process-lifecycle-fallback.log", LogLevel.Debug);
        var primary = new FakeMonitor(new ProcessLifecycleMonitorStartResult(false, "inactive", "primary-failed"));
        var fallback = new FakeMonitor(new ProcessLifecycleMonitorStartResult(true, "active"));
        var monitor = new FallbackProcessLifecycleMonitor(
            loggerScope.Logger,
            primaryFactory: () => primary,
            fallbackFactory: () => fallback);
        List<int> started = [];
        List<int> stopped = [];
        monitor.ProcessStarted += started.Add;
        monitor.ProcessStopped += stopped.Add;

        ProcessLifecycleMonitorStartResult result = monitor.Start();
        fallback.FireProcessStarted(1234);
        fallback.FireProcessStopped(1234);

        Assert.True(result.Success);
        Assert.Equal(1, primary.StartCallCount);
        Assert.Equal(1, fallback.StartCallCount);
        Assert.Equal([1234], started);
        Assert.Equal([1234], stopped);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("mode=polling result=fallback-active", logText, StringComparison.Ordinal);
        Assert.Contains("reason=primary-failed", logText, StringComparison.Ordinal);
        Assert.Contains("primaryStartMs=", logText, StringComparison.Ordinal);
        Assert.Contains("fallbackStartMs=", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackProcessLifecycleMonitor_Stop_IgnoresLateEventsFromActiveMonitor()
    {
        var fallback = new FakeMonitor(new ProcessLifecycleMonitorStartResult(true, "active"));
        var monitor = new FallbackProcessLifecycleMonitor(
            Logger.Instance,
            primaryFactory: () => new FakeMonitor(new ProcessLifecycleMonitorStartResult(false, "inactive", "primary-failed")),
            fallbackFactory: () => fallback);
        List<int> started = [];
        List<int> stopped = [];
        monitor.ProcessStarted += started.Add;
        monitor.ProcessStopped += stopped.Add;

        ProcessLifecycleMonitorStartResult result = monitor.Start();
        Assert.True(result.Success);

        monitor.Stop();
        fallback.FireProcessStarted(1234);
        fallback.FireProcessStopped(1234);

        Assert.Empty(started);
        Assert.Empty(stopped);
    }

    [Fact]
    public void PollingProcessLifecycleMonitor_Stop_IgnoresLateTickResults()
    {
        var monitor = new PollingProcessLifecycleMonitor(Logger.Instance, TimeSpan.FromSeconds(30));
        List<int> started = [];
        List<int> stopped = [];
        monitor.ProcessStarted += started.Add;
        monitor.ProcessStopped += stopped.Add;

        ProcessLifecycleMonitorStartResult result = monitor.Start();
        Assert.True(result.Success);

        monitor.Stop();
        InvokePrivate(monitor, "PublishTransition", new ProcessLifecycleMonitorTransition([1234], [4321]));

        Assert.Empty(started);
        Assert.Empty(stopped);
    }

    [Fact]
    public void WmiProcessLifecycleEventSource_Stop_IgnoresLateStartAndStopPublishes()
    {
        var source = new WmiProcessLifecycleEventSource();
        List<int> started = [];
        List<int> stopped = [];
        source.ProcessStarted += started.Add;
        source.ProcessStopped += stopped.Add;

        SetPrivateField(source, "_running", true);
        source.Stop();

        InvokePrivate(source, "PublishProcessStarted", 1234);
        InvokePrivate(source, "PublishProcessStopped", 4321);

        Assert.Empty(started);
        Assert.Empty(stopped);
    }

    [Fact]
    public void WmiProcessLifecycleEventSource_Dispose_IgnoresLateStartAndStopPublishes()
    {
        var source = new WmiProcessLifecycleEventSource();
        List<int> started = [];
        List<int> stopped = [];
        source.ProcessStarted += started.Add;
        source.ProcessStopped += stopped.Add;

        SetPrivateField(source, "_running", true);
        source.Dispose();

        InvokePrivate(source, "PublishProcessStarted", 1234);
        InvokePrivate(source, "PublishProcessStopped", 4321);

        Assert.Empty(started);
        Assert.Empty(stopped);
    }

    private static void InvokePrivate(object instance, string methodName, params object[] args)
    {
        MethodInfo? method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(instance, args);
    }

    private static void SetPrivateField(object instance, string fieldName, bool value)
    {
        FieldInfo? field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private sealed class FakeRawProcessLifecycleEventSource : IRawProcessLifecycleEventSource
    {
        public event Action<int>? ProcessStarted;
        public event Action<int>? ProcessStopped;

        public bool IsRunning { get; private set; }

        public void Start()
        {
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            IsRunning = false;
        }

        public void FireProcessStarted(int processId)
        {
            ProcessStarted?.Invoke(processId);
        }

        public void FireProcessStopped(int processId)
        {
            ProcessStopped?.Invoke(processId);
        }
    }

    private sealed class ThrowingRawProcessLifecycleEventSource : IRawProcessLifecycleEventSource
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

        public void Start()
        {
            throw new InvalidOperationException("boom");
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StartPublishingRawProcessLifecycleEventSource(int startedProcessId, int stoppedProcessId) : IRawProcessLifecycleEventSource
    {
        public event Action<int>? ProcessStarted;
        public event Action<int>? ProcessStopped;

        public bool IsRunning { get; private set; }

        public void Start()
        {
            IsRunning = true;
            ProcessStarted?.Invoke(startedProcessId);
            ProcessStopped?.Invoke(stoppedProcessId);
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            IsRunning = false;
        }
    }

    private sealed class FakeMonitor(ProcessLifecycleMonitorStartResult startResult) : IProcessLifecycleMonitor
    {
        private readonly ProcessLifecycleMonitorStartResult _startResult = startResult;

        public event Action<int>? ProcessStarted;
        public event Action<int>? ProcessStopped;

        public bool IsRunning { get; private set; }
        public int StartCallCount { get; private set; }

        public ProcessLifecycleMonitorStartResult Start()
        {
            StartCallCount++;
            IsRunning = _startResult.Success;
            return _startResult;
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            IsRunning = false;
        }

        public void FireProcessStarted(int processId)
        {
            ProcessStarted?.Invoke(processId);
        }

        public void FireProcessStopped(int processId)
        {
            ProcessStopped?.Invoke(processId);
        }
    }
}
