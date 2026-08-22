using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioEndpointTestPreferencesTests
{
    [Fact]
    public void Preferences_RetainSessionChoicesAcrossRepeatedTestInitialization()
    {
        var preferences = new AudioEndpointTestPreferences
        {
            HearMyself = true,
            MonitorVolumePercent = 67d,
        };
        preferences.RememberMonitorOutput("preferred-output");

        string first = preferences.GetPreferredMonitorOutputId("configured-output");
        string second = preferences.GetPreferredMonitorOutputId("changed-configured-output");

        Assert.True(preferences.HearMyself);
        Assert.Equal(67d, preferences.MonitorVolumePercent);
        Assert.Equal("preferred-output", first);
        Assert.Equal("preferred-output", second);
    }

    [Fact]
    public void Preferences_UseConfiguredMonitorOnlyBeforeTheFirstUserChoice()
    {
        var preferences = new AudioEndpointTestPreferences();

        Assert.Equal("configured-output", preferences.GetPreferredMonitorOutputId("configured-output"));

        preferences.RememberMonitorOutput(null);

        Assert.Equal(string.Empty, preferences.GetPreferredMonitorOutputId("configured-output"));
    }

    [Fact]
    public void ResolveAvailableMonitorOutput_FallsBackWithoutForgettingPreference_ThenRestoresIt()
    {
        var preferences = new AudioEndpointTestPreferences();
        preferences.RememberMonitorOutput("preferred-output");

        string missingSelection = preferences.ResolveAvailableMonitorOutputId(
            ["another-output"],
            configuredMonitorOutputId: null,
            out bool usedFallback);
        string restoredSelection = preferences.ResolveAvailableMonitorOutputId(
            ["another-output", "PREFERRED-OUTPUT"],
            configuredMonitorOutputId: null,
            out bool usedFallbackAfterReappearance);

        Assert.Equal(string.Empty, missingSelection);
        Assert.True(usedFallback);
        Assert.Equal("preferred-output", restoredSelection);
        Assert.False(usedFallbackAfterReappearance);
    }
}
