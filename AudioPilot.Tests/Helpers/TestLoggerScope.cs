using AudioPilot.Logging;

namespace AudioPilot.Tests.Helpers;

internal sealed class TestLoggerScope : IDisposable
{
    private readonly TestScopedDirectory? _directory;

    public string Root => _directory?.Root ?? string.Empty;
    public string LogFileName { get; }
    public string LogPath { get; }
    public Logger Logger { get; }

    public TestLoggerScope(string scopeName, string logFileName, LogLevel minimumLevel = LogLevel.Trace)
        : this(scopeName, logFileName, minimumLevel, fileBacked: false)
    {
    }

    private TestLoggerScope(string scopeName, string logFileName, LogLevel minimumLevel, bool fileBacked)
    {
        LogFileName = logFileName;

        if (fileBacked)
        {
            _directory = new TestScopedDirectory(scopeName);
            LogPath = Path.Combine(_directory.Root, logFileName);
            Logger = new Logger(_directory.Root, logFileName);
        }
        else
        {
            LogPath = string.Empty;
            Logger = Logger.CreateInMemoryForTests(logFileName);
        }

        Logger.MinimumLevel = minimumLevel;
    }

    public static TestLoggerScope CreateFileBacked(string scopeName, string logFileName, LogLevel minimumLevel = LogLevel.Trace)
        => new(scopeName, logFileName, minimumLevel, fileBacked: true);

    public static TestLoggerScope CreateInMemory(string logFileName, LogLevel minimumLevel = LogLevel.Trace)
        => new("in-memory", logFileName, minimumLevel, fileBacked: false);

    public void Dispose()
    {
        Logger.Dispose();
        _directory?.Dispose();
    }

    public string DisposeAndReadLogText()
    {
        string logText = Logger.DisposeAndReadLogTextForTests();
        _directory?.Dispose();
        return logText;
    }
}
