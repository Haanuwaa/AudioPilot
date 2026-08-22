using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Logging;

public sealed class LoggerRetentionTests : IDisposable
{
    private readonly TestScopedDirectory _directory = new(nameof(LoggerRetentionTests));

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    public void Restart_RotatesByCreationDate_EvenWhenRecentlyWritten(int ageDays, bool shouldRotate)
    {
        DateTime now = DateTime.UtcNow;
        string logPath = Path.Combine(_directory.Root, AppConstants.Files.LogFileName);
        string backupPath = Path.Combine(_directory.Root, AppConstants.Files.BackupFolderName, AppConstants.Files.LogFileName + ".bak");
        File.WriteAllText(logPath, "previous session\n");
        File.SetCreationTimeUtc(logPath, now.AddDays(-ageDays));
        File.SetLastWriteTimeUtc(logPath, now);

        for (int session = 0; session < 3; session++)
        {
            using var logger = new Logger(_directory.Root);
            logger.Info("Retention", $"session-{session}");
        }

        string activeContent = File.ReadAllText(logPath);
        Assert.Equal(shouldRotate, File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + ".1"));
        for (int session = 0; session < 3; session++)
        {
            Assert.Contains($"session-{session}", activeContent);
        }

        if (shouldRotate)
        {
            Assert.Equal("previous session\n", File.ReadAllText(backupPath));
            Assert.DoesNotContain("previous session", activeContent);
            Assert.InRange(File.GetCreationTimeUtc(logPath), now, DateTime.UtcNow);
        }
        else
        {
            Assert.Contains("previous session", activeContent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupWithoutNewEntries_PrunesOnlyExpiredLogBackups(bool activeLogExists)
    {
        DateTime now = DateTime.UtcNow;
        string logPath = Path.Combine(_directory.Root, AppConstants.Files.LogFileName);
        if (activeLogExists)
        {
            File.WriteAllText(logPath, "recent log");
        }

        string backupDirectory = Path.Combine(_directory.Root, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        string expiredBackup = Path.Combine(backupDirectory, AppConstants.Files.LogFileName + ".bak");
        string recentBackup = expiredBackup + ".1";
        string secondExpiredBackup = expiredBackup + ".2";
        string settingsBackup = Path.Combine(backupDirectory, "settings.json.bak");
        string unrelatedLog = Path.Combine(backupDirectory, "other.log.bak");
        foreach (string path in new[] { expiredBackup, secondExpiredBackup, settingsBackup, unrelatedLog })
        {
            File.WriteAllText(path, "old backup");
            File.SetLastWriteTimeUtc(path, now.AddDays(-22));
        }

        File.WriteAllText(recentBackup, "recent backup");
        File.SetLastWriteTimeUtc(recentBackup, now.AddDays(-20));

        new Logger(_directory.Root).Dispose();

        Assert.False(File.Exists(expiredBackup));
        Assert.False(File.Exists(secondExpiredBackup));
        Assert.Equal("recent backup", File.ReadAllText(recentBackup));
        Assert.Equal("old backup", File.ReadAllText(settingsBackup));
        Assert.Equal("old backup", File.ReadAllText(unrelatedLog));
        Assert.Equal(activeLogExists, File.Exists(logPath));
        if (activeLogExists)
        {
            Assert.Equal("recent log", File.ReadAllText(logPath));
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task NewUtcDay_PrunesExpiredBackups_AndRotatesOnlyAnOldLog(int elapsedDays, bool shouldRotate)
    {
        DateTime now = DateTime.UtcNow;
        var clock = new RetentionTimeProvider(now);
        string logPath = Path.Combine(_directory.Root, AppConstants.Files.LogFileName);
        string backupDirectory = Path.Combine(_directory.Root, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, AppConstants.Files.LogFileName + ".bak");
        File.WriteAllText(backupPath, "expiring backup");
        File.SetLastWriteTimeUtc(backupPath, now.AddDays(-20));

        await using var logger = new Logger(_directory.Root, AppConstants.Files.LogFileName, Logger.LoggerWriteMode.FileBacked,
            retentionTimeProvider: clock);
        logger.Info("Retention", "first day");
        await TestExecutionGuards.WaitUntilAsync(
            () => ContainsEntry(logPath, "first day"),
            "The first day's log entry was not written.");
        Assert.True(File.Exists(backupPath));

        clock.Advance(TimeSpan.FromDays(elapsedDays));
        logger.Info("Retention", "later day");
        await logger.DisposeAsync();

        string content = File.ReadAllText(logPath);
        Assert.Contains("later day", content);
        Assert.Equal(shouldRotate ? 1 : 0, logger.RotationCount);
        if (shouldRotate)
        {
            Assert.DoesNotContain("first day", content);
            Assert.Contains("first day", File.ReadAllText(backupPath));
            Assert.False(File.Exists(backupPath + ".1"));
        }
        else
        {
            Assert.Contains("first day", content);
            Assert.False(File.Exists(backupPath));
        }
    }

    [Fact]
    public void LockedExpiredBackup_DoesNotBlockOtherCleanupOrLogging()
    {
        string backupDirectory = Path.Combine(_directory.Root, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        string lockedPath = Path.Combine(backupDirectory, AppConstants.Files.LogFileName + ".bak");
        string otherPath = lockedPath + ".1";
        foreach (string path in new[] { lockedPath, otherPath })
        {
            File.WriteAllText(path, "expired");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-22));
        }

        using var heldBackup = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var logger = new Logger(_directory.Root))
        {
            logger.Info("Retention", "logging survives");
        }

        Assert.True(File.Exists(lockedPath));
        Assert.False(File.Exists(otherPath));
        Assert.Contains("logging survives", File.ReadAllText(Path.Combine(_directory.Root, AppConstants.Files.LogFileName)));
    }

    private static bool ContainsEntry(string path, string entry)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Contains(entry, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed class RetentionTimeProvider(DateTime utcNow) : TimeProvider
    {
        private long _ticks = utcNow.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    public void Dispose() => _directory.Dispose();
}
