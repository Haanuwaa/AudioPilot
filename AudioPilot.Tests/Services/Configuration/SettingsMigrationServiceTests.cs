using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsMigrationServiceTests
{
    [Fact]
    public void MigrateToCurrent_AllowsCurrentSchema()
    {
        var settings = new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
        };

        SettingsMigrationResult result = SettingsMigrationService.MigrateToCurrent(settings);

        Assert.Equal(Settings.CurrentSchemaVersion, result.OriginalSchemaVersion);
        Assert.Equal(Settings.CurrentSchemaVersion, result.FinalSchemaVersion);
        Assert.Empty(result.AppliedMigrations);
        Assert.False(result.IsSourceSchemaNewerThanCurrent);
    }

    [Fact]
    public void ImportUpgradesInitialSchemaAndPreservesWholeAndFractionalAudioValues()
    {
        Settings settings = SettingsTransferService.ParseImportedSettings("""
            {
              "SchemaVersion": "1.0.0",
              "Hotkeys": { "Media": { "SeekStepSeconds": 15 }, "Volume": { "MasterVolumeStepPercent": 7 } },
              "Routines": { "Items": [{ "Id": "quiet", "Name": "Quiet", "MasterVolumePercent": 12 }] }
            }
            """, current: null, replaceImport: true);
        Assert.Equal(Settings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(15m, settings.Hotkeys.Media.SeekStepSeconds);
        Assert.Equal(7m, settings.Hotkeys.Volume.MasterVolumeStepPercent);
        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(12m, routine.MasterVolumePercent);

        settings.Hotkeys.Media.SeekStepSeconds = 0.5m;
        settings.Hotkeys.Volume.MasterVolumeStepPercent = 1.5m;
        routine.MasterVolumePercent = 12.5m;
        Settings restored = SettingsTransferService.ParseImportedSettings(
            SettingsTransferService.SerializeSettings(settings), current: null, replaceImport: true);

        Assert.Equal(0.5m, restored.Hotkeys.Media.SeekStepSeconds);
        Assert.Equal(1.5m, restored.Hotkeys.Volume.MasterVolumeStepPercent);
        Assert.Equal(12.5m, Assert.Single(restored.Routines.Items).MasterVolumePercent);
    }

    [Fact]
    public void MigrateToCurrent_DoesNotDowngrade_WhenSourceSchemaIsNewer()
    {
        var settings = new Settings
        {
            SchemaVersion = "1.1.1",
        };

        SettingsMigrationResult result = SettingsMigrationService.MigrateToCurrent(settings);

        Assert.Equal("1.1.1", result.OriginalSchemaVersion);
        Assert.Equal("1.1.1", result.FinalSchemaVersion);
        Assert.Empty(result.AppliedMigrations);
        Assert.True(result.IsSourceSchemaNewerThanCurrent);
    }

    [Fact]
    public void MigrateToCurrent_Throws_WhenSourceSchemaIsOlder()
    {
        var settings = new Settings
        {
            SchemaVersion = "0.9.0",
        };

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SettingsMigrationService.MigrateToCurrent(settings));

        Assert.Contains(Settings.CurrentSchemaVersion, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void MigrateToCurrent_Throws_WhenSchemaVersionIsBlankOrInvalid(string schemaVersion)
    {
        var settings = new Settings
        {
            SchemaVersion = schemaVersion,
        };

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SettingsMigrationService.MigrateToCurrent(settings));

        Assert.Contains("version string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
