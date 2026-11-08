using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceSessionProcessResolverTests
{
    [Fact]
    public void TryResolveProcessName_UsesFreshCacheWhenProcessCannotBeOpened()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceSessionProcessResolverTests), "session-process-resolver.log");
        var resolver = new AudioDeviceSessionProcessResolver(
            loggerScope.Logger,
            _ => ("discord", null, null, DateTime.UtcNow.Ticks),
            static _ => false);

        bool success = resolver.TryResolveProcessName(42, out string processName);

        Assert.True(success);
        Assert.Equal("discord", processName);
    }

    [Fact]
    public void TryResolveProcessName_PrefersExecutableNameOverFriendlyCachedLabel()
    {
        using var loggerScope = TestLoggerScope.CreateInMemory("process-identity.log");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var resolver = new AudioDeviceSessionProcessResolver(loggerScope.Logger,
            _ => ("Friendly app label", null, null, 0), _ => false);
        Assert.True(resolver.TryResolveProcessName((uint)process.Id, out string name));
        Assert.Equal(process.ProcessName, name);
    }

    [Fact]
    public void TryResolveProcessName_ReturnsFalse_WhenPidIsZero()
    {
        using var loggerScope = new TestLoggerScope(nameof(AudioDeviceSessionProcessResolverTests), "session-process-resolver-zero.log");
        var resolver = new AudioDeviceSessionProcessResolver(
            loggerScope.Logger,
            static _ => null,
            static _ => true);

        bool success = resolver.TryResolveProcessName(0, out string processName);

        Assert.False(success);
        Assert.Equal(string.Empty, processName);
    }
}
