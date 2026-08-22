namespace AudioPilot.Tests.Helpers;

internal static class TestLogFileAssert
{
    private const int PollIntervalMs = 50;

    private static string ReadLogText(string logPath)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    internal static string ReadAvailableLogText(string logPath)
    {
        return ReadLogText(logPath);
    }

    internal static bool TrySignal(EventWaitHandle waitHandle)
    {
        try
        {
            return waitHandle.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static string WaitForLogTextCore(string logPath, Func<string, bool> predicate, int timeoutMs, Action? onWaitingStarted = null)
    {
        string logText;
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        using var waitHandle = new AutoResetEvent(false);
        using var watcher = CreateWatcher(logPath, waitHandle);
        onWaitingStarted?.Invoke();

        while (true)
        {
            logText = ReadLogText(logPath);
            if (predicate(logText))
            {
                return logText;
            }

            int remainingMs = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remainingMs == 0)
            {
                return logText;
            }

            int waitMs = Math.Min(remainingMs, PollIntervalMs);

            waitHandle.WaitOne(waitMs);
        }
    }

    private static FileSystemWatcher? CreateWatcher(string logPath, AutoResetEvent waitHandle)
    {
        string? directory = Path.GetDirectoryName(logPath);
        string fileName = Path.GetFileName(logPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(fileName) ||
            !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            void OnChanged(object? _, FileSystemEventArgs __) => TrySignal(waitHandle);

            void OnRenamed(object? _, RenamedEventArgs __) => TrySignal(waitHandle);

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;

            return watcher;
        }
        catch
        {
            return null;
        }
    }

    internal static string WaitForLogTextAfterWaitStartsForTests(string logPath, string requiredFragment, Action onWaitingStarted, int timeoutMs = 2000)
    {
        return WaitForLogTextCore(
            logPath,
            logText => logText.Contains(requiredFragment, StringComparison.Ordinal),
            timeoutMs,
            onWaitingStarted);
    }

    internal static string WaitForLogTextAfterWaitStartsForTests(string logPath, Action onWaitingStarted, params string[] requiredFragments)
    {
        return WaitForLogTextAfterWaitStartsForTests(logPath, onWaitingStarted, timeoutMs: 2000, requiredFragments);
    }

    internal static string WaitForLogTextAfterWaitStartsForTests(string logPath, Action onWaitingStarted, int timeoutMs, params string[] requiredFragments)
    {
        return WaitForLogTextCore(
            logPath,
            logText => requiredFragments.All(fragment => logText.Contains(fragment, StringComparison.Ordinal)),
            timeoutMs,
            onWaitingStarted);
    }

    internal static string WaitForLogText(string logPath, string requiredFragment, int timeoutMs = 2000)
    {
        return WaitForLogTextCore(
            logPath,
            logText => logText.Contains(requiredFragment, StringComparison.Ordinal),
            timeoutMs);
    }

    internal static string WaitForLogText(string logPath, int timeoutMs = 2000, params string[] requiredFragments)
    {
        return WaitForLogTextCore(
            logPath,
            logText => requiredFragments.All(fragment => logText.Contains(fragment, StringComparison.Ordinal)),
            timeoutMs);
    }
}
