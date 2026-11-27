namespace AudioPilot.Tests.Helpers;

public sealed class TestLogFileAssertTests : IDisposable
{
    private static readonly TimeSpan WaiterGuard = TimeSpan.FromSeconds(10);
    private readonly string _tempDirectory;

    public TestLogFileAssertTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AudioPilot.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenFragmentAppears()
    {
        string logPath = Path.Combine(_tempDirectory, "single.log");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogTextAfterWaitStartsForTests(logPath, "ready", () => waitStarted.TrySetResult(), (int)WaiterGuard.TotalMilliseconds);
        });

        await waitStarted.Task.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(logPath, "ready", TestContext.Current.CancellationToken);

        string logText = await waitTask.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);

        Assert.Contains("ready", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenAllFragmentsAppear()
    {
        string logPath = Path.Combine(_tempDirectory, "multi.log");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogTextAfterWaitStartsForTests(logPath, () => waitStarted.TrySetResult(), (int)WaiterGuard.TotalMilliseconds, "alpha", "beta");
        });

        await waitStarted.Task.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(logPath, "alpha", TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(logPath, " beta", TestContext.Current.CancellationToken);

        string logText = await waitTask.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);

        Assert.Contains("alpha", logText, StringComparison.Ordinal);
        Assert.Contains("beta", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenFileAppearsAfterWaitStarts()
    {
        string logPath = Path.Combine(_tempDirectory, "created-late.log");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogTextAfterWaitStartsForTests(logPath, "created", () => waitStarted.TrySetResult(), (int)WaiterGuard.TotalMilliseconds);
        });

        await waitStarted.Task.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(logPath, "created", TestContext.Current.CancellationToken);

        string logText = await waitTask.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);

        Assert.Contains("created", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForLogText_ReturnsWhenFileIsReplacedDuringWait()
    {
        string logPath = Path.Combine(_tempDirectory, "rotated.log");
        await File.WriteAllTextAsync(logPath, "before", TestContext.Current.CancellationToken);
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> waitTask = Task.Run(() =>
        {
            return TestLogFileAssert.WaitForLogTextAfterWaitStartsForTests(logPath, "after", () => waitStarted.TrySetResult(), (int)WaiterGuard.TotalMilliseconds);
        });

        await waitStarted.Task.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);
        File.Delete(logPath);
        await File.WriteAllTextAsync(logPath, "after", TestContext.Current.CancellationToken);

        string logText = await waitTask.WaitAsync(WaiterGuard, TestContext.Current.CancellationToken);

        Assert.Contains("after", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitForLogText_ReturnsAvailableTextOnTimeout()
    {
        string logPath = Path.Combine(_tempDirectory, "timeout.log");
        File.WriteAllText(logPath, "partial");

        string logText = TestLogFileAssert.WaitForLogText(logPath, "missing", timeoutMs: 50);

        Assert.Equal("partial", logText);
    }

    [Fact]
    public void TrySignal_ReturnsFalse_WhenWaitHandleIsDisposed()
    {
        var waitHandle = new AutoResetEvent(false);
        waitHandle.Dispose();

        bool signaled = TestLogFileAssert.TrySignal(waitHandle);

        Assert.False(signaled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
