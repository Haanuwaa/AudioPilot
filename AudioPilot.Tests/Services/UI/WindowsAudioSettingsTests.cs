namespace AudioPilot.Tests.Services.UI;

public sealed class WindowsAudioSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyTerminalLaunchFailureWarnsAndPrivateParametersAreOmitted(bool fallbackSucceeds)
    {
        using var scope = AudioPilot.Tests.Helpers.TestLoggerScope.CreateInMemory("settings-open.log");
        int calls = 0;
        bool opened = WindowsAudioSettings.OpenWithFallback("ms-settings:sound-properties?endpointId=private-endpoint", uri =>
        {
            calls++;
            if (uri == "ms-settings:sound") return fallbackSucceeds;
            throw new System.ComponentModel.Win32Exception(1155, "private-launch-error");
        }, logger: scope.Logger);

        string text = scope.DisposeAndReadLogText();
        Assert.Equal(2, calls);
        Assert.Equal(fallbackSucceeds, opened);
        Assert.Equal(fallbackSucceeds ? 0 : 1, text.Split('\n').Count(line => line.Contains("[Warning]", StringComparison.Ordinal)));
        if (!fallbackSucceeds)
        {
            Assert.Contains("page=DeviceProperties", text);
            Assert.Contains("nativeError=1155", text);
        }
        Assert.DoesNotContain("private-endpoint", text);
        Assert.DoesNotContain("private-launch-error", text);
    }

    [Fact]
    public void BluetoothLink_UsesDevicesFallback()
    {
        Assert.Equal("ms-settings:bluetooth", WindowsAudioSettings.GetUri(WindowsAudioSettingsPage.Bluetooth));
        var calls = new List<string>();
        Assert.True(WindowsAudioSettings.OpenWithFallback(WindowsAudioSettings.GetUri(WindowsAudioSettingsPage.Bluetooth),
            uri => { calls.Add(uri); return uri == "ms-settings:devices"; }, "ms-settings:devices"));
        Assert.Equal(["ms-settings:bluetooth", "ms-settings:devices"], calls);
    }

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
