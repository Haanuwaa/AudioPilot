using AudioPilot.Logging;

namespace AudioPilot.Tests.Helpers;

public sealed class TestLoggerScopeTests
{
    [Fact]
    public void DefaultScope_UsesInMemoryLoggerWithoutFilesystemRoot()
    {
        using var scope = new TestLoggerScope(nameof(TestLoggerScopeTests), "in-memory.log", LogLevel.Trace);

        scope.Logger.Info("Test", "hello");

        string logText = scope.DisposeAndReadLogText();

        Assert.Equal(string.Empty, scope.Root);
        Assert.Equal(string.Empty, scope.LogPath);
        Assert.Contains("[Info] [Test] hello", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void FileBackedScope_WritesLogFileForFilesystemAssertions()
    {
        using var scope = TestLoggerScope.CreateFileBacked(nameof(TestLoggerScopeTests), "file-backed.log", LogLevel.Trace);

        scope.Logger.Warning("Test", "file-backed-warning");
        scope.Logger.Dispose();

        string logText = TestLogFileAssert.WaitForLogText(scope.LogPath, "file-backed-warning");

        Assert.NotEqual(string.Empty, scope.Root);
        Assert.NotEqual(string.Empty, scope.LogPath);
        Assert.Contains("[Warning] [Test] file-backed-warning", logText, StringComparison.Ordinal);
    }
}
