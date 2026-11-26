using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Cli;

[Collection("RuntimeTuningConfigIsolation")]
public sealed class AdvancedTuningTests : IDisposable
{
    private readonly IReadOnlyList<CliKeyMetadata> _original = CliRuntimeManager.GetKeyMetadata();

    public void Dispose()
    {
        foreach (var entry in _original)
            Assert.True(CliRuntimeManager.TrySet(entry.Key, entry.CurrentValue!, out _));
    }

    [Fact]
    public void ProcessFallbackInterval_IsRuntimeOnlyAndReportsBuiltInDefault()
    {
        const string key = "process-monitor-fallback-interval-ms";
        Assert.True(CliRuntimeManager.TrySet(key, "500", out _));
        var metadata = Assert.Single(CliRuntimeManager.GetKeyMetadata(), entry => entry.Key == key);
        Assert.Equal("runtime-only", metadata.Scope);
        Assert.Equal("2000", metadata.DefaultValue);
        Assert.Equal("500", metadata.CurrentValue);
        Assert.DoesNotContain(key, CliConfigManager.GetKnownKeys());
    }

    [Fact]
    public void Headless_LoadsSavedTuningWithoutCreatingAudio_AndReloadsChangedValues()
    {
        using var workspace = new TestSettingsWorkspace(nameof(AdvancedTuningTests));
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        var settings = new Settings();
        Dictionary<string, string> values = new()
        {
            ["auto-save-debounce-ms"] = "1250",
            ["output-switch-debounce-ms"] = "200",
            ["input-switch-debounce-ms"] = "250",
            ["bluetooth-reconnect-max-attempts"] = "3",
            ["bluetooth-reconnect-attempt-timeout-ms"] = "1800",
            ["bluetooth-reconnect-cooldown-ms"] = "6000",
            ["bluetooth-reconnect-only-likely"] = "false",
            ["bluetooth-reconnect-success-stabilize-window-ms"] = "25000",
            ["bluetooth-reconnect-cached-endpoint-probe-attempts"] = "5",
            ["bluetooth-reconnect-cached-endpoint-probe-delay-ms"] = "200",
            ["steam-big-picture-monitor-debounce-ms"] = "300",
            ["steam-big-picture-confirmation-delay-ms"] = "900",
        };
        foreach (var (key, value) in values)
            Assert.True(CliConfigManager.TrySet(settings, key, value, out _));
        settingsService.SaveSettings(settings);
        using var runner = new LocalHeadlessCommandRunner(new LocalHeadlessCommandRunner.RuntimeServiceFactories(
            CreateSettingsService: () => settingsService,
            CreateAudioService: () => throw new InvalidOperationException("Tuning must not create audio services."),
            CreateBluetoothReconnectCoordinator: () => throw new InvalidOperationException("Tuning must not create reconnect services.")));

        foreach (var (key, value) in values)
            Assert.Equal(value, runner.GetRuntime(key).Value);
        Assert.Equal(3, BluetoothReconnectOptions.FromSettings(settings).MaxAttempts);
        Assert.True(runner.SetConfig("bluetooth-reconnect-success-stabilize-window-ms", "30000").Updated);
        Assert.Equal("30000", runner.GetRuntime("bluetooth-reconnect-success-stabilize-window-ms").Value);
        foreach (var key in new[] { "auto-save-debounce-ms", "output-switch-debounce-ms", "input-switch-debounce-ms" })
        {
            Assert.True(runner.SetConfig(key, "500").Updated);
            Assert.Equal("500", runner.GetRuntime(key).Value);
        }
    }

    [Fact]
    public void PersistedReload_PreservesOverridesUntilThatSavedValueChanges()
    {
        var state = new AdvancedTuningState();
        var settings = new AdvancedTuningSettings();
        state.Apply(settings);
        BluetoothReconnectRuntimeConfig.MaxAttempts = 3;
        RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs = 950;
        RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs = 30000;
        RuntimeTuningConfig.AutoSaveDebounceMs = 1500;
        RuntimeTuningConfig.OutputSwitchDebounceMs = 300;
        RuntimeTuningConfig.InputSwitchDebounceMs = 400;

        state.Apply(AdvancedTuningSettings.Clone(settings));
        Assert.Equal(3, BluetoothReconnectRuntimeConfig.MaxAttempts);
        Assert.Equal(950, RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs);
        Assert.Equal(30000, RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs);
        Assert.Equal(1500, RuntimeTuningConfig.AutoSaveDebounceMs);
        Assert.Equal(300, RuntimeTuningConfig.OutputSwitchDebounceMs);
        Assert.Equal(400, RuntimeTuningConfig.InputSwitchDebounceMs);

        settings.BluetoothReconnect.MaxAttempts = 1;
        settings.AutoSaveDebounceMs = 1000;
        state.Apply(settings);
        Assert.Equal(1, BluetoothReconnectRuntimeConfig.MaxAttempts);
        Assert.Equal(950, RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs);
        Assert.Equal(30000, RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs);
        Assert.Equal(1000, RuntimeTuningConfig.AutoSaveDebounceMs);
        Assert.Equal(300, RuntimeTuningConfig.OutputSwitchDebounceMs);
        Assert.Equal(400, RuntimeTuningConfig.InputSwitchDebounceMs);

        settings.OutputSwitchDebounceMs = 150;
        settings.InputSwitchDebounceMs = 175;
        state.Apply(settings);
        Assert.Equal(150, RuntimeTuningConfig.OutputSwitchDebounceMs);
        Assert.Equal(175, RuntimeTuningConfig.InputSwitchDebounceMs);
        RuntimeTuningConfig.AutoSaveDebounceMs = 1600;

        new AdvancedTuningState().Apply(settings);
        Assert.Equal(1000, RuntimeTuningConfig.AutoSaveDebounceMs);
        Assert.Equal(settings.SteamBigPicture.ConfirmationDelayMs, RuntimeTuningConfig.SteamBigPictureConfirmationDelayMs);
        Assert.Equal(settings.BluetoothReconnect.SuccessStabilizeWindowMs, RuntimeTuningConfig.BluetoothReconnectSuccessStabilizeWindowMs);
    }

    [Fact]
    public void RuntimeMetadata_ReportsStableDefaultsAndDescriptionsAfterOverrides()
    {
        Assert.True(CliRuntimeManager.TrySet("auto-save-debounce-ms", "1234", out _));
        var metadata = CliRuntimeManager.GetKeyMetadata();
        Assert.All(metadata, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.DefaultValue));
            Assert.NotEqual($"Controls {entry.Key.Replace('-', ' ')}.", entry.Description);
        });
        var autoSave = Assert.Single(metadata, entry => entry.Key == "auto-save-debounce-ms");
        Assert.Equal(AppConstants.Timing.AutoSaveDebounceMs.ToString(System.Globalization.CultureInfo.InvariantCulture), autoSave.DefaultValue);
        Assert.Equal("1234", autoSave.CurrentValue);
        Assert.Equal("runtime override; persisted via config", autoSave.Scope);
        var endpointWait = Assert.Single(metadata, entry => entry.Key == "bluetooth-reconnect-success-stabilize-window-ms");
        Assert.Equal("runtime override; persisted via config", endpointWait.Scope);
        var settings = new Settings();
        Assert.False(CliConfigManager.TrySet(settings, endpointWait.Key, "120001", out _));
        settings.AdvancedTuning.BluetoothReconnect.SuccessStabilizeWindowMs = 120001;
        settings.AdvancedTuning.AutoSaveDebounceMs = int.MaxValue;
        settings.AdvancedTuning.OutputSwitchDebounceMs = int.MinValue;
        settings.AdvancedTuning.InputSwitchDebounceMs = int.MaxValue;
        SettingsValidationService.Normalize(settings);
        Assert.Equal(10000, settings.AdvancedTuning.AutoSaveDebounceMs);
        Assert.Equal(25, settings.AdvancedTuning.OutputSwitchDebounceMs);
        Assert.Equal(2000, settings.AdvancedTuning.InputSwitchDebounceMs);
        Assert.Equal(120000, settings.Clone().AdvancedTuning.BluetoothReconnect.SuccessStabilizeWindowMs);
    }

    [Theory]
    [InlineData("auto-save-debounce-ms", 750, 100, 10000)]
    [InlineData("output-switch-debounce-ms", 100, 25, 2000)]
    [InlineData("input-switch-debounce-ms", 100, 25, 2000)]
    public void PersistedDebounce_ValidatesBoundsAndPreservesValuesThroughSerialization(string key, int defaultValue, int minimum, int maximum)
    {
        var settings = new Settings();
        Assert.True(CliConfigManager.TryGet(settings, key, out string value, out _));
        Assert.Equal(defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
        foreach (int invalid in new[] { minimum - 1, maximum + 1 })
            Assert.False(CliConfigManager.TrySet(settings, key, invalid.ToString(System.Globalization.CultureInfo.InvariantCulture), out _));
        foreach (int valid in new[] { minimum, maximum })
        {
            string expected = valid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(CliConfigManager.TrySet(settings, key, expected, out _));
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings.Clone(), SettingsJson.Options), SettingsJson.Options)!;
            SettingsValidationService.Normalize(roundTrip);
            Assert.True(CliConfigManager.TryGet(roundTrip, key, out value, out _));
            Assert.Equal(expected, value);
        }
    }
}
