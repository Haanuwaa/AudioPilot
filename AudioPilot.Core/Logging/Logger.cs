using System.Collections.Concurrent;
using System.Security.Cryptography;
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

        internal sealed class LevelPolicy(LogLevel minimum, int disableGeneration = 0)
        {
            internal LogLevel Minimum { get; } = minimum;
            internal int DisableGeneration { get; } = disableGeneration;
        }
        internal readonly record struct PolicyStamp(LevelPolicy Level, int PrivacyVersion)
        {
            internal bool AllowsEntry(LogEntry entry) =>
                entry.Policy.PrivacyVersion == PrivacyVersion
                && entry.Policy.Level.DisableGeneration == Level.DisableGeneration
                && IsEnabled(entry.Level, Level.Minimum);

            internal bool AllowsProducer(LogLevel level, PolicyStamp captured) =>
                captured.PrivacyVersion == PrivacyVersion
                && captured.Level.DisableGeneration == Level.DisableGeneration
                && IsEnabled(level, Level.Minimum);

            internal bool AllowsBatch(PolicyStamp batchPolicy) =>
                PrivacyVersion == batchPolicy.PrivacyVersion
                && Level.DisableGeneration == batchPolicy.Level.DisableGeneration
                && Level.Minimum != LogLevel.None;
        }

        internal readonly struct LogEntry(DateTime timestamp, LogLevel level, string category, string message, string? methodName,
            string? exceptionDetails, PolicyStamp policy)
        {
            public readonly DateTime Timestamp = timestamp;
            public readonly LogLevel Level = level;
            public readonly string Category = category;
            public readonly string Message = message;
            public readonly string? MethodName = methodName;
            public readonly string? ExceptionDetails = exceptionDetails;
            public readonly PolicyStamp Policy = policy;
        }

        private const int MaxQueueSize = AppConstants.Logging.MaxQueueSize;
        private const int MaxBatchSize = AppConstants.Logging.MaxBatchSize;
        private readonly long _maxLogFileBytes;
        private const int MaxExceptionDetailsChars = AppConstants.Logging.MaxExceptionDetailsChars;
        private readonly int _logBackupRetentionCount;
        private const int LogResetIntervalDays = AppConstants.Logging.LogResetIntervalDays;
        private const int LogBackupMaxAgeDays = AppConstants.Logging.LogBackupMaxAgeDays;

        private static readonly Lazy<Logger> _instance = new(
            () => new Logger(),
            LazyThreadSafetyMode.ExecutionAndPublication);

        [GeneratedRegex(@" in (?:[A-Za-z]:[\\/]|\\\\|//)[^\r\n]+", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionPathRegex();

        [GeneratedRegex(@"(?:(?<!\w)[A-Za-z]:[\\/]|\\\\|(?<!:)//)[^\r\n]+", RegexOptions.CultureInvariant)]
        private static partial Regex ExceptionMessagePathRegex();

        private readonly string _logFilePath;
        private readonly string _logFileMutexName = string.Empty;
        private readonly string _logBackupDirectory;
        private readonly string _logFileName;
        private readonly LoggerWriteMode _writeMode;
        private readonly TimeProvider _retentionTimeProvider;
        private readonly StringBuilder? _inMemoryLogBuffer;
        private readonly ConcurrentQueue<LogEntry> _logQueue = new();
        private sealed record ReservedFatal(LogEntry Entry);
        private ReservedFatal? _reservedFatal;
        private readonly Lock _fileLock = new();
        private readonly Lock _producerStateLock = new();
        private readonly Lock _disposeSync = new();
        private Task? _shutdownTask;
        private Task<bool>? _disposeTask;
        private readonly Lock _consoleLock = new();
        private readonly Task _logWriterTask;
        private readonly CancellationTokenSource _cts;
        private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
        private readonly int _disposeWaitMs;
        private readonly bool _consoleAttached;
        private int _consoleOutputFailureCount;
        private const int MaxConsoleOutputFailures = 3;
        private int _queueSignalPending;
        private volatile bool _disposed;
        private DateTime _lastRetentionCheckDateUtc;
        private int _queuedCount;
        private long _droppedMessageCount;
        private long _totalDroppedMessageCount;
        private int _rotationCount;
        private long? _rotationFailureTimestamp;
        private string? _directoryError;
        private sealed record FileWriteAttempt(DateTimeOffset CompletedUtc, bool Succeeded, string? Error, long FailedBatches);
        private FileWriteAttempt? _lastFileWrite;

        internal LoggingHealthSnapshot CaptureHealth()
        {
            FileWriteAttempt? write = Volatile.Read(ref _lastFileWrite);
            return new(MinimumLevel.ToString(), LogPrivacy.IsRedactionEnabled, QueueDepth,
                write?.CompletedUtc, write?.Succeeded, write?.Error, write?.FailedBatches ?? 0, Volatile.Read(ref _directoryError));
        }
        private readonly int _maxQueueCapacity = MaxQueueSize;
        private TaskCompletionSource<bool>? _activeProducerCompletionSource;
        private int _activeProducerCount;

        private LevelPolicy _levelPolicy = new(LogLevel.Info);
        public LogLevel MinimumLevel
        {
            get => Volatile.Read(ref _levelPolicy).Minimum;
            set { lock (_producerStateLock) SetMinimumLevel(value); }
        }
        internal string? RootDirectory { get; }

        internal int QueueDepth => Volatile.Read(ref _queuedCount) + (Volatile.Read(ref _reservedFatal) == null ? 0 : 1);
        internal long PendingDroppedCount => Interlocked.Read(ref _droppedMessageCount);
        internal long TotalDroppedCount => Interlocked.Read(ref _totalDroppedMessageCount);
        internal int RotationCount => Volatile.Read(ref _rotationCount);
        internal int MaxQueueCapacity => _maxQueueCapacity;
        internal bool IsShutdownComplete => Volatile.Read(ref _shutdownTask)?.IsCompleted == true;
        internal bool WriterPrimitivesDisposedForTests => IsShutdownComplete;
        internal void SetQueueDepthForTests(int value) => Interlocked.Exchange(ref _queuedCount, value);
        internal bool TryReserveQueueSlotForTests() => TryReserveQueueSlot();
        internal bool TryEnqueueForTests(string line)
        {
            if (!TryReserveQueueSlot())
            {
                return false;
            }

            var entry = new LogEntry(DateTime.Now, LogLevel.Info, "Test", line, null, null, CapturePolicy());
            _logQueue.Enqueue(entry);
            SignalQueue();
            return true;
        }

        public static Logger Instance => _instance.Value;
        internal static bool InstanceShutdownComplete => !_instance.IsValueCreated || _instance.Value.IsShutdownComplete;

        internal static void DisposeInstanceIfCreated()
        {
            if (!_instance.IsValueCreated)
            {
                return;
            }

            try
            {
                _instance.Value.Dispose();
            }
            catch (Exception ex)
            {
                EmitInternalDiagnostic("Logger process shutdown failed", ex);
            }
        }

        internal static void DisposeInstanceNonBlockingIfCreated()
        {
            if (!_instance.IsValueCreated)
            {
                return;
            }

            try
            {
                _ = _instance.Value.DisposeAsync().AsTask();
            }
            catch (Exception ex)
            {
                EmitInternalDiagnostic("Logger non-blocking shutdown failed", ex);
            }
        }

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
            int disposeWaitMs = AppConstants.Logging.DisposeWaitMs,
            TimeProvider? retentionTimeProvider = null,
            long maxLogFileBytes = AppConstants.Logging.MaxLogFileBytes,
            int logBackupRetentionCount = AppConstants.Files.LogBackupRetentionCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLogFileBytes, 1024);
            ArgumentOutOfRangeException.ThrowIfLessThan(logBackupRetentionCount, 2);
            _maxLogFileBytes = maxLogFileBytes;
            _logBackupRetentionCount = logBackupRetentionCount;
            _writeMode = writeMode;
            _logFileName = logFileName;
            _retentionTimeProvider = retentionTimeProvider ?? TimeProvider.System;
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

            var logDir = Path.GetFullPath(logDirectory ?? AppDataPaths.GetLocalStateRoot());
            RootDirectory = logDir;
            _logFilePath = Path.Combine(logDir, _logFileName);
            string normalizedPath = Path.GetFullPath(_logFilePath).ToUpperInvariant();
            _logFileMutexName = @"Local\AudioPilot.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
            _logBackupDirectory = Path.Combine(logDir, AppConstants.Files.BackupFolderName);
            _consoleAttached = DetectConsoleAttachment();
            TryCreateLogDirectory();
            _logWriterTask = Task.Run(() => ProcessLogQueueAsync(_cts.Token));
        }

        public bool IsEnabled(LogLevel level)
        {
            return IsEnabled(level, MinimumLevel) && !_disposed;
        }

        private static bool IsEnabled(LogLevel level, LogLevel minimum) => minimum != LogLevel.None && level >= minimum;

        internal PolicyStamp CapturePolicy() => new(Volatile.Read(ref _levelPolicy), LogPrivacy.PolicyVersion);

        private void SetMinimumLevel(LogLevel level)
        {
            if (_levelPolicy.Minimum == level)
            {
                return;
            }

            int disableGeneration = level == LogLevel.None
                ? _levelPolicy.DisableGeneration + 1
                : _levelPolicy.DisableGeneration;
            Volatile.Write(ref _levelPolicy, new(level, disableGeneration));
        }

        private bool TryCreateLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(RootDirectory!);
                Volatile.Write(ref _directoryError, null);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Volatile.Write(ref _directoryError, ex.GetType().Name);
                return false;
            }
        }

        private static bool DetectConsoleAttachment()
        {
            if (Environment.GetEnvironmentVariable("AUDIOPILOT_DISABLE_CONSOLE_LOGGING") == "1")
            {
                return false;
            }

            try
            {
                using Stream stdout = Console.OpenStandardOutput();
                return stdout != Stream.Null;
            }
            catch
            {
                return false;
            }
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
            lock (_fileLock)
            {
                try
                {
                    if (_directoryError == null) ExecuteWithLogFileLock(CheckLogRetention);
                }
                catch (Exception ex)
                {
                    EmitInternalDiagnostic("Logger startup retention check failed", ex);
                }
            }

            while (!token.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(CancellationToken.None).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                while (true)
                {
                    var messages = DequeueBatch();
                    if (messages.Count == 0)
                    {
                        break;
                    }

                    WriteBatch(messages);
                }

                Interlocked.Exchange(ref _queueSignalPending, 0);
                if (!_logQueue.IsEmpty || Volatile.Read(ref _reservedFatal) != null)
                {
                    SignalQueue();
                }
            }
        }

        private List<LogEntry> DequeueBatch()
        {
            var messages = new List<LogEntry>(Math.Clamp(QueueDepth, 0, MaxBatchSize));
            if (Interlocked.Exchange(ref _reservedFatal, null) is { } fatal) messages.Add(fatal.Entry);
            while (_logQueue.TryDequeue(out LogEntry entry))
            {
                messages.Add(entry);
                Interlocked.Decrement(ref _queuedCount);
                if (messages.Count >= MaxBatchSize)
                {
                    break;
                }
            }

            return messages;
        }

        private bool WriteBatch(List<LogEntry> entries)
        {
            if (entries.Count == 0)
                return true;

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    PolicyStamp activePolicy = CapturePolicy();
                    entries.RemoveAll(entry => !activePolicy.AllowsEntry(entry));
                    if (activePolicy.Level.Minimum == LogLevel.None || entries.Count == 0) return false;
                    AppendDroppedMessageWarningIfNeeded(entries);
                    foreach (LogEntry entry in entries)
                    {
                        _inMemoryLogBuffer!.AppendLine(FormatLogEntry(entry));
                    }
                }

                return true;
            }

            string? fileError = null;
            bool lastLineWritten = false;
            string[] lines;
            PolicyStamp policy;
            lock (_fileLock)
            {
                policy = CapturePolicy();
                entries.RemoveAll(entry => !policy.AllowsEntry(entry));
                if (policy.Level.Minimum == LogLevel.None || entries.Count == 0) return false;
                long dropped = Interlocked.Read(ref _droppedMessageCount);
                if (dropped > 0) entries.Add(DroppedMessageWarning(dropped, policy));
                lines = new string[entries.Count];
                for (int index = 0; index < entries.Count; index++) lines[index] = FormatLogEntry(entries[index]);
                try
                {
                    ExecuteWithLogFileLock(() =>
                    {
                        if (!CapturePolicy().AllowsBatch(policy)) return;
                        if (_directoryError != null && !TryCreateLogDirectory())
                        {
                            fileError = _directoryError;
                            return;
                        }
                        CheckLogRetention();
                        fileError = AppendBoundedLines(lines, policy, out lastLineWritten);
                        if (dropped > 0 && lastLineWritten) Interlocked.Add(ref _droppedMessageCount, -dropped);
                    });
                }
                catch (Exception ex)
                {
                    fileError = ex.GetType().Name;
                    if (ex is DirectoryNotFoundException) Volatile.Write(ref _directoryError, fileError);
                }
                if (fileError != null || lastLineWritten)
                {
                    long failures = (_lastFileWrite?.FailedBatches ?? 0) + (fileError == null ? 0 : 1);
                    Volatile.Write(ref _lastFileWrite, new(DateTimeOffset.UtcNow, fileError == null, fileError, failures));
                }
            }

            if (_consoleAttached)
            {
                lock (_consoleLock)
                {
                    if (_consoleOutputFailureCount >= MaxConsoleOutputFailures || !CapturePolicy().AllowsBatch(policy))
                        return lastLineWritten && fileError == null;

                    try
                    {
                        if (fileError != null)
                        {
                            Console.WriteLine($"Failed to write to log file: {fileError}");
                        }

                        foreach (string line in lines)
                        {
                            if (!CapturePolicy().AllowsBatch(policy)) break;
                            Console.WriteLine(line);
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
            return lastLineWritten && fileError == null;
        }

        /// <summary>
        /// Serializes append, rotation and deletion across the UI and short-lived CLI processes.
        /// Each writer must hold the lock before opening the file and seeking to its end.
        /// </summary>
        private void ExecuteWithLogFileLock(Action operation)
        {
            using var mutex = new Mutex(initiallyOwned: false, _logFileMutexName);
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(AppConstants.Logging.FileLockTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    throw new IOException("Timed out waiting for the log file lock.");
                }

                operation();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
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

            entries.Add(DroppedMessageWarning(dropped, CapturePolicy()));
        }

        private static LogEntry DroppedMessageWarning(long dropped, PolicyStamp policy) =>
            new(DateTime.Now, LogLevel.Warning, "Logger", $"{dropped} log message(s) dropped due to queue overflow", null, null, policy);

        private string? AppendBoundedLines(string[] lines, PolicyStamp policy, out bool lastLineWritten)
        {
            lastLineWritten = false;
            string? error = null;
            long preambleBytes = Encoding.UTF8.Preamble.Length;
            long maxContentBytes = _maxLogFileBytes - preambleBytes;
            Span<long> sizes = stackalloc long[MaxBatchSize + 1];
            for (int index = 0; index < lines.Length; index++)
                sizes[index] = (long)Encoding.UTF8.GetByteCount(lines[index]) + Environment.NewLine.Length;

            var active = new FileInfo(_logFilePath);
            long length = active.Exists ? active.Length : 0;
            int start = 0;
            while (start < lines.Length && CapturePolicy().AllowsBatch(policy))
            {
                if (sizes[start] > maxContentBytes)
                {
                    error = "LogEntryTooLarge";
                    start++;
                    continue;
                }

                if (length > 0 && sizes[start] > _maxLogFileBytes - length)
                {
                    if (!TryRotateLog()) return "LogRotationUnavailable";
                    PruneOldLogBackups();
                    length = 0;
                }

                // Write complete records and account for the UTF-8 BOM on each new active file.
                long nextLength = length == 0 ? preambleBytes : length;
                int end = start;
                while (end < lines.Length && sizes[end] <= _maxLogFileBytes - nextLength)
                {
                    nextLength += sizes[end++];
                }

                if (!AppendLines(lines.AsSpan(start, end - start), policy)) break;
                lastLineWritten = end == lines.Length;
                length = nextLength;
                start = end;
            }
            return error;
        }

        private bool AppendLines(ReadOnlySpan<string> lines, PolicyStamp policy)
        {
            FileStream stream = OpenAppendStreamWithRetry();
            using (stream)
            {
                if (stream.Length == 0)
                {
                    try
                    {
                        File.SetCreationTimeUtc(stream.SafeFileHandle, _retentionTimeProvider.GetUtcNow().UtcDateTime);
                    }
                    catch (Exception ex)
                    {
                        EmitInternalDiagnostic("Logger creation timestamp update failed", ex);
                    }
                }
                if (!CapturePolicy().AllowsBatch(policy)) return false;
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                foreach (string line in lines) writer.WriteLine(line);
                return true;
            }
        }

        private FileStream OpenAppendStreamWithRetry()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                }
                catch (IOException ex) when (attempt < 2 && ex is not DirectoryNotFoundException and not DriveNotFoundException)
                {
                    WaitForRetryDelay(attempt);
                }
            }
        }

        private bool TryRotateLog()
        {
            if (_rotationFailureTimestamp is { } lastAttempt && _retentionTimeProvider.GetElapsedTime(lastAttempt) < TimeSpan.FromSeconds(1))
                return false;

            try
            {
                if (ArchiveActiveLogFile())
                {
                    _rotationFailureTimestamp = null;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (System.Diagnostics.Debugger.IsAttached)
                {
                    System.Diagnostics.Debug.WriteLine($"Logger rotation failed: {ex.GetType().Name}");
                }
            }
            _rotationFailureTimestamp = _retentionTimeProvider.GetTimestamp();
            return false;
        }

        /// <summary>
        /// Checks persisted file age at startup and on the first write of each UTC day.
        /// Creation time is reset when an empty active log is opened so filesystem timestamp
        /// reuse after rotation cannot make a new log appear old to another process.
        /// </summary>
        private void CheckLogRetention()
        {
            DateTime today = _retentionTimeProvider.GetUtcNow().UtcDateTime.Date;
            if (today == _lastRetentionCheckDateUtc)
            {
                return;
            }

            _lastRetentionCheckDateUtc = today;
            try
            {
                var activeLog = new FileInfo(_logFilePath);
                if (activeLog.Exists && activeLog.Length > 0 &&
                    (today - activeLog.CreationTimeUtc.Date).TotalDays >= LogResetIntervalDays)
                {
                    _ = TryRotateLog();
                }
            }
            catch (Exception ex)
            {
                EmitInternalDiagnostic("Logger age-based rotation failed", ex);
            }

            PruneOldLogBackups();
        }

        public void Log(LogLevel level, string category, string message, string? methodName = null, Exception? exception = null)
        {
            if (!TryBeginProducer(level, out PolicyStamp policy))
            {
                return;
            }

            try
            {
                LogCore(level, category, message, methodName, exception, policy);
            }
            finally
            {
                EndProducer();
            }
        }

        public void Log(LogLevel level, string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null)
        {
            if (!TryBeginProducer(level, out PolicyStamp policy))
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

                LogCore(level, category, message, methodName, exception, policy);
            }
            finally
            {
                EndProducer();
            }
        }

        private void LogCore(LogLevel level, string category, string message, string? methodName, Exception? exception, PolicyStamp policy)
        {
            if (!CapturePolicy().AllowsProducer(level, policy)) return;
            var timestamp = DateTime.Now;
            if (OperationTrace.CurrentId is { } traceId) message += $" | traceId={traceId}";
            string? exceptionDetails = exception != null ? FormatExceptionDetails(exception) : null;
            var logEntry = new LogEntry(timestamp, level, category, message, methodName, exceptionDetails, policy);

            if (_writeMode == LoggerWriteMode.InMemory)
            {
                lock (_fileLock)
                {
                    if (!CapturePolicy().AllowsProducer(level, policy)) return;
                    var logLine = FormatLogEntry(logEntry);
                    _inMemoryLogBuffer!.AppendLine(logLine);
                }

                return;
            }

            if (!CapturePolicy().AllowsProducer(level, policy)) return;
            if (!TryReserveQueueSlot(countFailure: level != LogLevel.Fatal))
            {
                if (level == LogLevel.Fatal)
                {
                    if (Interlocked.CompareExchange(ref _reservedFatal, new(logEntry), null) == null) SignalQueue();
                    else
                    {
                        Interlocked.Increment(ref _droppedMessageCount);
                        Interlocked.Increment(ref _totalDroppedMessageCount);
                    }
                }
                return;
            }

            _logQueue.Enqueue(logEntry);
            SignalQueue();
        }

        private bool TryReserveQueueSlot(bool countFailure = true)
        {
            int reservedCount = Interlocked.Increment(ref _queuedCount);
            if (reservedCount <= MaxQueueSize)
            {
                return true;
            }

            Interlocked.Decrement(ref _queuedCount);
            if (countFailure)
            {
                Interlocked.Increment(ref _droppedMessageCount);
                Interlocked.Increment(ref _totalDroppedMessageCount);
            }
            return false;
        }

        private static string FormatLogEntry(LogEntry entry)
        {
            var timestamp = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var method = string.IsNullOrEmpty(entry.MethodName) ? "" : $" [{entry.MethodName}]";
            var exceptionDetails = entry.ExceptionDetails ?? "";
            string message = entry.Message.AsSpan().IndexOfAny('\r', '\n') >= 0
                ? entry.Message.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
                : entry.Message;

            return $"[{timestamp}] [{entry.Level}] [{entry.Category}] {message}{method}{exceptionDetails}";
        }

        private static string FormatExceptionDetails(Exception exception)
        {
            const int maxExceptions = 8;
            var details = new StringBuilder();
            int exceptionCount = 0;
            bool truncated = false;

            void AppendBounded(string? value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                int available = MaxExceptionDetailsChars - details.Length;
                int length = Math.Min(value.Length, available);
                details.Append(value, 0, length);
                truncated |= length < value.Length;
            }

            void AppendException(Exception current)
            {
                if (exceptionCount >= maxExceptions || details.Length >= MaxExceptionDetailsChars)
                {
                    truncated = true;
                    return;
                }

                string label = exceptionCount++ == 0 ? "Exception" : "Inner exception";
                AppendBounded($"\n  {label}: {current.GetType().Name} (HRESULT=0x{current.HResult:X8}): ");
                AppendBounded(FormatExceptionMessageForLog(current.Message));
                AppendBounded("\n");
                AppendBounded(SanitizeExceptionDetails(current.StackTrace ?? string.Empty));

                if (current is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        AppendException(inner);
                        if (truncated)
                        {
                            break;
                        }
                    }
                }
                else if (current.InnerException is { } inner)
                {
                    AppendException(inner);
                }
            }

            AppendException(exception);
            if (truncated)
            {
                details.Append("\n  [truncated]");
            }

            return details.ToString();
        }

        private bool TryBeginProducer(LogLevel level, out PolicyStamp policy)
        {
            policy = CapturePolicy();
            return IsEnabled(level, policy.Level.Minimum) && TryBeginProducer();
        }

        private bool TryBeginProducer()
        {
            lock (_producerStateLock)
            {
                if (_disposed)
                {
                    return false;
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
                    _activeProducerCompletionSource = null;
                }
            }

            completionSource?.TrySetResult(true);
        }

        private Task GetActiveProducersCompletionTask()
        {
            lock (_producerStateLock)
            {
                return _activeProducerCount == 0
                    ? Task.CompletedTask
                    : (_activeProducerCompletionSource ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
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

        internal static string FormatExceptionMessageForLog(string message) =>
            LogPrivacy.IsRedactionEnabled ? LogPrivacy.RedactedLabel(message) : LogPrivacy.FormatRawValue(SanitizeExceptionMessage(message));

        public void ApplyLogLevel(Settings? settings)
        {
            if (!TryBeginProducer()) return;
            try
            {
                if (ApplyLogPolicy(settings)) DeleteLogFilesIfPresent();
            }
            finally { EndProducer(); }
        }

        /// <summary>Applies filtering immediately and performs file cleanup without blocking the caller.</summary>
        public Task ApplyLogLevelAsync(Settings? settings)
        {
            if (!TryBeginProducer()) return Task.CompletedTask;
            try
            {
                if (ApplyLogPolicy(settings))
                {
                    return Task.Run(() =>
                    {
                        try { DeleteLogFilesIfPresent(); }
                        finally { EndProducer(); }
                    });
                }
            }
            catch
            {
                EndProducer();
                throw;
            }

            EndProducer();
            return Task.CompletedTask;
        }

        internal static LogLevel ParseLogLevel(string? value) =>
            Enum.TryParse(value, ignoreCase: true, out LogLevel level) && Enum.IsDefined(level) ? level : LogLevel.Info;

        private bool ApplyLogPolicy(Settings? settings)
        {
            LogLevel level;
            bool changed;
            lock (_producerStateLock)
            {
                LogPrivacy.ApplySettings(settings);
                if (settings == null) return false;

                level = ParseLogLevel(settings.Diagnostics?.LogLevel);
                changed = _levelPolicy.Minimum != level;
                SetMinimumLevel(level);
                if (level == LogLevel.None)
                {
                    ClearQueuedMessages();
                    return true;
                }
            }

            if (changed) Info("Logger", $"Log level set to: {level}");
            return false;
        }

        private void ClearQueuedMessages()
        {
            Interlocked.Exchange(ref _reservedFatal, null);
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
                    if (MinimumLevel != LogLevel.None) return;
                    _inMemoryLogBuffer!.Clear();
                }

                return;
            }

            lock (_fileLock)
            {
                if (MinimumLevel != LogLevel.None) return;
                try
                {
                    ExecuteWithLogFileLock(() =>
                    {
                        if (MinimumLevel != LogLevel.None) return;
                        foreach (string path in new[] { _logFilePath }.Concat(EnumerateLogBackupCandidates()).Concat(EnumerateOrphanedRotationTempFiles()))
                        {
                            try
                            {
                                File.Delete(path);
                            }
                            catch (DirectoryNotFoundException)
                            {
                            }
                            catch (Exception ex)
                            {
                                EmitInternalDiagnostic("Failed to delete a log file after disabling logging", ex);
                            }
                        }
                    });
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
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return new(TryDrainAsync());
        }

        /// <summary>Stops accepting entries and reports whether cleanup finished within the shutdown budget.</summary>
        internal Task<bool> TryDrainAsync()
        {
            lock (_disposeSync)
            {
                if (_disposeTask != null) return _shutdownTask!.IsCompletedSuccessfully ? Task.FromResult(true) : _disposeTask;
                lock (_producerStateLock) _disposed = true;
                Task cleanup = Task.Run(DisposeCoreAsync);
                Volatile.Write(ref _shutdownTask, cleanup);
                return _disposeTask = WaitForDisposalAsync(cleanup);
            }
        }

        private async Task<bool> WaitForDisposalAsync(Task cleanup)
        {
            try
            {
                await cleanup.WaitAsync(TimeSpan.FromMilliseconds(_disposeWaitMs)).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                // The writer may be blocked on a diagnostic sink. Observe eventual faults without writing to that sink.
                _ = cleanup.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception) { }
            return false;
        }

        private async Task DisposeCoreAsync()
        {
            // A producer accepted before disposal may still enqueue its final message.
            await GetActiveProducersCompletionTask().ConfigureAwait(false);
            _cts.Cancel();
            _queueSignal.Release();
            try
            {
                await _logWriterTask.ConfigureAwait(false);
            }
            finally
            {
                try { DrainQueuedMessagesForShutdown(); }
                finally
                {
                    _cts.Dispose();
                    _queueSignal.Dispose();
                }
            }
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
            while (true)
            {
                var messages = DequeueBatch();
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

            if (Environment.GetEnvironmentVariable("AUDIOPILOT_DISABLE_CONSOLE_LOGGING") != "1")
            {
                try
                {
                    Console.WriteLine(payload);
                }
                catch
                {
                }
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
                : $"{message}: {exception.GetBaseException().GetType().Name} message={FormatExceptionMessageForLog(exception.GetBaseException().Message)}";
        }

        internal static string SanitizeExceptionDetails(string exceptionDetails)
        {
            if (string.IsNullOrEmpty(exceptionDetails) || !exceptionDetails.Contains(" in ", StringComparison.Ordinal))
            {
                return exceptionDetails;
            }

            return ExceptionPathRegex().Replace(exceptionDetails, "");
        }

        /// <summary>
        /// Acquires a writable handle before shifting backups so a blocked active log does not consume history.
        /// The same handle truncates the active log if a reader prevents renaming but permits writes.
        /// </summary>
        private bool ArchiveActiveLogFile()
        {
            if (!File.Exists(_logFilePath))
            {
                return false;
            }

            using var active = new FileStream(_logFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Directory.CreateDirectory(_logBackupDirectory);

            string backupPath = GetLogBackupPath();
            string stagedPath = Path.Combine(_logBackupDirectory, $"{_logFileName}.rotating.{Guid.NewGuid():N}.tmp");
            bool stagedByMove = false;
            bool staged = false;
            try
            {
                for (int attempt = 0; attempt < 3 && !staged; attempt++)
                {
                    try
                    {
                        File.Move(_logFilePath, stagedPath, overwrite: true);
                        stagedByMove = true;
                        staged = true;
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

                if (!staged && File.Exists(_logFilePath))
                {
                    File.Copy(_logFilePath, stagedPath, overwrite: true);
                    staged = true;
                }

                if (!staged)
                {
                    return false;
                }

                RotateLogBackups();
                File.Move(stagedPath, backupPath, overwrite: true);
                if (!stagedByMove)
                {
                    active.SetLength(0);
                }

                Interlocked.Increment(ref _rotationCount);
                return true;
            }
            catch
            {
                try
                {
                    if (File.Exists(stagedPath))
                    {
                        if (stagedByMove && !File.Exists(_logFilePath))
                        {
                            File.Move(stagedPath, _logFilePath);
                        }
                        else
                        {
                            File.Delete(stagedPath);
                        }
                    }
                }
                catch
                {
                }

                throw;
            }
        }

        private static void WaitForRetryDelay(int attempt)
        {
            Thread.Sleep(10 * (attempt + 1));
        }

        private void RotateLogBackups()
        {
            for (int index = _logBackupRetentionCount - 2; index >= 1; index--)
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

            DateTime cutoff = _retentionTimeProvider.GetUtcNow().UtcDateTime.AddDays(-LogBackupMaxAgeDays);
            foreach (string backupPath in EnumerateLogBackupCandidates())
            {
                if (!File.Exists(backupPath))
                {
                    continue;
                }

                try
                {
                    DateTime lastWrite = File.GetLastWriteTimeUtc(backupPath);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(backupPath);
                    }
                }
                catch (Exception ex)
                {
                    EmitInternalDiagnostic("Logger expired backup cleanup failed", ex);
                }
            }

            foreach (string orphanedPath in EnumerateOrphanedRotationTempFiles())
            {
                try
                {
                    File.Delete(orphanedPath);
                }
                catch (Exception ex)
                {
                    EmitInternalDiagnostic("Logger orphaned rotation temp cleanup failed", ex);
                }
            }
        }

        private string[] EnumerateOrphanedRotationTempFiles()
        {
            if (!Directory.Exists(_logBackupDirectory))
            {
                return [];
            }

            try
            {
                return Directory.GetFiles(_logBackupDirectory, _logFileName + ".rotating.*.tmp");
            }
            catch
            {
                return [];
            }
        }

        private IEnumerable<string> EnumerateLogBackupCandidates()
        {
            yield return GetLogBackupPath();

            for (int index = 1; index < _logBackupRetentionCount; index++)
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

            File.Move(source, destination, overwrite: true);
        }
    }
}
