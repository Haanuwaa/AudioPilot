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
    public void MigrateToCurrent_DoesNotDowngrade_WhenSourceSchemaIsNewer()
    {
        var settings = new Settings
        {
            SchemaVersion = "1.0.1",
        };

        SettingsMigrationResult result = SettingsMigrationService.MigrateToCurrent(settings);

        Assert.Equal("1.0.1", result.OriginalSchemaVersion);
        Assert.Equal("1.0.1", result.FinalSchemaVersion);
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
