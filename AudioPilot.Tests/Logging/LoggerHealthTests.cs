using System.Reflection;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

public sealed class LoggerHealthTests : IDisposable
{
    private readonly TestScopedDirectory _directory;

    public LoggerHealthTests()
    {
        _directory = new TestScopedDirectory(nameof(LoggerHealthTests));
    }

    [Fact]
    public void TryEnqueueForTests_ReportsOverflow_AndIncrementsDropCounters()
    {
        using var logger = new Logger(_directory.Root, "health-overflow.log")
        {
            MinimumLevel = LogLevel.Trace
        };

        long beforePending = logger.PendingDroppedCount;
        long beforeTotal = logger.TotalDroppedCount;

        logger.SetQueueDepthForTests(logger.MaxQueueCapacity);
        bool enqueued = logger.TryEnqueueForTests("line");

        Assert.False(enqueued);
        Assert.True(logger.PendingDroppedCount > beforePending);
        Assert.True(logger.TotalDroppedCount > beforeTotal);
    }

    [Fact]
    public void QueueCapacityReservation_AllowsOnlyRemainingSlots_UnderConcurrency()
    {
        using Logger logger = Logger.CreateInMemoryForTests();
        logger.SetQueueDepthForTests(logger.MaxQueueCapacity - 1);
        int successfulReservations = 0;

        Parallel.For(0, 64, _ =>
        {
            if (logger.TryReserveQueueSlotForTests())
            {
                Interlocked.Increment(ref successfulReservations);
            }
        });

        Assert.Equal(1, successfulReservations);
        Assert.Equal(logger.MaxQueueCapacity, logger.QueueDepth);
        logger.SetQueueDepthForTests(0);
    }

    [Fact]
    public void Dispose_DrainsQueueDepthToZero()
    {
        var logger = new Logger(_directory.Root, "health-drain.log")
        {
            MinimumLevel = LogLevel.Trace
        };

        for (int i = 0; i < 2000; i++)
        {
            logger.Trace("Health", $"message-{i}");
        }

        logger.Dispose();

        Assert.Equal(0, logger.QueueDepth);
    }

    [Fact]
    public async Task DisposeAsync_DrainsQueueDepthToZero()
    {
        var logger = new Logger(_directory.Root, "health-async-drain.log")
        {
            MinimumLevel = LogLevel.Trace
        };

        for (int i = 0; i < 2000; i++)
        {
            logger.Trace("Health", $"message-{i}");
        }

        await logger.DisposeAsync();

        Assert.Equal(0, logger.QueueDepth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_WhenWriterTimesOut_DefersPrimitiveDisposalUntilWriterCompletes(bool disposeAsync)
    {
        var writerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Logger logger = Logger.CreateWithWriterTaskForTests(writerCompletion.Task, disposeWaitMs: 20);

        if (disposeAsync)
        {
            await logger.DisposeAsync();
        }
        else
        {
            logger.Dispose();
        }

        Assert.False(logger.WriterPrimitivesDisposedForTests);

        writerCompletion.TrySetResult();
        await TestExecutionGuards.WaitUntilAsync(
            () => logger.WriterPrimitivesDisposedForTests,
            "Logger writer primitives were not disposed after the timed-out writer completed.",
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Dispose_ReleasesWriterResourcesWithinTimeout()
    {
        const string fileName = "health-dispose-release.log";
        string logPath = Path.Combine(_directory.Root, fileName);

        var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        for (int i = 0; i < 256; i++)
        {
            logger.Trace("Health", $"message-{i}");
        }

        logger.Dispose();

        bool released = SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    using var stream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(2));

        Assert.True(released);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var logger = new Logger(_directory.Root, "health-dispose-idempotent.log")
        {
            MinimumLevel = LogLevel.Trace
        };

        logger.Trace("Health", "first");

        logger.Dispose();
        logger.Dispose();

        Assert.False(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightMessageFactory_AndPersistsMessage()
    {
        const string fileName = "health-dispose-inflight.log";
        string logPath = Path.Combine(_directory.Root, fileName);
        var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        var factoryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFactoryToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task producerTask = Task.Run(() =>
            logger.Info("Health", () =>
            {
                factoryEntered.TrySetResult(true);
                allowFactoryToFinish.Task.GetAwaiter().GetResult();
                return "in-flight-message";
            }), TestContext.Current.CancellationToken);

        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Task disposeTask = Task.Run(logger.Dispose, TestContext.Current.CancellationToken);
        allowFactoryToFinish.TrySetResult(true);

        await Task.WhenAll(producerTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        string logText = logger.DisposeAndReadLogTextForTests();
        Assert.Contains("in-flight-message", logText, StringComparison.Ordinal);
        Assert.Equal(0, logger.QueueDepth);
    }

    [Fact]
    public void IsEnabled_ReturnsFalseForFatal_WhenMinimumLevelIsNone()
    {
        using var logger = new Logger(_directory.Root, "health-none-level.log")
        {
            MinimumLevel = LogLevel.None
        };

        Assert.False(logger.IsEnabled(LogLevel.Fatal));
    }

    [Fact]
    public void ApplyLogLevel_None_DeletesExistingLogFile()
    {
        const string fileName = "health-none-delete.log";
        string logPath = Path.Combine(_directory.Root, fileName);

        using var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Info
        };

        logger.Info("Health", "create-file");
        logger.Dispose();

        Assert.True(File.Exists(logPath));

        using var loggerForApply = new Logger(_directory.Root, fileName);
        loggerForApply.ApplyLogLevel(new Settings { Miscellaneous = new MiscellaneousSettings { LogLevel = "None" } });

        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void RotationCount_Increments_WhenLogExceedsMaxSize()
    {
        using var logger = new Logger(_directory.Root, "health-rotation.log")
        {
            MinimumLevel = LogLevel.Trace
        };

        string payload = new('Y', 64 * 1024);
        for (int i = 0; i < 120; i++)
        {
            logger.Trace("Health", payload);
        }

        logger.Dispose();

        Assert.True(logger.RotationCount > 0);
    }

    [Fact]
    public void Warning_WithException_SanitizesPathInExceptionMessage()
    {
        const string fileName = "health-sanitize.log";
        string logPath = Path.Combine(_directory.Root, fileName);
        const string rawPath = @"C:\path\to\settings.json";

        using var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        var ex = new IOException($"Failed to open {rawPath} for read");
        logger.Warning("Health", "path-sanitize-check", nameof(Warning_WithException_SanitizesPathInExceptionMessage), ex);
        logger.Dispose();

        string logText = TestLogFileAssert.WaitForLogText(logPath, "<path>");
        Assert.Contains("<path>", logText);
        Assert.DoesNotContain(rawPath, logText);
    }

    [Fact]
    public void Debug_WithException_SanitizesPathInExceptionMessage()
    {
        const string fileName = "health-debug-sanitize.log";
        string logPath = Path.Combine(_directory.Root, fileName);
        const string rawPath = @"C:\\private\\secret\\settings.json";

        using var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        var ex = new IOException($"Failed to open {rawPath} in debug");
        logger.Log(LogLevel.Debug, "Health", "debug-path-sanitize-check", nameof(Debug_WithException_SanitizesPathInExceptionMessage), ex);
        logger.Dispose();

        string logText = TestLogFileAssert.WaitForLogText(logPath, "<path>");
        Assert.Contains("<path>", logText);
        Assert.DoesNotContain(rawPath, logText);
    }

    [Fact]
    public void Warning_WithException_SanitizesUncPathInExceptionMessage()
    {
        const string fileName = "health-unc-sanitize.log";
        string logPath = Path.Combine(_directory.Root, fileName);
        const string rawPath = @"\\\\server\\share\\secrets.txt";

        using var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        var ex = new IOException($"UNC open failed: {rawPath}");
        logger.Warning("Health", "unc-path-sanitize-check", nameof(Warning_WithException_SanitizesUncPathInExceptionMessage), ex);
        logger.Dispose();

        string logText = TestLogFileAssert.WaitForLogText(logPath, "<path>");
        Assert.Contains("<path>", logText);
        Assert.DoesNotContain(rawPath, logText);
    }

    [Fact]
    public void SanitizeExceptionDetails_RemovesUncPathsFromStackTraceText()
    {
        const string rawDetails = "\n  Exception: IOException: boom\n   at AudioPilot.Tests.Logging.LoggerHealthTests.Test() in \\\\server\\share\\secret\\LoggerHealthTests.cs:line 42";

        string sanitized = Logger.SanitizeExceptionDetails(rawDetails);

        Assert.Contains("at AudioPilot.Tests.Logging.LoggerHealthTests.Test()", sanitized);
        Assert.DoesNotContain("\\\\server\\share\\secret", sanitized);
        Assert.DoesNotContain("LoggerHealthTests.cs:line 42", sanitized);
    }

    [Fact]
    public void WriteBatch_WhenFallbackTriggered_EmitsExceptionTypeWithoutRawMessage()
    {
        using var logger = new Logger(_directory.Root, "health-fallback.log")
        {
            MinimumLevel = LogLevel.Trace
        };

        TestPrivateAccess.SetField(logger, "_logFilePath", _directory.Root);
        MethodInfo? writeBatch = typeof(Logger).GetMethod("WriteBatch", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(writeBatch);

        using var consoleCapture = new StringWriter();
        TextWriter originalOut = Console.Out;
        Console.SetOut(consoleCapture);
        try
        {
            var logEntry = new Logger.LogEntry(DateTime.Now, LogLevel.Info, "Test", "queued-message", null, null);
            writeBatch!.Invoke(logger, [new List<Logger.LogEntry> { logEntry }]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        string output = consoleCapture.ToString();
        Assert.Contains("Failed to write to log file: UnauthorizedAccessException", output, StringComparison.Ordinal);
        Assert.Contains("queued-message", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory.Root, output, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }
}

