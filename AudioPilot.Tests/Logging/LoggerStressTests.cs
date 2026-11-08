using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

[Trait(TestCategories.Name, TestCategories.Stress)]
public sealed class LoggerStressTests : IDisposable
{
    private readonly TestScopedDirectory _directory;

    public LoggerStressTests()
    {
        _directory = new TestScopedDirectory(nameof(LoggerStressTests));
    }

    [StressFact]
    public void Logger_HandlesBurstLogging_WithoutThrowing()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(Logger_HandlesBurstLogging_WithoutThrowing)))
        {
            return;
        }

        string fileName = "burst.log";
        string path = Path.Combine(_directory.Root, fileName);
        using var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        Parallel.For(0, 20000, i =>
        {
            logger.Trace("Stress", $"message-{i}");
        });

        logger.Dispose();

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }

    [StressFact]
    public void Logger_ConcurrentInstances_PreserveEveryCompleteEntry()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(Logger_ConcurrentInstances_PreserveEveryCompleteEntry)))
        {
            return;
        }

        const int writerCount = 16;
        const int entriesPerWriter = 128;
        const string fileName = "shared.log";
        string payload = new('X', 512);
        Logger[] loggers = [.. Enumerable.Range(0, writerCount)
            .Select(_ => new Logger(_directory.Root, fileName))];
        try
        {
            Parallel.For(0, writerCount, writer =>
            {
                for (int entry = 0; entry < entriesPerWriter; entry++)
                {
                    loggers[writer].Info("Stress", $"writer-{writer}-entry-{entry} {payload}");
                }

                loggers[writer].Dispose();
            });
        }
        finally
        {
            foreach (Logger logger in loggers)
            {
                logger.Dispose();
            }
        }

        string[] lines = File.ReadAllLines(Path.Combine(_directory.Root, fileName));
        Assert.Equal(writerCount * entriesPerWriter, lines.Length);
        var expected = Enumerable.Range(0, writerCount)
            .SelectMany(writer => Enumerable.Range(0, entriesPerWriter)
                .Select(entry => $"writer-{writer}-entry-{entry} {payload}"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string[] parts = line.Split("[Stress] ", 2, StringSplitOptions.None);
            Assert.Equal(2, parts.Length);
            Assert.True(expected.Remove(parts[1]), "Log entry was duplicated or corrupted.");
        }

        Assert.Empty(expected);
    }

    [StressFact]
    public void Logger_RotatesLogFile_WhenMaxSizeExceeded()
    {
        if (!TestExecutionGuards.RequireStressEnabled(nameof(Logger_RotatesLogFile_WhenMaxSizeExceeded)))
        {
            return;
        }

        string fileName = "rotate.log";
        string path = Path.Combine(_directory.Root, fileName);
        string archivedPath = Path.Combine(_directory.Root, AppConstants.Files.BackupFolderName, fileName + ".bak");

        using var logger = new Logger(_directory.Root, fileName)
        {
            MinimumLevel = LogLevel.Trace
        };

        string payload = new('X', 64 * 1024);
        for (int i = 0; i < 120; i++)
        {
            logger.Trace("Stress", payload);
        }

        logger.Dispose();

        _ = SpinWait.SpinUntil(
            () => File.Exists(archivedPath) && new FileInfo(archivedPath).Length > 0,
            TimeSpan.FromSeconds(2));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(archivedPath));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.True(new FileInfo(archivedPath).Length > 0);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }
}

