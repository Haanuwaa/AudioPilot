using System.Text.Json;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version100_ImportsPreserveSavedValues(bool replaceImport)
    {
        string fixture = ReadVersion100Fixture();

        Settings imported = SettingsTransferService.ParseImportedSettings(fixture, new Settings(), replaceImport);

        AssertSavedValues(fixture, imported.Clone());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version100_LoadSaveAndReload_PreserveSavedValuesAndOriginalBackup(bool recoverFromBackup)
    {
        using var workspace = new TestSettingsWorkspace(nameof(SettingsCompatibilityTests));
        string fixture = ReadVersion100Fixture();
        string path = Path.Combine(workspace.PrimaryDir, "settings.json");
        string sourcePath = recoverFromBackup ? Path.Combine(workspace.PrimaryDir, "backups", "settings.json.bak") : path;
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, fixture);
        var service = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);

        Settings loaded = service.LoadSettings();
        AssertSavedValues(fixture, loaded);
        service.SaveSettings(loaded);
        string firstSave = File.ReadAllText(path);
        AssertSavedValues(fixture, service.LoadSettings());

        Assert.Equal(firstSave, File.ReadAllText(path));
        Assert.Equal(fixture, File.ReadAllText(Path.Combine(workspace.PrimaryDir, "backups", "settings.json.bak")));
    }

    private static string ReadVersion100Fixture()
    {
        using Stream stream = typeof(SettingsCompatibilityTests).Assembly.GetManifestResourceStream("AudioPilot.Tests.Fixtures.Settings.1.0.0.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertSavedValues(string fixture, Settings settings)
    {
        using JsonDocument expected = JsonDocument.Parse(fixture);
        using JsonDocument actual = JsonDocument.Parse(SettingsTransferService.SerializeSettings(settings));
        AssertValues(expected.RootElement, actual.RootElement);
    }

    private static void AssertValues(JsonElement expected, JsonElement actual, string path = "$")
    {
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind}, actual {actual.ValueKind}");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in expected.EnumerateObject())
            {
                Assert.True(actual.TryGetProperty(property.Name, out JsonElement value), $"Missing persisted key: {path}.{property.Name}");
                AssertValues(property.Value, value, $"{path}.{property.Name}");
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (int index = 0; index < expected.GetArrayLength(); index++)
            {
                AssertValues(expected[index], actual[index], $"{path}[{index}]");
            }
        }
        else
        {
            Assert.True(JsonElement.DeepEquals(expected, actual), $"{path}: expected {expected}, actual {actual}");
        }
    }
}
