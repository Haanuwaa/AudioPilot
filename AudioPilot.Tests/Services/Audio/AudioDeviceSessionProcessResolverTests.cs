using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceSessionProcessResolverTests
{
    [Fact]
    public void TryResolveProcessName_UsesCachedEntry_WhenFresh()
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
