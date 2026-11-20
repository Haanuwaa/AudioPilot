using AudioPilot.Localization;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests;

public sealed class AudioSessionEndpointLabelTests
{
    [Fact]
    public void EndpointRefreshChangesIdentityAndRoleLabels_WithoutEditingAudio()
    {
        var item = new AudioSessionItem("Master Volume", 50, isMaster: true, isMic: false, isMuted: true, endpointId: "old",
            endpointName: "Speakers", endpointRole: Role.Multimedia);
        var changes = new List<string?>();
        item.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        item.VolumeChanged += _ => Assert.Fail("Metadata refresh must not write volume.");
        item.MuteChanged += _ => Assert.Fail("Metadata refresh must not write mute.");
        item.UpdateRoutingMetadataFromSystem(null, "", "new", endpointName: "Headphones", endpointRole: Role.Communications);
        Assert.Equal("Headphones", item.EndpointLabel);
        Assert.Contains(nameof(item.EndpointDescription), changes);
        Assert.Equal("Mixer_RoleDevice", Assert.IsType<LocalizedText>(item.EndpointDescription).Key);
        Assert.Equal(0, item.VolumeEditRevision);
        Assert.Equal(0, item.MuteEditRevision);
        Assert.True(item.IsMuted);
        Assert.Equal(50, item.Volume);
        item.UpdateRoutingMetadataFromSystem(null, "", "unknown");
        Assert.Equal("Mixer_DeviceUnavailable", Assert.IsType<LocalizedText>(item.EndpointLabel).Key);
        Assert.DoesNotContain("old", item.EndpointLabel.ToString(), StringComparison.Ordinal);
    }
}
