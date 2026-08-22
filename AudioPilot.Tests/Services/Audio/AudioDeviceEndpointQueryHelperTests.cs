using AudioPilot.Logging;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceEndpointQueryHelperTests
{
    [Fact]
    public void TryGetDeviceById_ReturnsNull_ForBlankId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var lockSlim = new ReaderWriterLockSlim();
        var helper = new AudioDeviceEndpointQueryHelper(
            enumerator,
            lockSlim,
            Logger.Instance,
            () => false,
            () => [NRole.Multimedia],
            () => [NRole.Console],
            (_, _) => { });

        MMDevice? device = helper.TryGetDeviceById(string.Empty);

        Assert.Null(device);
    }

    [Fact]
    public void GetAllDefaultPlaybackDevices_ReturnsEmpty_WhenDisposed()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var lockSlim = new ReaderWriterLockSlim();
        var helper = new AudioDeviceEndpointQueryHelper(
            enumerator,
            lockSlim,
            Logger.Instance,
            () => true,
            () => [NRole.Multimedia],
            () => [NRole.Console],
            (_, _) => { });

        List<MMDevice?> devices = helper.GetAllDefaultPlaybackDevices();

        Assert.Empty(devices);
    }
}
