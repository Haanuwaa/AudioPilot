using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceSessionControlRestoreHelperTests
{
    [Fact]
    public void TryRestoreSession_SkipsExpiredSessions()
    {
        bool attempted = false;

        bool restored = AudioDeviceSessionControlRestoreHelper.TryRestoreSession(
            AudioSessionState.AudioSessionStateExpired,
            42,
            "Spotify",
            (_, _) =>
            {
                attempted = true;
                return true;
            });

        Assert.False(restored);
        Assert.False(attempted);
    }

    [Fact]
    public void TryRestoreSession_SkipsZeroPidSessions()
    {
        bool attempted = false;

        bool restored = AudioDeviceSessionControlRestoreHelper.TryRestoreSession(
            AudioSessionState.AudioSessionStateActive,
            0,
            "Spotify",
            (_, _) =>
            {
                attempted = true;
                return true;
            });

        Assert.False(restored);
        Assert.False(attempted);
    }

    [Fact]
    public void TryRestoreSession_InvokesRestoreForValidSession()
    {
        uint? appliedPid = null;
        string? appliedDisplayName = null;

        bool restored = AudioDeviceSessionControlRestoreHelper.TryRestoreSession(
            AudioSessionState.AudioSessionStateActive,
            42,
            "Spotify",
            (pid, displayName) =>
            {
                appliedPid = pid;
                appliedDisplayName = displayName;
                return true;
            });

        Assert.True(restored);
        Assert.Equal((uint)42, appliedPid);
        Assert.Equal("Spotify", appliedDisplayName);
    }

    [Fact]
    public void TryRestoreSession_ReturnsFalse_WhenRestoreCallbackReturnsFalse()
    {
        bool restored = AudioDeviceSessionControlRestoreHelper.TryRestoreSession(
            AudioSessionState.AudioSessionStateActive,
            42,
            "Spotify",
            static (_, _) => false);

        Assert.False(restored);
    }
}
