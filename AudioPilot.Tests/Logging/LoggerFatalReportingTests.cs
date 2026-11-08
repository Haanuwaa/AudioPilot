using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

[Collection("LogPrivacy")]
public sealed class LoggerFatalReportingTests
{
    [Fact]
    public async Task SaturatedQueueRetainsTheFirstFatalInABoundedReserve()
    {
        using var scope = TestLoggerScope.CreateFileBacked("fatal-reserve", "fatal-reserve.log");
        var logger = scope.Logger;
        logger.Info("Test", "writer-ready");
        await TestExecutionGuards.WaitUntilAsync(() => logger.CaptureHealth().LastWriteSucceeded == true,
            "The log writer did not finish startup.", TimeSpan.FromSeconds(5));
        var fileLock = TestPrivateAccess.GetField<Lock>(logger, "_fileLock");
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            lock (fileLock) { held.SetResult(); release.Wait(); }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            // Block an in-flight batch so the worker cannot dequeue any of the queue filler.
            logger.Info("Test", "blocked-batch");
            await TestExecutionGuards.WaitUntilAsync(() => logger.QueueDepth == 0,
                "The log writer did not dequeue the blocking batch.", TimeSpan.FromSeconds(5));
            for (int i = 0; i < logger.MaxQueueCapacity + 1000; i++) logger.Info("Test", "queue filler");
            long droppedBefore = logger.TotalDroppedCount;
            Assert.Equal(1000, droppedBefore);
            Assert.Equal(logger.MaxQueueCapacity, logger.QueueDepth);
            logger.Fatal("Test", "reserved-fatal-marker");
            Assert.Equal(droppedBefore, logger.TotalDroppedCount);
            Assert.Equal(logger.MaxQueueCapacity + 1, logger.QueueDepth);
            for (int i = 0; i < 20; i++) logger.Fatal("Test", "later-fatal");
            Assert.Equal(droppedBefore + 20, logger.TotalDroppedCount);
            Assert.Equal(logger.MaxQueueCapacity + 1, logger.QueueDepth);
        }
        finally { release.Set(); await holder; }

        string text = scope.DisposeAndReadLogText();
        Assert.Single(text.Split('\n'), line => line.Contains("reserved-fatal-marker", StringComparison.Ordinal));
        Assert.DoesNotContain("later-fatal", text);
        Assert.Equal(0, logger.QueueDepth);
    }

    [Fact]
    public async Task EmergencyReportDeadlineCoversBlockedWritesAndRepeatedCallsJoinTheSameReport()
    {
        using var scope = TestLoggerScope.CreateFileBacked("fatal-deadline", "fatal-deadline.log");
        var logger = scope.Logger;
        var fileLock = TestPrivateAccess.GetField<Lock>(logger, "_fileLock");
        using var clock = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            lock (fileLock) { held.SetResult(); release.Wait(); }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task<FatalLogWriteResult> pending = logger.ReportFatalAsync("Test", "first-emergency-marker",
                new InvalidOperationException("first"), TimeSpan.FromSeconds(10), clock);
            await clock.WaitForTimerAsync();
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(FatalLogWriteResult.TimedOut, await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally { release.Set(); await holder; }

        Assert.Equal(FatalLogWriteResult.Written, await logger.ReportFatalAsync("Test", "second-emergency-marker",
            new InvalidOperationException("second"), TimeSpan.FromSeconds(5)));
        string text = scope.DisposeAndReadLogText();
        Assert.Single(text.Split('\n'), line => line.Contains("first-emergency-marker", StringComparison.Ordinal));
        Assert.DoesNotContain("second-emergency-marker", text);
    }

    [Fact]
    public async Task EmergencyReportingRespectsDisabledLoggingAndReportsWriteFailures()
    {
        using var scope = TestLoggerScope.CreateFileBacked("fatal-unavailable", "fatal-unavailable.log");
        scope.Logger.MinimumLevel = LogLevel.None;
        Assert.Equal(FatalLogWriteResult.Disabled, await scope.Logger.ReportFatalAsync("Test", "disabled-marker", new InvalidOperationException()));
        scope.Logger.MinimumLevel = LogLevel.Fatal;
        TestPrivateAccess.SetField(scope.Logger, "_logFilePath", scope.Root);
        Assert.Equal(FatalLogWriteResult.Unavailable, await scope.Logger.ReportFatalAsync("Test", "unavailable-marker", new InvalidOperationException(), TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("disabled-marker", scope.DisposeAndReadLogText());
    }
}
