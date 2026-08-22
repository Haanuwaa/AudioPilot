using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceDefaultEndpointCollectionHelperTests
{
    [Fact]
    public void GetDistinctDefaultDevicesForRoles_DeduplicatesSharedDeviceIds()
    {
        var devices = new Dictionary<Role, FakeDevice>
        {
            [Role.Console] = new("shared"),
            [Role.Multimedia] = new("shared"),
            [Role.Communications] = new("comm"),
        };

        int disposedDuplicates = 0;
        List<FakeDevice?> result = AudioDeviceDefaultEndpointCollectionHelper.GetDistinctDefaultDevicesForRoles(
            role => devices[role],
            static device => device.Id,
            _ => disposedDuplicates++,
            static _ => { },
            static (_, _, _) => { });

        Assert.Equal(3, result.Count);
        Assert.Same(result[0], result[1]);
        Assert.Equal("comm", result[2]!.Id);
        Assert.Equal(1, disposedDuplicates);
    }

    [Fact]
    public void GetDistinctDefaultDevicesForRoles_AddsNull_WhenRoleIsMissing()
    {
        List<Role> missingRoles = [];

        List<FakeDevice?> result = AudioDeviceDefaultEndpointCollectionHelper.GetDistinctDefaultDevicesForRoles(
            role => role == Role.Multimedia
                ? throw new System.Runtime.InteropServices.COMException("missing", unchecked((int)0x80070490))
                : new FakeDevice(role.ToString()),
            static device => device.Id,
            static _ => { },
            role => missingRoles.Add(role),
            static (_, _, _) => { });

        Assert.Null(result[1]);
        Assert.Equal([Role.Multimedia], missingRoles);
    }

    private sealed class FakeDevice(string id)
    {
        public string Id { get; } = id;
    }
}
