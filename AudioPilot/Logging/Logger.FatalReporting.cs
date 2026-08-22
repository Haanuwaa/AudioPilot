namespace AudioPilot.Logging;

internal enum FatalLogWriteResult { Written, Disabled, Unavailable, TimedOut }

public partial class Logger
{
    private Task<FatalLogWriteResult>? _fatalReport;

    /// <summary>Attempts one critical write off the caller, without waiting indefinitely for log-file or producer locks.</summary>
    internal async Task<FatalLogWriteResult> ReportFatalAsync(string category, string message, Exception exception,
        TimeSpan? budget = null, TimeProvider? timeProvider = null)
    {
        if (MinimumLevel == LogLevel.None) return FatalLogWriteResult.Disabled;
        if (!IsEnabled(LogLevel.Fatal)) return FatalLogWriteResult.Unavailable;
        Task<FatalLogWriteResult>? operation = Volatile.Read(ref _fatalReport);
        if (operation == null)
        {
            var completion = new TaskCompletionSource<FatalLogWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = Interlocked.CompareExchange(ref _fatalReport, completion.Task, null);
            if (operation == null)
            {
                operation = completion.Task;
                _ = Task.Run(() =>
                {
                    bool producer = false;
                    FatalLogWriteResult result = FatalLogWriteResult.Unavailable;
                    try
                    {
                        if (MinimumLevel == LogLevel.None) result = FatalLogWriteResult.Disabled;
                        else if (producer = TryBeginProducer())
                        {
                            var entry = new LogEntry(DateTime.Now, LogLevel.Fatal, category, message, null, FormatExceptionDetails(exception));
                            result = WriteBatch([entry]) ? FatalLogWriteResult.Written
                                : MinimumLevel == LogLevel.None ? FatalLogWriteResult.Disabled : FatalLogWriteResult.Unavailable;
                        }
                    }
                    catch (Exception) { result = FatalLogWriteResult.Unavailable; }
                    finally
                    {
                        if (producer) EndProducer();
                        completion.TrySetResult(result);
                    }
                });
            }
        }
        try { return await operation.WaitAsync(budget ?? TimeSpan.FromMilliseconds(500), timeProvider ?? TimeProvider.System).ConfigureAwait(false); }
        catch (TimeoutException) { return FatalLogWriteResult.TimedOut; }
    }
}
