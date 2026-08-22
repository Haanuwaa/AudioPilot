namespace AudioPilot.Tests.Services.UI;

public sealed class WindowsAudioSettingsTests
{
    [Fact]
    public void DeviceLink_EscapesOpaqueIdsAndFallsBackForOlderWindows()
    {
        const string endpoint = "{device}&other=value#fragment?\" name";
        string uri = WindowsAudioSettings.GetDeviceUri(endpoint, true);
        Assert.Equal("ms-settings:sound-properties?endpointId=" + Uri.EscapeDataString(endpoint), uri);
        Assert.DoesNotContain("&other=", uri, StringComparison.Ordinal);
        Assert.Equal("ms-settings:sound", WindowsAudioSettings.GetDeviceUri(endpoint, false));
        Assert.Equal("ms-settings:sound", WindowsAudioSettings.GetDeviceUri(string.Empty, true));
        Assert.Equal("ms-settings:sound", WindowsAudioSettings.GetDeviceUri(new string('x', 4097), true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsLaunch_FallsBackWhenLaunchIsRejectedOrThrows(bool throws)
    {
        var calls = new List<string>();
        Assert.True(WindowsAudioSettings.OpenWithFallback("ms-settings:apps-volume", target =>
        {
            calls.Add(target);
            if (target == "ms-settings:sound") return true;
            if (throws) throw new InvalidOperationException();
            return false;
        }));
        Assert.Equal(["ms-settings:apps-volume", "ms-settings:sound"], calls);
    }

    [Fact]
    public void SettingsLaunch_DoesNotRetryTheSameFallbackOrHideTotalFailure()
    {
        int calls = 0;
        Assert.False(WindowsAudioSettings.OpenWithFallback("ms-settings:sound", _ => { calls++; return false; }));
        Assert.Equal(1, calls);
    }
}
