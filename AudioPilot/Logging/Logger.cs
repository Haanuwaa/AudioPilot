using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AudioPilot.Constants;
using AudioPilot.Models;

namespace AudioPilot.Logging
{
    public interface ILogger : IDisposable
    {
        LogLevel MinimumLevel { get; set; }
        bool IsEnabled(LogLevel level);
        void Log(LogLevel level, string category, string message, string? methodName = null, Exception? exception = null);
        void Log(LogLevel level, string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
        void Trace(string category, string message, string? methodName = null);
        void Trace(string category, Func<string> messageFactory, string? methodName = null);
        void Debug(string category, string message, string? methodName = null);
        void Debug(string category, Func<string> messageFactory, string? methodName = null);
        void Info(string category, string message, string? methodName = null);
        void Info(string category, Func<string> messageFactory, string? methodName = null);
        void Warning(string category, string message, string? methodName = null, Exception? exception = null);
        void Warning(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
        void Error(string category, string message, string? methodName = null, Exception? exception = null);
        void Error(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
        void Fatal(string category, string message, string? methodName = null, Exception? exception = null);
        void Fatal(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null);
    }

    public partial class Logger : ILogger, IAsyncDisposable
    {
        internal enum LoggerWriteMode
        {
            FileBacked,
            InMemory,
        }

        internal readonly struct LogEntry(DateTime timestamp, LogLevel level, string category, string message, string? methodName, string? exceptionDetails)
        {
            public readonly DateTime Timestamp = timestamp;
            public readonly LogLevel Level = level;
            public readonly string Category = category;
            public readonly string Message = message;
            public readonly string? MethodName = methodName;
            public readonly string? ExceptionDetails = exceptionDetails;
        }

        private const int MaxQueueSize = AppConstants.Logging.MaxQueueSize;
        private const int MaxBatchSize = AppConstants.Logging.MaxBatchSize;
        private const long MaxLogFileBytes = AppConstants.Logging.MaxLogFileBytes;
        private const int MaxExceptionDetailsChars = AppConstants.Logging.MaxExceptionDetailsChars;
        private const int LogBackupRetentionCount = AppConstants.Files.LogBackupRetentionCount;
        private const int LogResetIntervalDays = AppConstants.Logging.LogResetIntervalDays;
        private const int LogBackupMaxAgeDays = AppConstants.Logging.LogBackupMaxAgeDays;

        private static readonly Lazy<Logger> _instance = new(
            () => new Logger(AppDataPaths.GetWritableDataRoot()),
            LazyThreadSafetyMode.ExecutionAndPublication);

        [GeneratedRegex(@" in (?:(?:[A-Za-z]:\\)|(?:\\\\))[^\r\n]+", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionPathRegex();

        [GeneratedRegex(@"(?:[A-Za-z]:\\[^\r\n]+|\\\\[^\r\n]+)", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionMessagePathRegex();

        private readonly string _logFilePath;
        private readonly string _logBackupDirectory;
        private readonly string _logFileName;
        private readonly LoggerWriteMode _writeMode;
        private readonly StringBuilder? _inMemoryLogBuffer;
        private readonly ConcurrentQueue<LogEntry> _logQueue = new();
        private readonly Lock _fileLock = new();
        private readonly Lock _producerStateLock = new();
        private readonly Lock _consoleLock = new();
        private readonly Task _logWriterTask;
        private readonly CancellationTokenSource _cts;
        private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
        private readonly int _disposeWaitMs;
        private readonly bool _consoleAttached;
        private int _consoleOutputFailureCount;
        private const int MaxConsoleOutputFailures = 3;
        private int _queueSignalPending;
        private int _shutdownDrainClaimed;
        private int _disposeStarted;
        private int _writerPrimitivesDisposed;
        private bool _disposed;
        private DateTime _currentDate;
        private int _queuedCount;
        private long _droppedMessageCount;
        private long _totalDroppedMessageCount;
        private int _rotationCount;
        private readonly int _maxQueueCapacity = MaxQueueSize;
        private TaskCompletionSource<bool> _activeProducerCompletionSource = CreateCompletedProducerCompletionSource();
        private int _activeProducerCount;

        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        internal int QueueDepth => Volatile.Read(ref _queuedCount);
        internal long PendingDroppedCount => Interlocked.Read(ref _droppedMessageCount);
        internal long TotalDroppedCount => Interlocked.Read(ref _totalDroppedMessageCount);
        internal int RotationCount => Volatile.Read(ref _rotationCount);
        internal int MaxQueueCapacity => _maxQueueCapacity;
        internal bool WriterPrimitivesDisposedForTests => Volatile.Read(ref _writerPrimitivesDisposed) != 0;
        internal static Func<int, Task> RetryDelayAsyncForTests { get; set; } = Task.Delay;
        internal void SetQueueDepthForTests(int value) => Interlocked.Exchange(ref _queuedCount, value);
        internal bool TryReserveQueueSlotForTests() => TryReserveQueueSlot();
        internal bool TryEnqueueForTests(string line)
        {
            if (!TryReserveQueueSlot())
            {
                return false;
            }

            var entry = new LogEntry(DateTime.Now, LogLevel.Info, "Test", line, null, null);
            _logQueue.Enqueue(entry);
            SignalQueue();
            return true;
        }

        public static Logger Instance => _instance.Value;

        public Logger(string? logDirectory = null, string logFileName = AppConstants.Files.LogFileName)
            : this(logDirectory, logFileName, LoggerWriteMode.FileBacked)
        {
        }

        internal static Logger CreateInMemoryForTests(string logFileName = AppConstants.Files.LogFileName)
            => new(logDirectory: null, logFileName, LoggerWriteMode.InMemory);

        internal static Logger CreateWithWriterTaskForTests(Task writerTask, int disposeWaitMs)
        {
            ArgumentNullException.ThrowIfNull(writerTask);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(disposeWaitMs);
            return new Logger(
                logDirectory: null,
                AppConstants.Files.LogFileName,
                LoggerWriteMode.InMemory,
                writerTask,
                disposeWaitMs);
        }

        internal Logger(
            string? logDirectory,
            string logFileName,
            LoggerWriteMode writeMode,
            Task? writerTaskForTests = null,
            int disposeWaitMs = AppConstants.Logging.DisposeWaitMs)
        {
            _writeMode = writeMode;
            _logFileName = logFileName;
            _currentDate = DateTime.Now.Date;
            _cts = new CancellationTokenSource();
            _disposeWaitMs = disposeWaitMs;

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                _logFilePath = string.Empty;
                _logBackupDirectory = string.Empty;
                _inMemoryLogBuffer = new StringBuilder();
                _logWriterTask = writerTaskForTests ?? Task.CompletedTask;
                _consoleAttached = false;
                return;
            }

            var logDir = logDirectory ?? AppDataPaths.GetWritableDataRoot();
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, _logFileName);
            _logBackupDirectory = Path.Combine(logDir, AppConstants.Files.BackupFolderName);
            _logWriterTask = Task.Run(() => ProcessLogQueueAsync(_cts.Token));

            _consoleAttached = DetectConsoleAttachment();

            CheckAndResetForNewDay();
        }

        public bool IsEnabled(LogLevel level)
        {
            return level >= MinimumLevel && !_disposed;
        }

        private static bool DetectConsoleAttachment()
        {
            if (Environment.GetEnvironmentVariable("AUDIOPILOT_DISABLE_CONSOLE_LOGGING") == "1")
            {
                return false;
            }

            try
            {
                return Console.OpenStandardOutput() != Stream.Null;
            }
            catch
            {
                return false;
            }
        }

        private static TaskCompletionSource<bool> CreateCompletedProducerCompletionSource()
        {
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            completionSource.TrySetResult(true);
            return completionSource;
        }

        private void SignalQueue()
        {
            if (Interlocked.Exchange(ref _queueSignalPending, 1) == 0)
            {
                _queueSignal.Release();
            }
        }

        private async Task ProcessLogQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                while (true)
                {
                    var messages = DequeueBatch(MaxBatchSize);
                    if (messages.Count == 0)
                    {
                        break;
                    }

                    WriteBatch(messages);
                }

                Interlocked.Exchange(ref _queueSignalPending, 0);
                if (!_logQueue.IsEmpty)
                {
                    SignalQueue();
                }
            }

            if (Interlocked.CompareExchange(ref _shutdownDrainClaimed, 1, 0) != 0)
            {
                return;
            }

            while (true)
            {
                var remaining = DequeueBatch(MaxBatchSize);
                if (remaining.Count == 0)
                {
                    break;
                }

                WriteBatch(remaining);
            }
        }

        private List<LogEntry> DequeueBatch(int maxItems)
        {
            var messages = new List<LogEntry>(Math.Min(maxItems, MaxBatchSize));
            while (_logQueue.TryDequeue(out LogEntry entry))
            {
                messages.Add(entry);
                Interlocked.Decrement(ref _queuedCount);
                if (messages.Count >= maxItems)
                {
                    break;
                }
            }

            return messages;
        }

        private void WriteBatch(List<LogEntry> entries)
        {
            if (entries.Count == 0)
                return;

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    AppendDroppedMessageWarningIfNeeded(entries);
                    foreach (LogEntry entry in entries)
                    {
                        _inMemoryLogBuffer!.AppendLine(FormatLogEntry(entry));
                    }
                }

                return;
            }

            lock (_fileLock)
            {
                try
                {
                    CheckAndResetForNewDay();
                    RotateIfNeeded(entries);
                    AppendDroppedMessageWarningIfNeeded(entries);

                    using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    foreach (var entry in entries)
                    {
                        writer.WriteLine(FormatLogEntry(entry));
                    }
                    writer.Flush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to write to log file: {ex.GetType().Name}");
                    foreach (var entry in entries)
                    {
                        Console.WriteLine(FormatLogEntry(entry));
                    }
                }
            }

            if (_consoleAttached && _consoleOutputFailureCount < MaxConsoleOutputFailures)
            {
                lock (_consoleLock)
                {
                    try
                    {
                        foreach (var entry in entries)
                        {
                            Console.WriteLine(FormatLogEntry(entry));
                        }
                    }
                    catch (Exception ex)
                    {
                        _consoleOutputFailureCount++;
                        if (_consoleOutputFailureCount >= MaxConsoleOutputFailures)
                        {
                            EmitInternalDiagnostic($"Console output disabled after {_consoleOutputFailureCount} failures: {ex.GetType().Name}");
                        }
                    }
                }
            }
        }

        private void AppendDroppedMessageWarningIfNeeded(List<LogEntry> entries)
        {
            long dropped = Interlocked.Exchange(ref _droppedMessageCount, 0);
            if (dropped <= 0)
            {
                return;
            }

            var warningEntry = new LogEntry(DateTime.Now, LogLevel.Warning, "Logger", $"{dropped} log message(s) dropped due to queue overflow", null, null);
            entries.Add(warningEntry);
        }

        private void RotateIfNeeded(List<LogEntry> incomingEntries)
        {
            try
            {
                long incomingBytesEstimate = 0;
                for (int index = 0; index < incomingEntries.Count; index++)
                {
                    incomingBytesEstimate += (FormatLogEntry(incomingEntries[index]).Length + Environment.NewLine.Length) * sizeof(char);
                }

                long currentLength = File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0;

                if ((currentLength + incomingBytesEstimate) < MaxLogFileBytes)
                {
                    return;
                }

                if (!File.Exists(_logFilePath))
                {
                    return;
                }

                bool rotated = ArchiveActiveLogFile();

                if (rotated)
                {
                    Interlocked.Increment(ref _rotationCount);
                    PruneOldLogBackups();
                }
            }
            catch (Exception ex)
            {
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    System.Diagnostics.Debug.WriteLine($"Logger rotation failed: {ex.GetType().Name}");
                }
            }
        }

        private void CheckAndResetForNewDay()
        {
            var today = DateTime.Now.Date;
            if ((today - _currentDate).TotalDays >= LogResetIntervalDays)
            {
                _currentDate = today;
                try
                {
                    if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 0)
                    {
                        _ = ArchiveActiveLogFile();
                    }

                    PruneOldLogBackups();
                }
                catch (Exception ex)
                {
                    if (System.Diagnostics.Debugger.IsAttached)
                    {
                        System.Diagnostics.Debug.WriteLine($"Logger daily reset failed: {ex.GetType().Name}");
                    }
                }
            }
        }

        public void Log(LogLevel level, string category, string message, string? methodName = null, Exception? exception = null)
        {
            if (level < MinimumLevel)
                return;

            if (!TryBeginProducer())
            {
                return;
            }

            try
            {
                LogCore(level, category, message, methodName, exception);
            }
            finally
            {
                EndProducer();
            }
        }

        public void Log(LogLevel level, string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
        {
            if (level < MinimumLevel)
            {
                return;
            }

            if (!TryBeginProducer())
            {
                return;
            }

            try
            {
                string message;
                try
                {
                    message = messageFactory();
                }
                catch (Exception ex)
                {
                    message = $"<log message factory failed: {ex.GetType().Name}>";
                }

                LogCore(level, category, message, methodName, exception);
            }
            finally
            {
                EndProducer();
            }
        }

        private void LogCore(LogLevel level, string category, string message, string? methodName, Exception? exception)
        {
            var timestamp = DateTime.Now;
            string? exceptionDetails = exception != null ? FormatExceptionDetails(exception) : null;
            var logEntry = new LogEntry(timestamp, level, category, message, methodName, exceptionDetails);

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    var logLine = FormatLogEntry(logEntry);
                    _inMemoryLogBuffer!.AppendLine(logLine);
                }

                return;
            }

            if (!TryReserveQueueSlot())
            {
                return;
            }

            _logQueue.Enqueue(logEntry);
            SignalQueue();
        }

        private bool TryReserveQueueSlot()
        {
            int reservedCount = Interlocked.Increment(ref _queuedCount);
            if (reservedCount <= MaxQueueSize)
            {
                return true;
            }

            Interlocked.Decrement(ref _queuedCount);
            Interlocked.Increment(ref _droppedMessageCount);
            Interlocked.Increment(ref _totalDroppedMessageCount);
            return false;
        }

        private static string FormatLogEntry(LogEntry entry)
        {
            var timestamp = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var method = string.IsNullOrEmpty(entry.MethodName) ? "" : $" [{entry.MethodName}]";
            var exceptionDetails = entry.ExceptionDetails ?? "";

            return $"[{timestamp}] [{entry.Level}] [{entry.Category}] {entry.Message}{method}{exceptionDetails}";
        }

        private static string FormatExceptionDetails(Exception exception)
        {
            var exceptionMessage = SanitizeExceptionMessage(exception.Message);
            var exceptionDetails = $"\n  Exception: {exception.GetType().Name}: {exceptionMessage}\n{exception.StackTrace}";

            exceptionDetails = SanitizeExceptionDetails(exceptionDetails);

            if (exceptionDetails.Length > MaxExceptionDetailsChars)
            {
                exceptionDetails = exceptionDetails[..MaxExceptionDetailsChars] + "\n  [truncated]";
            }

            return exceptionDetails;
        }

        private bool TryBeginProducer()
        {
            lock (_producerStateLock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_activeProducerCount == 0)
                {
                    _activeProducerCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _activeProducerCount++;
                return true;
            }
        }

        private void EndProducer()
        {
            TaskCompletionSource<bool>? completionSource = null;

            lock (_producerStateLock)
            {
                if (_activeProducerCount == 0)
                {
                    return;
                }

                _activeProducerCount--;
                if (_activeProducerCount == 0)
                {
                    completionSource = _activeProducerCompletionSource;
                }
            }

            completionSource?.TrySetResult(true);
        }

        private bool WaitForActiveProducersBeforeShutdown()
        {
            Task activeProducersTask = GetActiveProducersCompletionTask();

            try
            {
                return activeProducersTask.Wait(_disposeWaitMs);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException or TaskCanceledException))
            {
                return true;
            }
        }

        private async Task<bool> WaitForActiveProducersBeforeShutdownAsync()
        {
            Task activeProducersTask = GetActiveProducersCompletionTask();

            try
            {
                await activeProducersTask
                    .WaitAsync(TimeSpan.FromMilliseconds(_disposeWaitMs))
                    .ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }

        private Task GetActiveProducersCompletionTask()
        {
            lock (_producerStateLock)
            {
                return _activeProducerCount == 0
                    ? Task.CompletedTask
                    : _activeProducerCompletionSource.Task;
            }
        }

        internal static string SanitizeExceptionMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return message;
            }

            return ExceptionMessagePathRegex().Replace(message, "<path>");
        }

        public void ApplyLogLevel(Settings? settings)
        {
            LogPrivacy.ApplySettings(settings);

            if (settings == null || string.IsNullOrEmpty(settings.Miscellaneous.LogLevel))
                return;

            if (Enum.TryParse<LogLevel>(settings.Miscellaneous.LogLevel, true, out var level))
            {
                MinimumLevel = level;

                if (level == LogLevel.None)
                {
                    ClearQueuedMessages();
                    DeleteLogFilesIfPresent();
                    return;
                }

                Info("Logger", $"Log level set to: {level}");
            }
            else
            {
                Warning("Logger", $"Invalid log level '{settings.Miscellaneous.LogLevel}' in settings, defaulting to Info");
                MinimumLevel = LogLevel.Info;
            }
        }

        private void ClearQueuedMessages()
        {
            while (_logQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedCount);
            }
        }

        private void DeleteLogFilesIfPresent()
        {
            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    _inMemoryLogBuffer!.Clear();
                }

                return;
            }

            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_logFilePath))
                    {
                        File.Delete(_logFilePath);
                    }

                    foreach (string backupPath in EnumerateLogBackupCandidates())
                    {
                        if (File.Exists(backupPath))
                        {
                            File.Delete(backupPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    EmitInternalDiagnostic("Failed to delete log files after disabling logging", ex);
                }
            }
        }

        public void Trace(string category, string message, string? methodName = null)
            => Log(LogLevel.Trace, category, message, methodName);

        public void Trace(string category, Func<string> messageFactory, string? methodName = null)
            => Log(LogLevel.Trace, category, messageFactory, methodName);

        public void Debug(string category, string message, string? methodName = null)
            => Log(LogLevel.Debug, category, message, methodName);

        public void Debug(string category, Func<string> messageFactory, string? methodName = null)
            => Log(LogLevel.Debug, category, messageFactory, methodName);

        public void Info(string category, string message, string? methodName = null)
            => Log(LogLevel.Info, category, message, methodName);

        public void Info(string category, Func<string> messageFactory, string? methodName = null)
            => Log(LogLevel.Info, category, messageFactory, methodName);

        public void Warning(string category, string message, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Warning, category, message, methodName, exception);

        public void Warning(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Warning, category, messageFactory, methodName, exception);

        public void Error(string category, string message, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Error, category, message, methodName, exception);

        public void Error(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Error, category, messageFactory, methodName, exception);

        public void Fatal(string category, string message, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Fatal, category, message, methodName, exception);

        public void Fatal(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
            => Log(LogLevel.Fatal, category, messageFactory, methodName, exception);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposed = true;

            bool producersCompleted = WaitForActiveProducersBeforeShutdown();
            if (!producersCompleted)
            {
                EmitInternalDiagnostic($"Logger producer shutdown timed out after {_disposeWaitMs}ms; activeProducersPossible=true pendingQueueDepth={QueueDepth}");
            }

            _cts?.Cancel();
            _queueSignal.Release();

            DrainQueuedMessagesForShutdown();

            bool shutdownCompleted = false;
            try
            {
                shutdownCompleted = _logWriterTask.Wait(_disposeWaitMs);
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException or TaskCanceledException))
            {
                shutdownCompleted = true;
            }
            catch (Exception ex)
            {
                EmitInternalDiagnostic("Logger shutdown wait failed", ex);
            }

            if (!shutdownCompleted)
            {
                EmitInternalDiagnostic($"Logger shutdown timed out after {_disposeWaitMs}ms; pendingQueueDepth={QueueDepth}");
            }
            else if (_logWriterTask.IsFaulted && _logWriterTask.Exception != null)
            {
                EmitInternalDiagnostic("Logger writer task faulted during shutdown", _logWriterTask.Exception);
            }

            DisposeWriterPrimitivesAfterCompletion(producersCompleted, shutdownCompleted);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _disposed = true;

            bool producersCompleted = await WaitForActiveProducersBeforeShutdownAsync().ConfigureAwait(false);
            if (!producersCompleted)
            {
                EmitInternalDiagnostic($"Logger producer shutdown timed out after {_disposeWaitMs}ms; activeProducersPossible=true pendingQueueDepth={QueueDepth}");
            }

            _cts?.Cancel();
            _queueSignal.Release();

            DrainQueuedMessagesForShutdown();

            bool shutdownCompleted = false;
            try
            {
                await _logWriterTask
                    .WaitAsync(TimeSpan.FromMilliseconds(_disposeWaitMs))
                    .ConfigureAwait(false);
                shutdownCompleted = true;
            }
            catch (TimeoutException)
            {
                shutdownCompleted = false;
            }
            catch (OperationCanceledException)
            {
                shutdownCompleted = true;
            }
            catch (Exception ex)
            {
                EmitInternalDiagnostic("Logger shutdown wait failed", ex);
            }

            if (!shutdownCompleted)
            {
                EmitInternalDiagnostic($"Logger writer shutdown timed out after {_disposeWaitMs}ms; pendingQueueDepth={QueueDepth}");
            }

            DisposeWriterPrimitivesAfterCompletion(producersCompleted, shutdownCompleted);
            GC.SuppressFinalize(this);
        }

        private void DisposeWriterPrimitivesAfterCompletion(bool producersCompleted, bool writerCompleted)
        {
            if (producersCompleted && writerCompleted)
            {
                DisposeWriterPrimitives();
                return;
            }

            Task cleanupPrerequisite = Task.WhenAll(_logWriterTask, GetActiveProducersCompletionTask());
            _ = cleanupPrerequisite.ContinueWith(
                static (_, state) =>
                {
                    var logger = (Logger)state!;
                    if (logger._logWriterTask.IsFaulted && logger._logWriterTask.Exception != null)
                    {
                        EmitInternalDiagnostic("Logger writer task faulted after shutdown timeout", logger._logWriterTask.Exception);
                    }

                    logger.DisposeWriterPrimitives();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeWriterPrimitives()
        {
            if (Interlocked.Exchange(ref _writerPrimitivesDisposed, 1) != 0)
            {
                return;
            }

            _cts.Dispose();
            _queueSignal.Dispose();
        }

        internal string DisposeAndReadLogTextForTests()
        {
            Dispose();

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    return _inMemoryLogBuffer!.ToString();
                }
            }

            lock (_fileLock)
            {
                if (!File.Exists(_logFilePath))
                {
                    return string.Empty;
                }

                using var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        private void DrainQueuedMessagesForShutdown()
        {
            if (Interlocked.CompareExchange(ref _shutdownDrainClaimed, 1, 0) != 0)
            {
                return;
            }

            while (true)
            {
                var messages = DequeueBatch(MaxBatchSize);
                if (messages.Count == 0)
                {
                    break;
                }

                WriteBatch(messages);
            }
        }

        private static void EmitInternalDiagnostic(string message, Exception? exception = null)
        {
            string payload = FormatInternalDiagnosticPayload(message, exception);

            try
            {
                Console.WriteLine(payload);
            }
            catch
            {
            }

            if (System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Debug.WriteLine(payload);
            }
        }

        internal static string FormatInternalDiagnosticPayload(string message, Exception? exception)
        {
            return exception == null
                ? message
                : $"{message}: {SanitizeExceptionMessage(exception.GetBaseException().Message)}";
        }

        internal static string SanitizeExceptionDetails(string exceptionDetails)
        {
            if (string.IsNullOrEmpty(exceptionDetails) || !exceptionDetails.Contains(" in ", StringComparison.Ordinal))
            {
                return exceptionDetails;
            }

            return ExceptionPathRegex().Replace(exceptionDetails, "");
        }

        private bool ArchiveActiveLogFile()
        {
            if (!File.Exists(_logFilePath))
            {
                return false;
            }

            Directory.CreateDirectory(_logBackupDirectory);
            RotateLogBackups();

            string backupPath = GetLogBackupPath();
            bool archived = false;
            for (int attempt = 0; attempt < 3 && !archived; attempt++)
            {
                try
                {
                    File.Move(_logFilePath, backupPath, overwrite: true);
                    archived = true;
                }
                catch (IOException)
                {
                    WaitForRetryDelay(attempt);
                }
                catch (UnauthorizedAccessException)
                {
                    WaitForRetryDelay(attempt);
                }
            }

            if (!archived && File.Exists(_logFilePath))
            {
                File.Copy(_logFilePath, backupPath, overwrite: true);
                File.WriteAllText(_logFilePath, string.Empty);
                archived = true;
            }

            return archived;
        }

        private static void WaitForRetryDelay(int attempt)
        {
            RetryDelayAsyncForTests(10 * (attempt + 1)).GetAwaiter().GetResult();
        }

        private void RotateLogBackups()
        {
            for (int index = LogBackupRetentionCount - 2; index >= 1; index--)
            {
                MoveLogBackupIfExists(GetLogBackupPath(index), GetLogBackupPath(index + 1));
            }

            MoveLogBackupIfExists(GetLogBackupPath(), GetLogBackupPath(1));
        }

        private void PruneOldLogBackups()
        {
            if (!Directory.Exists(_logBackupDirectory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-LogBackupMaxAgeDays);
            foreach (string backupPath in EnumerateLogBackupCandidates())
            {
                if (!File.Exists(backupPath))
                {
                    continue;
                }

                DateTime lastWrite = File.GetLastWriteTimeUtc(backupPath);
                if (lastWrite < cutoff)
                {
                    File.Delete(backupPath);
                }
            }
        }

        private IEnumerable<string> EnumerateLogBackupCandidates()
        {
            yield return GetLogBackupPath();

            for (int index = 1; index < LogBackupRetentionCount; index++)
            {
                yield return GetLogBackupPath(index);
            }
        }

        private string GetLogBackupPath(int? index = null)
        {
            return index is null
                ? Path.Combine(_logBackupDirectory, _logFileName + ".bak")
                : Path.Combine(_logBackupDirectory, _logFileName + $".bak.{index.Value}");
        }

        private static void MoveLogBackupIfExists(string source, string destination)
        {
            if (!File.Exists(source))
            {
                return;
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }
}
