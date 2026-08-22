using System.Runtime.InteropServices;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceEndpointQueryHelperTests
{
    [Theory]
    [InlineData(true, unchecked((int)0x80070490), AudioEndpointStatusKind.NoDevice)]
    [InlineData(false, unchecked((int)0x80070490), AudioEndpointStatusKind.NoDevice)]
    [InlineData(true, unchecked((int)0x88890004), AudioEndpointStatusKind.Unavailable)]
    [InlineData(false, unchecked((int)0x88890004), AudioEndpointStatusKind.Unavailable)]
    public void CaptureAudioStatus_EndpointFailurePreservesOtherEndpoint(bool failOutput, int hresult, AudioEndpointStatusKind expectedKind)
    {
        var available = new AudioEndpointStatus(AudioEndpointStatusKind.Available, "Fixture endpoint", 42, true);
        var failures = new List<string>();
        AudioEndpointStatus ReadFailure() => throw new COMException("Fixture failure", hresult);

        AudioStatusSnapshot snapshot = AudioDeviceEndpointQueryHelper.CaptureAudioStatus(
            failOutput ? ReadFailure : () => available,
            failOutput ? () => available : ReadFailure,
            (target, _) => failures.Add(target));

        AudioEndpointStatus failed = failOutput ? snapshot.Output : snapshot.Input;
        Assert.Same(available, failOutput ? snapshot.Input : snapshot.Output);
        Assert.Equal(expectedKind, failed.Kind);
        Assert.Null(failed.VolumePercent);
        Assert.Null(failed.Muted);
        if (expectedKind == AudioEndpointStatusKind.Unavailable)
        {
            Assert.Equal(failOutput ? "output" : "input", Assert.Single(failures));
        }
        else
        {
            Assert.Empty(failures);
        }
    }

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
