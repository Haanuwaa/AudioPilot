using System.IO.Compression;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsTransferServiceTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace;

    public SettingsTransferServiceTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(SettingsTransferServiceTests));
    }

    [Fact]
    public void ExportSettings_WritesJsonFile()
    {
        var settings = BuildSettings();
        string jsonPath = Path.Combine(_workspace.Root, AppConstants.Files.SettingsFileName);

        SettingsTransferService.ExportSettings(settings, jsonPath);

        Assert.True(File.Exists(jsonPath));
        string json = File.ReadAllText(jsonPath);
        Assert.Contains("\"Theme\": \"Dark\"", json, StringComparison.Ordinal);
        Assert.Contains("\"DeviceSwitching\":", json, StringComparison.Ordinal);
        Assert.Contains("\"Routines\":", json, StringComparison.Ordinal);
        Assert.Contains("\"PlayDialogSounds\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportSettings_WritesZipArchiveWithSettingsEntry()
    {
        var settings = BuildSettings();
        string zipPath = Path.Combine(_workspace.Root, "settings-export.zip");

        SettingsTransferService.ExportSettings(settings, zipPath);

        Assert.True(File.Exists(zipPath));

        using var archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = Assert.Single(archive.Entries, archiveEntry => archiveEntry.FullName == SettingsTransferService.SettingsArchiveEntryName);

        using var reader = new StreamReader(entry.Open());
        string json = reader.ReadToEnd();
        Assert.Contains("\"RunAtStartup\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"Routine 1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"PlayDialogSounds\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadImportText_ReadsSettingsJsonFromZipArchive()
    {
        string zipPath = Path.Combine(_workspace.Root, "settings-import.zip");

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry(SettingsTransferService.SettingsArchiveEntryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{\"Theme\":\"Light\",\"RunAtStartup\":true}");
        }

        string json = SettingsTransferService.ReadImportText(zipPath);

        Assert.Contains("\"Theme\":\"Light\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadImportText_ThrowsWhenZipMissingSettingsEntry()
    {
        string zipPath = Path.Combine(_workspace.Root, "invalid-settings.zip");

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("nested/settings.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }

        var exception = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ReadImportText(zipPath));

        Assert.Contains(SettingsTransferService.SettingsArchiveEntryName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadImportText_ThrowsWhenJsonFileExceedsSizeLimit()
    {
        string jsonPath = Path.Combine(_workspace.Root, "oversized-settings.json");
        string oversizedContent = new('a', AppConstants.Limits.MaxSettingsImportFileBytes + 1);
        File.WriteAllText(jsonPath, oversizedContent);

        var exception = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ReadImportText(jsonPath));

        Assert.Contains("import limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadImportText_ThrowsWhenZipEntryExceedsSizeLimit()
    {
        string zipPath = Path.Combine(_workspace.Root, "oversized-settings.zip");

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry(SettingsTransferService.SettingsArchiveEntryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(new string('a', AppConstants.Limits.MaxSettingsImportArchiveEntryBytes + 1));
        }

        var exception = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ReadImportText(zipPath));

        Assert.Contains("settings.json entry exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseImportedSettings_ReplaceModeNormalizesImportedSettings()
    {
        const string importJson = """
        {
            "SchemaVersion": "1.0.0",
            "Theme": "Light",
            "RunAtStartup": true,
            "DeviceSwitching": {
                "Output": {
                    "SwitchRoles": []
                }
            },
            "Routines": {
                "Items": [
                    {
                        "Id": "routine-1",
                        "Name": "Routine 1",
                        "Enabled": true,
                        "OutputDeviceId": "out-1",
                        "OutputDeviceName": "Speakers",
                        "Hotkey": "Ctrl+Alt+R"
                    }
                ]
            }
        }
        """;

        Settings imported = SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: true);

        Assert.Equal(AppTheme.Light, imported.Theme);
        Assert.True(imported.RunAtStartup);
        Assert.True(imported.Miscellaneous.PlayDialogSounds);
        Assert.Equal(["Multimedia", "Communications", "Console"], imported.DeviceSwitching.Output.SwitchRoles);
        AudioRoutine routine = Assert.Single(imported.Routines.Items);
        Assert.Equal("Routine 1", routine.Name);
        Assert.Equal("Ctrl+Alt+R", routine.Hotkey);
    }

    [Fact]
    public void ParseImportedSettings_ReplaceMode_RejectsOlderSchemaVersion()
    {
        const string importJson = """
        {
            "SchemaVersion": "0.9.0",
            "Theme": "Light",
            "RunAtStartup": true
        }
        """;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: true));

        Assert.Contains("unsupported schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseImportedSettings_MergeMode_ReplacesRoutinesFromImport()
    {
        const string importJson = """
        {
            "Routines": {
                "Items": [
                    {
                        "Id": "routine-imported",
                        "Name": "Imported Routine",
                        "Enabled": true,
                        "InputDeviceId": "in-2",
                        "InputDeviceName": "Boom Mic",
                        "Hotkey": "Ctrl+Alt+I"
                    }
                ]
            }
        }
        """;

        Settings imported = SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: false);

        AudioRoutine routine = Assert.Single(imported.Routines.Items);
        Assert.Equal("routine-imported", routine.Id);
        Assert.Equal("Imported Routine", routine.Name);
        Assert.Equal("Ctrl+Alt+I", routine.Hotkey);
        Assert.Equal("in-2", routine.InputDeviceId);
        Assert.False(imported.Miscellaneous.PlayDialogSounds);
    }

    [Fact]
    public void ParseImportedSettings_MergeMode_RejectsOlderSchemaVersion_WhenProvided()
    {
        const string importJson = """
        {
            "SchemaVersion": "0.9.0",
            "Theme": "Light"
        }
        """;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: false));

        Assert.Contains("unsupported schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseImportedSettings_ThrowsWhenSchemaVersionMissingForReplaceImport()
    {
        const string importJson = """
        {
            "Theme": "Light"
        }
        """;

        var exception = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: true));

        Assert.Contains("SchemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseImportedSettings_ThrowsWhenSchemaVersionIsNewerThanSupported()
    {
        const string importJson = """
        {
            "SchemaVersion": "99.0.0",
            "Theme": "Light"
        }
        """;

        var exception = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: true));

        Assert.Contains("unsupported schema version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseImportedSettings_ThrowsWhenUnknownTopLevelPropertyIsPresent()
    {
        const string importJson = """
        {
            "SchemaVersion": "1.0.0",
            "Theme": "Light",
            "UnexpectedProperty": true
        }
        """;

        var exception = Assert.Throws<InvalidDataException>(() => SettingsTransferService.ParseImportedSettings(importJson, BuildSettings(), replaceImport: true));

        Assert.Contains("unsupported top-level property", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private static Settings BuildSettings()
    {
        return new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            Miscellaneous = new MiscellaneousSettings { PlayDialogSounds = false },
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+.",
                    ReverseSwitchHotkey = "Ctrl+Alt+,",
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-1", Name = "Speakers" }
                    ],
                    SwitchRoles = ["Multimedia", "Communications", "Console"]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-1", Name = "Microphone" }
                    ],
                    SwitchRoles = ["Multimedia", "Communications", "Console"]
                }
            },
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Routine 1",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        Hotkey = "Ctrl+Alt+R"
                    }
                ]
            }
        };
    }
}
