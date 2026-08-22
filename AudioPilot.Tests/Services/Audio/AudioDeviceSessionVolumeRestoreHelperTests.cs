namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceSessionVolumeRestoreHelperTests
{
    [Fact]
    public void TryApplySavedVolume_Applies_WhenProcessNameIsResolved()
    {
        var resolver = new StubAudioDeviceSessionProcessResolver(true, "spotify");
        var helper = new AudioDeviceSessionVolumeRestoreHelper(resolver);
        string? appliedProcessName = null;
        string? appliedDisplayName = null;

        bool success = helper.TryApplySavedVolume(42, "Spotify", (processName, displayName) =>
        {
            appliedProcessName = processName;
            appliedDisplayName = displayName;
        });

        Assert.True(success);
        Assert.Equal("spotify", appliedProcessName);
        Assert.Equal("Spotify", appliedDisplayName);
    }

    [Fact]
    public void TryApplySavedVolume_Skips_WhenProcessNameCannotBeResolved()
    {
        var resolver = new StubAudioDeviceSessionProcessResolver(false, string.Empty);
        var helper = new AudioDeviceSessionVolumeRestoreHelper(resolver);
        bool applied = false;

        bool success = helper.TryApplySavedVolume(42, "Spotify", (_, _) => applied = true);

        Assert.False(success);
        Assert.False(applied);
    }

    [Fact]
    public void TryApplySavedVolume_UsesProvidedDisplayName_WhenApplying()
    {
        var resolver = new StubAudioDeviceSessionProcessResolver(true, "spotify");
        var helper = new AudioDeviceSessionVolumeRestoreHelper(resolver);
        string? appliedDisplayName = null;

        bool success = helper.TryApplySavedVolume(42, "Spotify Premium", (_, displayName) => appliedDisplayName = displayName);

        Assert.True(success);
        Assert.Equal("Spotify Premium", appliedDisplayName);
    }

    private sealed class StubAudioDeviceSessionProcessResolver(bool success, string processName)
        : AudioDeviceSessionProcessResolver(null!, null!, null!)
    {
        private readonly bool _success = success;
        private readonly string _processName = processName;

        public override bool TryResolveProcessName(uint pid, out string processName)
        {
            processName = _processName;
            return _success;
        }
    }
}
