using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace;

    public SettingsServiceTests()
    {
        _workspace = new TestSettingsWorkspace(nameof(SettingsServiceTests));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"Hotkeys\":{\"Media\":{}}}")]
    public void NewOrPartialSettings_LeaveMediaHotkeysUnassigned(string? json)
    {
        if (json != null)
        {
            File.WriteAllText(Path.Combine(_workspace.PrimaryDir, "settings.json"), json);
        }
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        Settings settings = service.LoadSettings();
        service.SaveSettings(settings);

        HotkeysMediaSettings media = service.LoadSettings().Hotkeys.Media;
        Assert.Empty(media.ShowCurrentTrack);
        Assert.Empty(media.PlayPause);
        Assert.Empty(media.NextTrack);
        Assert.Empty(media.PreviousTrack);
        Assert.Empty(media.SeekForward);
        Assert.Empty(media.SeekBackward);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSettings_UsingInjectedDirectories()
    {
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var expected = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                PreserveAudioLevels = false,
                BluetoothReconnectEnabled = false,
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+.",
                    ReverseSwitchHotkey = "Ctrl+Alt+Left",
                    SwitchRoles = ["Multimedia", "Communications", "Console"],
                    CycleDevices =
                    [
                        new CycleDevice { Id = "out-1", Name = "Speakers" },
                        new CycleDevice { Id = "out-2", Name = "Headphones" }
                    ]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+,",
                    ReverseSwitchHotkey = "Ctrl+Alt+Right",
                    SwitchRoles = ["Multimedia", "Communications"],
                    CycleDevices =
                    [
                        new CycleDevice { Id = "in-1", Name = "Mic" }
                    ]
                }
            },
            Routines = new RoutinesSettings
            {
                Items =
                [
                    new AudioRoutine
                    {
                        Id = "routine-1",
                        Name = "Desk",
                        Enabled = true,
                        OutputDeviceId = "out-1",
                        OutputDeviceName = "Speakers",
                        InputDeviceId = "in-1",
                        InputDeviceName = "Mic",
                        Hotkey = "Ctrl+Alt+R"
                    }
                ]
            }
        };

        service.SaveSettings(expected);
        var loaded = service.LoadSettings();

        Assert.Equal(expected.DeviceSwitching.Output.SwitchHotkey, loaded.DeviceSwitching.Output.SwitchHotkey);
        Assert.Equal(expected.DeviceSwitching.Output.ReverseSwitchHotkey, loaded.DeviceSwitching.Output.ReverseSwitchHotkey);
        Assert.Equal(expected.DeviceSwitching.Input.SwitchHotkey, loaded.DeviceSwitching.Input.SwitchHotkey);
        Assert.Equal(expected.DeviceSwitching.Input.ReverseSwitchHotkey, loaded.DeviceSwitching.Input.ReverseSwitchHotkey);
        Assert.Equal(expected.RunAtStartup, loaded.RunAtStartup);
        Assert.Equal(expected.Theme, loaded.Theme);
        Assert.Equal(expected.DeviceSwitching.PreserveAudioLevels, loaded.DeviceSwitching.PreserveAudioLevels);
        Assert.Equal(expected.DeviceSwitching.BluetoothReconnectEnabled, loaded.DeviceSwitching.BluetoothReconnectEnabled);
        Assert.Equal(expected.DeviceSwitching.Output.SwitchRoles, loaded.DeviceSwitching.Output.SwitchRoles);
        Assert.Equal(expected.DeviceSwitching.Input.SwitchRoles, loaded.DeviceSwitching.Input.SwitchRoles);
        Assert.Equal(2, loaded.DeviceSwitching.Output.CycleDevices.Count);
        Assert.Equal("out-1", loaded.DeviceSwitching.Output.CycleDevices[0].Id);
        Assert.Equal("in-1", Assert.Single(loaded.DeviceSwitching.Input.CycleDevices).Id);
        AudioRoutine loadedRoutine = Assert.Single(loaded.Routines.Items);
        Assert.Equal("routine-1", loadedRoutine.Id);
        Assert.Equal("Desk", loadedRoutine.Name);
        Assert.True(loadedRoutine.Enabled);
        Assert.Equal("Ctrl+Alt+R", loadedRoutine.Hotkey);
    }

    [Fact]
    public void LoadSettings_MapsRunAtStartup_AndPreservesTheme()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        File.WriteAllText(settingsPath, BuildSupportedSettingsJson(theme: "Light", runAtStartup: true));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var loaded = service.LoadSettings();

        Assert.True(loaded.RunAtStartup);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(string.Empty, loaded.DeviceSwitching.Output.ReverseSwitchHotkey);
        Assert.Equal(string.Empty, loaded.DeviceSwitching.Input.ReverseSwitchHotkey);
    }

    [Fact]
    public void LoadSettings_LoadsDefaults_WhenOlderSchemaVersionIsUnsupported()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        File.WriteAllText(
            settingsPath,
            BuildSupportedSettingsJson(
                schemaVersion: "0.1.0",
                outputSwitchRoles: ["Multimedia", "Console"],
                inputSwitchRoles: ["Multimedia", "Communications"]));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var loaded = service.LoadSettings();

        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.RunAtStartup);
        Assert.Equal(AppTheme.System, loaded.Theme);
    }

    [Fact]
    public void LoadSettings_NormalizesInvalidOrEmptyRoleLists()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        File.WriteAllText(
            settingsPath,
            BuildSupportedSettingsJson(
                outputSwitchRoles: ["  multimedia ", "invalid", "CONSOLE", "Multimedia"],
                inputSwitchRoles: []));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var loaded = service.LoadSettings();

        Assert.Equal(["Multimedia", "Console"], loaded.DeviceSwitching.Output.SwitchRoles);
        Assert.Equal(["Multimedia", "Communications", "Console"], loaded.DeviceSwitching.Input.SwitchRoles);
        Assert.False(loaded.RunAtStartup);
    }

    [Fact]
    public void LoadSettings_RewritesToCanonicalSchema_RemovesUnknownAndAddsMissingFields()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        File.WriteAllText(
            settingsPath,
            BuildSupportedSettingsJson(
                extraRootProperties: """
                    "ObsoleteField": "remove-me"
                """));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        _ = service.LoadSettings();

        string persistedJson = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("ObsoleteField", persistedJson);
        Assert.Contains("\"AutoScrollToMixerOnRestore\"", persistedJson);
        Assert.Contains("\"PlayAppSounds\": true", persistedJson);
    }

    [Fact]
    public void LoadSettings_DoesNotRewrite_WhenSettingsJsonIsAlreadyCanonical()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        var settings = new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+.",
                    ReverseSwitchHotkey = "Ctrl+Alt+Left",
                    SwitchRoles = ["Multimedia", "Communications", "Console"]
                },
                Input = new DeviceSwitchingInputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+,",
                    ReverseSwitchHotkey = "Ctrl+Alt+Right",
                    SwitchRoles = ["Multimedia", "Communications", "Console"]
                }
            }
        };

        string canonicalJson = JsonSerializer.Serialize(settings, SettingsJson.Options);
        File.WriteAllText(settingsPath, canonicalJson);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        _ = service.LoadSettings();

        string persistedJson = File.ReadAllText(settingsPath);
        Assert.Equal(canonicalJson, persistedJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadSettings_PreservesValidSettings_WhenCanonicalRewriteCannotBeSaved(bool hasOlderFallback)
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string originalJson = BuildSupportedSettingsJson(theme: "Light", runAtStartup: true);
        File.WriteAllText(settingsPath, originalJson);
        if (hasOlderFallback)
        {
            File.WriteAllText(
                Path.Combine(_workspace.FallbackDir, "settings.json"),
                BuildSupportedSettingsJson(theme: "Dark", runAtStartup: false));
        }

        File.WriteAllText(Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName), "blocked");
        File.WriteAllText(Path.Combine(_workspace.FallbackDir, AppConstants.Files.BackupFolderName), "blocked");
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal(originalJson, File.ReadAllText(settingsPath));
        Assert.Equal(settingsPath, service.GetSettingsPath());
    }

    [Fact]
    public void LoadSettings_DoesNotRewrite_WhenSourceSchemaIsNewer_PreservesUnknownFields()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string originalJson = """
        {
                    "SchemaVersion": "1.2.0",
                    "Theme": "System",
                    "DeviceSwitching": {
                        "PreserveAudioLevels": true,
                        "Output": {
                            "CycleDevices": [],
                            "SwitchHotkey": "",
                            "SwitchRoles": ["Multimedia", "Communications", "Console"]
                        },
                        "Input": {
                            "CycleDevices": [],
                            "SwitchHotkey": "",
                            "SwitchRoles": ["Multimedia", "Communications", "Console"]
                        }
                    },
                    "Hotkeys": {
                        "App": { "ToggleAppVisibility": "Ctrl+Alt+H" },
                        "Media": { "PlayPause": "Ctrl+Alt+P", "NextTrack": "Ctrl+Alt+.", "PreviousTrack": "Ctrl+Alt+," },
                        "Mute": { "Mic": "", "Sound": "", "Deafen": "" }
                    },
                    "Miscellaneous": { "LogLevel": "Info" },
                    "RunAtStartup": false,
                    "FutureField": "keep-me"
        }
        """;

        File.WriteAllText(settingsPath, originalJson);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal("1.2.0", loaded.SchemaVersion);

        string persistedJson = File.ReadAllText(settingsPath);
        Assert.Contains("\"FutureField\": \"keep-me\"", persistedJson);
        Assert.Equal(originalJson.Replace("\r\n", "\n"), persistedJson.Replace("\r\n", "\n"));
    }

    [Fact]
    public void LoadSettings_WhenSourceSchemaIsNewer_EnsuresRequiredStructureWithoutRewriting()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string originalJson = """
        {
                    "SchemaVersion": "1.2.0",
                    "Theme": "System",
                    "DeviceSwitching": null,
                    "Hotkeys": null,
                    "Routines": null,
                    "FutureField": "keep-me"
        }
        """;

        File.WriteAllText(settingsPath, originalJson);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal("1.2.0", loaded.SchemaVersion);
        Assert.NotNull(loaded.DeviceSwitching);
        Assert.NotNull(loaded.DeviceSwitching.Output);
        Assert.NotNull(loaded.DeviceSwitching.Input);
        Assert.NotNull(loaded.Hotkeys);
        Assert.NotNull(loaded.Routines);

        string persistedJson = File.ReadAllText(settingsPath);
        Assert.Equal(originalJson.Replace("\r\n", "\n"), persistedJson.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SaveSettings_RefusesNewerFileWrittenSinceLoad_WithoutUsingAnotherLocation(bool fallback, bool incompatibleValue)
    {
        string directory = fallback ? _workspace.FallbackDir : _workspace.PrimaryDir;
        string settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, SettingsTransferService.SerializeSettings(new Settings()));
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        Settings loaded = service.LoadSettings();
        string newerJson = incompatibleValue
            ? """{"schemaVersion":"99.0.0","Theme":{"Future":"value"}}"""
            : """{"SchemaVersion":"99.0.0","DeviceSwitching":{"FutureMode":"keep"}}""";
        File.WriteAllText(settingsPath, newerJson);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => service.SaveSettings(loaded));

        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(newerJson, File.ReadAllText(settingsPath));
        Assert.Equal(settingsPath, service.GetSettingsPath());
        string otherDirectory = fallback ? _workspace.PrimaryDir : _workspace.FallbackDir;
        Assert.False(File.Exists(Path.Combine(otherDirectory, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(directory, "backups")));
    }

    [Fact]
    public void SaveSettings_RefusesNewerModelBeforeNormalizationOrCreatingFiles()
    {
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var settings = new Settings { SchemaVersion = "99.0.0", DeviceSwitching = null! };

        Assert.Throws<InvalidDataException>(() => service.SaveSettings(settings));

        Assert.Null(settings.DeviceSwitching);
        Assert.Empty(Directory.GetFiles(_workspace.PrimaryDir, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_workspace.FallbackDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void LoadSettings_IgnoresUnsupportedFlatLegacyKeys_AndRewritesToNestedSchema()
    {
        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        File.WriteAllText(settingsPath, """
        {
            "SchemaVersion": "1.0.0",
            "Theme": "Dark",
            "RunAtStartup": true,
            "OutputSwitchHotkey": "Ctrl+Alt+LEGACY"
        }
        """);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal(string.Empty, loaded.DeviceSwitching.Output.SwitchHotkey);

        string persistedJson = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("\"OutputSwitchHotkey\"", persistedJson);
        Assert.Contains("\"DeviceSwitching\"", persistedJson);
    }

    [Fact]
    public void DeleteSettingsFiles_RemovesBothPrimaryAndFallbackFiles()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string fallbackPath = Path.Combine(_workspace.FallbackDir, "settings.json");
        File.WriteAllText(primaryPath, "{}");
        File.WriteAllText(fallbackPath, "{}");

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        service.DeleteSettingsFiles();

        Assert.False(File.Exists(primaryPath));
        Assert.False(File.Exists(fallbackPath));
    }

    [Fact]
    public void DeleteSettingsFiles_DoesNotDeleteUnrelatedLogBackupsInSharedBackupDirectory()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        string settingsBackupPath = Path.Combine(backupDirectory, "settings.json.bak");
        string logBackupPath = Path.Combine(backupDirectory, AppConstants.Files.LogFileName + ".bak");

        Directory.CreateDirectory(backupDirectory);
        File.WriteAllText(primaryPath, "{}");
        File.WriteAllText(settingsBackupPath, "{}");
        File.WriteAllText(logBackupPath, "log-backup");

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        service.DeleteSettingsFiles();

        Assert.False(File.Exists(primaryPath));
        Assert.False(File.Exists(settingsBackupPath));
        Assert.True(File.Exists(logBackupPath));
    }

    [Fact]
    public void LoadSettings_RecoversFromBackup_WhenPrimaryJsonIsCorrupted()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, "settings.json.bak");

        File.WriteAllText(primaryPath, "{ not-valid-json");
        File.WriteAllText(backupPath, """
                {
                    "SchemaVersion": "1.0.0",
                    "Theme": "Dark",
                    "RunAtStartup": true,
                    "DeviceSwitching": {
                        "PreserveAudioLevels": true,
                        "Output": {
                            "SwitchHotkey": "Ctrl+Alt+.",
                            "CycleDevices": [
                                { "Id": "out-a", "Name": "Speakers" }
                            ],
                            "SwitchRoles": ["Multimedia", "Console"]
                        },
                        "Input": {
                            "SwitchHotkey": "Ctrl+Alt+,",
                            "CycleDevices": [
                                { "Id": "in-a", "Name": "Mic" }
                            ],
                            "SwitchRoles": ["Communications", "Console"]
                        }
                    },
                    "Hotkeys": {
                        "App": { "ToggleAppVisibility": "Ctrl+Alt+H" },
                        "Media": { "PlayPause": "Ctrl+Alt+P", "NextTrack": "Ctrl+Alt+.", "PreviousTrack": "Ctrl+Alt+," },
                        "Mute": { "Mic": "", "Sound": "", "Deafen": "" },
                        "Listen": { "ListenToInput": "" }
                    },
                    "Miscellaneous": { "LogLevel": "Info" },
                    "Routines": { "Items": [] }
                }
                """);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        var loaded = service.LoadSettings();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal("out-a", Assert.Single(loaded.DeviceSwitching.Output.CycleDevices).Id);
        Assert.Equal("in-a", Assert.Single(loaded.DeviceSwitching.Input.CycleDevices).Id);
    }

    [Fact]
    public void LoadSettings_IgnoresBackupWithUnsupportedOlderSchema_AndLoadsDefaults()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        string backupPath = Path.Combine(backupDirectory, "settings.json.bak");

        Directory.CreateDirectory(backupDirectory);
        File.WriteAllText(primaryPath, "{ not-valid-json");
        File.WriteAllText(backupPath, """
        {
            "SchemaVersion": "0.9.0",
            "Theme": "Light",
            "RunAtStartup": true
        }
        """);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.False(loaded.RunAtStartup);
    }

    [Fact]
    public void LoadSettings_UsesFallbackSettingsFile_WhenPrimaryJsonIsCorrupted()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string fallbackPath = Path.Combine(_workspace.FallbackDir, "settings.json");

        File.WriteAllText(primaryPath, "{ not-valid-json");
        File.WriteAllText(
            fallbackPath,
            BuildSupportedSettingsJson(
                theme: "Dark",
                runAtStartup: true,
                outputSwitchHotkey: "Ctrl+Alt+FALLBACK"));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal("Ctrl+Alt+FALLBACK", loaded.DeviceSwitching.Output.SwitchHotkey);
        Assert.Equal(fallbackPath, service.GetSettingsPath());
    }

    [Fact]
    public void LoadSettings_IgnoresFallbackWithUnsupportedOlderSchema_AndLoadsDefaults()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string fallbackPath = Path.Combine(_workspace.FallbackDir, "settings.json");

        File.WriteAllText(primaryPath, "{ not-valid-json");
        File.WriteAllText(fallbackPath, """
        {
            "SchemaVersion": "0.9.0",
            "Theme": "Dark",
            "RunAtStartup": true
        }
        """);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(AppTheme.System, loaded.Theme);
        Assert.False(loaded.RunAtStartup);
        Assert.Equal(primaryPath, service.GetSettingsPath());
    }

    [Fact]
    public async Task SaveSettings_ConcurrentWriters_PersistValidJsonWithoutCorruption()
    {
        var firstWriter = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var secondWriter = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        var firstSettings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+O"
                }
            },
            Theme = AppTheme.Dark
        };

        var secondSettings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+P"
                }
            },
            Theme = AppTheme.Light
        };

        Task t1 = Task.Run(() => firstWriter.SaveSettings(firstSettings), TestContext.Current.CancellationToken);
        Task t2 = Task.Run(() => secondWriter.SaveSettings(secondSettings), TestContext.Current.CancellationToken);

        await Task.WhenAll(t1, t2);

        string settingsPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string json = File.ReadAllText(settingsPath);

        Settings? loaded = JsonSerializer.Deserialize<Settings>(json);
        Assert.NotNull(loaded);
        Assert.True(
            string.Equals(loaded.DeviceSwitching.Output.SwitchHotkey, firstSettings.DeviceSwitching.Output.SwitchHotkey, StringComparison.Ordinal)
            || string.Equals(loaded.DeviceSwitching.Output.SwitchHotkey, secondSettings.DeviceSwitching.Output.SwitchHotkey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadSettings_ConcurrentWithSave_ReturnsValidSnapshot()
    {
        var writer = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var reader = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        var initialSettings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+Initial"
                }
            },
            Theme = AppTheme.System
        };

        var updatedSettings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+Updated"
                }
            },
            Theme = AppTheme.Dark
        };

        writer.SaveSettings(initialSettings);

        Task saveTask = Task.Run(() => writer.SaveSettings(updatedSettings), TestContext.Current.CancellationToken);
        Task<Settings> loadTask = Task.Run(() => reader.LoadSettings());

        await Task.WhenAll(saveTask, loadTask);

        Settings loaded = await loadTask;

        Assert.True(
            string.Equals(loaded.DeviceSwitching.Output.SwitchHotkey, initialSettings.DeviceSwitching.Output.SwitchHotkey, StringComparison.Ordinal)
            || string.Equals(loaded.DeviceSwitching.Output.SwitchHotkey, updatedSettings.DeviceSwitching.Output.SwitchHotkey, StringComparison.Ordinal));
        Assert.True(loaded.Theme == AppTheme.System || loaded.Theme == AppTheme.Dark);
    }

    [Fact]
    public void ReadTextFileWithSettingsLock_ReturnsFileContents()
    {
        string importPath = Path.Combine(_workspace.Root, "import-settings.json");
        const string json = "{\"Theme\":\"Dark\"}";
        File.WriteAllText(importPath, json);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        string result = service.ReadTextFileWithSettingsLock(importPath);

        Assert.Equal(json, result);
    }

    [Fact]
    public void LoadSettings_RecoversFromNewestBackupCandidate_WhenPrimaryIsCorrupted()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);

        string newestBackupPath = Path.Combine(backupDirectory, "settings.json.bak");
        string olderBackupPath = Path.Combine(backupDirectory, "settings.json.bak.1");

        File.WriteAllText(primaryPath, "{ not-valid-json");

        File.WriteAllText(newestBackupPath, BuildSupportedSettingsJson(theme: "Dark", outputSwitchHotkey: "Ctrl+Alt+NEW"));
        File.WriteAllText(olderBackupPath, BuildSupportedSettingsJson(theme: "Light", runAtStartup: true, outputSwitchHotkey: "Ctrl+Alt+OLD"));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.False(loaded.RunAtStartup);
        Assert.Equal("Ctrl+Alt+NEW", loaded.DeviceSwitching.Output.SwitchHotkey);
    }

    [Fact]
    public void LoadSettings_RecoversFromOlderBackup_WhenNewestBackupIsCorrupted()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(SettingsServiceTests), "settings-backup-recovery.log", LogLevel.Warning);
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);

        string newestBackupPath = Path.Combine(backupDirectory, "settings.json.bak");
        string olderBackupPath = Path.Combine(backupDirectory, "settings.json.bak.1");

        File.WriteAllText(primaryPath, "{ not-valid-json");
        File.WriteAllText(newestBackupPath, "{ still-not-valid-json");
        File.WriteAllText(
            olderBackupPath,
            BuildSupportedSettingsJson(
                theme: "Light",
                runAtStartup: true,
                outputSwitchHotkey: "Ctrl+Alt+RECOVERED"));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir, loggerScope.Logger);

        Settings loaded = service.LoadSettings();

        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal("Ctrl+Alt+RECOVERED", loaded.DeviceSwitching.Output.SwitchHotkey);

        string logText = TestLogFileAssert.WaitForLogText(
            loggerScope.LogPath,
            2000,
            "settings-backup-candidate-failed",
            "source=primary:settings.json.bak",
            "Recovered settings from backup",
            "source=primary:settings.json.bak.1");
    }

    [Fact]
    public void LoadSettings_RecoversFromBackup_WhenSettingsFileIsMissing()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, "settings.json.bak");

        File.WriteAllText(
            backupPath,
            BuildSupportedSettingsJson(
                theme: "Dark",
                runAtStartup: true,
                outputSwitchHotkey: "Ctrl+Alt+RESTORED"));

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.True(File.Exists(primaryPath));
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.RunAtStartup);
        Assert.Equal("Ctrl+Alt+RESTORED", loaded.DeviceSwitching.Output.SwitchHotkey);

        string persistedJson = File.ReadAllText(primaryPath);
        Settings persisted = JsonSerializer.Deserialize<Settings>(persistedJson, SettingsJson.Options)!;
        Assert.Equal("Ctrl+Alt+RESTORED", persisted.DeviceSwitching.Output.SwitchHotkey);
    }

    [Fact]
    public void LoadSettings_CreatesSettingsFile_WhenMissingAndNoBackupExists()
    {
        string primaryPath = Path.Combine(_workspace.PrimaryDir, "settings.json");
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        Settings loaded = service.LoadSettings();

        Assert.True(File.Exists(primaryPath));
        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);

        string persistedJson = File.ReadAllText(primaryPath);
        Assert.Contains($"\"SchemaVersion\": \"{Settings.CurrentSchemaVersion}\"", persistedJson);
    }

    [Fact]
    public void SettingsFileExists_ReturnsTrue_WhenPrimaryBackupsExistWithoutActiveSettingsFile()
    {
        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, "settings.json.bak");
        File.WriteAllText(backupPath, "{}");

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        bool exists = service.SettingsFileExists();

        Assert.True(exists);
    }

    [Fact]
    public void SaveSettings_RetainsConfiguredNumberOfBackups()
    {
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);

        int writes = AppConstants.Files.SettingsBackupRetentionCount + 3;
        for (int index = 0; index < writes; index++)
        {
            service.SaveSettings(new Settings
            {
                DeviceSwitching = new DeviceSwitchingSettings
                {
                    Output = new DeviceSwitchingOutputSettings
                    {
                        SwitchHotkey = $"Ctrl+Alt+{index}"
                    }
                }
            });
        }

        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        string[] backupFiles = [.. Directory.GetFiles(backupDirectory, "settings.json.bak*")];

        Assert.True(backupFiles.Length <= AppConstants.Files.SettingsBackupRetentionCount);
        Assert.Contains(backupFiles, path => string.Equals(path, Path.Combine(backupDirectory, "settings.json.bak"), StringComparison.Ordinal));
    }

    [Fact]
    public void SaveSettings_WhenContentUnchanged_DoesNotCreateBackups()
    {
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        var settings = new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+O"
                }
            },
            Theme = AppTheme.Dark,
            RunAtStartup = true
        };

        service.SaveSettings(settings);
        service.SaveSettings(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+O"
                }
            },
            Theme = AppTheme.Dark,
            RunAtStartup = true
        });

        string backupDirectory = Path.Combine(_workspace.PrimaryDir, AppConstants.Files.BackupFolderName);
        if (!Directory.Exists(backupDirectory))
        {
            return;
        }

        string[] backupFiles = [.. Directory.GetFiles(backupDirectory, "settings.json.bak*")];
        Assert.Empty(backupFiles);
    }

    [Fact]
    public void SaveSettings_WhenWriteFails_ThrowsGenericIOExceptionMessage()
    {
        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir);
        TestPrivateAccess.SetField(service, "_primarySettingsPath", _workspace.PrimaryDir);
        TestPrivateAccess.SetField(service, "_fallbackSettingsPath", _workspace.FallbackDir);
        TestPrivateAccess.SetField(service, "_activeSettingsPath", _workspace.PrimaryDir);
        TestPrivateAccess.SetField(service, "_useFallback", false);

        IOException ex = Assert.Throws<IOException>(() => service.SaveSettings(new Settings()));

        Assert.Equal("Failed to save settings.", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.DoesNotContain(_workspace.PrimaryDir, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_workspace.FallbackDir, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveSettings_WhenPrimaryWriteFails_LogsReasonBeforeFallingBack()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(SettingsServiceTests), "settings-save-fallback.log", LogLevel.Warning);
        string blockedPrimaryPath = Path.Combine(_workspace.PrimaryDir, "blocked-primary");
        Directory.CreateDirectory(blockedPrimaryPath);
        string fallbackSettingsPath = Path.Combine(_workspace.FallbackDir, AppConstants.Files.SettingsFileName);

        var service = new SettingsService(_workspace.PrimaryDir, _workspace.FallbackDir, loggerScope.Logger);
        service.OverrideWriteTargetsForTests(blockedPrimaryPath, fallbackSettingsPath);

        service.SaveSettings(new Settings
        {
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+F"
                }
            },
            Theme = AppTheme.Dark
        });

        Assert.True(File.Exists(fallbackSettingsPath));
        Assert.Equal(fallbackSettingsPath, service.GetSettingsPath());

        string logText = TestLogFileAssert.WaitForLogText(
            loggerScope.LogPath,
            2000,
            "settings-save-primary-io-fallback",
            "target=primary:blocked-primary",
            "error=IOException",
            "<path>");

        Assert.DoesNotContain("message=", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(blockedPrimaryPath, logText, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private static string BuildSupportedSettingsJson(
        string schemaVersion = Settings.CurrentSchemaVersion,
        string theme = "System",
        bool runAtStartup = false,
        string outputSwitchHotkey = "",
        string inputSwitchHotkey = "",
        IReadOnlyList<string>? outputSwitchRoles = null,
        IReadOnlyList<string>? inputSwitchRoles = null,
        string extraRootProperties = "")
    {
        string outputRolesJson = FormatStringArray(outputSwitchRoles ?? ["Multimedia", "Communications", "Console"]);
        string inputRolesJson = FormatStringArray(inputSwitchRoles ?? ["Multimedia", "Communications", "Console"]);
        string extraSection = string.IsNullOrWhiteSpace(extraRootProperties)
            ? string.Empty
            : $",\n{extraRootProperties}";

        return $$"""
        {
            "SchemaVersion": "{{schemaVersion}}",
            "Theme": "{{theme}}",
            "RunAtStartup": {{runAtStartup.ToString().ToLowerInvariant()}},
            "DeviceSwitching": {
                "PreserveAudioLevels": true,
                "Output": {
                    "CycleDevices": [],
                    "SwitchHotkey": "{{outputSwitchHotkey}}",
                    "SwitchRoles": {{outputRolesJson}}
                },
                "Input": {
                    "CycleDevices": [],
                    "SwitchHotkey": "{{inputSwitchHotkey}}",
                    "SwitchRoles": {{inputRolesJson}}
                }
            },
            "Hotkeys": {
                "App": { "ToggleAppVisibility": "Ctrl+Alt+H" },
                "Media": { "PlayPause": "Ctrl+Alt+P", "NextTrack": "Ctrl+Alt+.", "PreviousTrack": "Ctrl+Alt+," },
                "Mute": { "Mic": "", "Sound": "", "Deafen": "" }
            },
            "Miscellaneous": { "LogLevel": "Info" }{{extraSection}}
        }
        """;
    }

    private static string FormatStringArray(IReadOnlyList<string> values)
    {
        return $"[{string.Join(", ", values.Select(value => $"\"{value}\""))}]";
    }

}

