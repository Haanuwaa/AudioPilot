using System.Text.Json;
using System.Text.Json.Nodes;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsJsonTests
{
    [Theory]
    [InlineData("{\"SchemaVersion\":\"1.0.0\"}")]
    [InlineData("{\"SchemaVersion\":\"1.0.0\",\"App\":null,\"Startup\":null,\"Updates\":null,\"Diagnostics\":null,\"Routines\":null}")]
    public void MissingOrNullCanonicalSectionsRestoreDefaultsAndExportOnlyCanonicalPaths(string json)
    {
        Settings settings = SettingsTransferService.ParseImportedSettings(json, null, replaceImport: true);
        Assert.Equal(AppTheme.System, settings.App.Theme);
        Assert.Equal("System", settings.App.Language);
        Assert.True(settings.App.PlayAppSounds);
        Assert.False(settings.App.AutoSaveEnabled);
        Assert.False(settings.Startup.Enabled);
        Assert.False(settings.Startup.UseTaskScheduler);
        Assert.False(settings.Startup.SuppressDeviceWarnings);
        Assert.False(settings.Updates.CheckAutomatically);
        Assert.True(settings.Diagnostics.RedactLogContent);
        Assert.Equal("Info", settings.Diagnostics.LogLevel);
        Assert.Equal(TimeZoneInfo.Local.Id, settings.Routines.DefaultTimeZoneId);
        Assert.Empty(settings.Routines.Items);
        using var document = JsonDocument.Parse(SettingsTransferService.SerializeSettings(settings.Clone()));
        Assert.Equal(Settings.CurrentSchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetString());
        Assert.False(document.RootElement.TryGetProperty("Miscellaneous", out _));
        Assert.False(document.RootElement.TryGetProperty("Theme", out _));
        Assert.False(document.RootElement.TryGetProperty("Language", out _));
        Assert.False(document.RootElement.TryGetProperty("RunAtStartup", out _));
    }

    [Fact]
    public void PickerScope_DefaultsToConfiguredAndSurvivesCloningAndExportImport()
    {
        Assert.False(JsonSerializer.Deserialize<Settings>("{}", SettingsJson.ImportOptions)!.DeviceSwitching.PickerShowAllAvailableDevices);
        var settings = new Settings();
        settings.DeviceSwitching.PickerShowAllAvailableDevices = true;
        Settings cloned = settings.Clone();
        Assert.True(cloned.DeviceSwitching.PickerShowAllAvailableDevices);
        cloned.DeviceSwitching.PickerShowAllAvailableDevices = false;
        Assert.True(settings.DeviceSwitching.PickerShowAllAvailableDevices);
        Settings imported = SettingsTransferService.ParseImportedSettings(SettingsTransferService.SerializeSettings(settings), null, replaceImport: true);
        Assert.True(imported.DeviceSwitching.PickerShowAllAvailableDevices);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"DeviceSwitching\":{\"PreserveAudioLevels\":false}}")]
    public void MissingMuteContinuitySetting_DefaultsEnabled_IndependentlyOfVolume(string json)
    {
        Settings settings = JsonSerializer.Deserialize<Settings>(json, SettingsJson.ImportOptions)!;
        Assert.True(settings.DeviceSwitching.KeepMuteStateWhenSwitching);
        Assert.Equal(json == "{}", settings.DeviceSwitching.PreserveAudioLevels);
    }

    [Theory]
    [InlineData(RoutineTriggerKind.Hotkey)]
    [InlineData(RoutineTriggerKind.Application)]
    [InlineData(RoutineTriggerKind.AudioPilotStartup)]
    [InlineData(RoutineTriggerKind.SteamBigPicture)]
    [InlineData(RoutineTriggerKind.DeviceChange)]
    [InlineData(RoutineTriggerKind.Scheduled)]
    [InlineData(RoutineTriggerKind.Network)]
    public void RoutineRoundTrip_IsIndependentOfJsonPropertyOrder(RoutineTriggerKind trigger)
    {
        var routine = new AudioRoutine
        {
            Id = "json-roundtrip",
            Name = "音楽 🎧 \"Desk\"",
            OutputDeviceId = "output-1",
            OutputDeviceStableId = " Opaque-Stable-Output ",
            InputDeviceStableId = "Opaque-Stable-Input",
            TriggerKind = trigger,
            TriggerAppPath = @"C:\Apps\Player.exe",
            RouteToApplication = true,
            ApplicationTriggerMode = trigger == RoutineTriggerKind.Application ? ApplicationTriggerMode.ProcessFocus : ApplicationTriggerMode.AppLaunch,
            ApplicationTriggerTitlePattern = "voice|stream",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
            RestorePreviousAudioOnDeactivate = true,
            ShowInTrayMenu = true,
            ScheduleTime = trigger == RoutineTriggerKind.Scheduled ? new TimeOnly(21, 45, 12) : new TimeOnly(12, 0),
            ScheduleDays = trigger == RoutineTriggerKind.Scheduled ? [DayOfWeek.Monday, DayOfWeek.Friday] : [],
            NotifyBeforeScheduledRun = trigger == RoutineTriggerKind.Scheduled,
            TriggerNetworkName = "Office WiFi",
            NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
        };
        JsonObject original = JsonSerializer.SerializeToNode(routine, SettingsJson.Options)!.AsObject();
        var reordered = new JsonObject(original.Reverse().Select(property => KeyValuePair.Create(property.Key, property.Value?.DeepClone())));

        AudioRoutine imported = RoutineTransferService.ParseSingleRoutine(reordered.ToJsonString());
        var settingsDocument = new JsonObject
        {
            [nameof(Settings.Routines)] = new JsonObject { [nameof(RoutinesSettings.Items)] = new JsonArray(reordered.DeepClone()) },
        };
        Settings settings = JsonSerializer.Deserialize<Settings>(settingsDocument, SettingsJson.Options)!;

        Assert.True(JsonNode.DeepEquals(original, JsonSerializer.SerializeToNode(imported, SettingsJson.Options)));
        Assert.True(JsonNode.DeepEquals(original, JsonSerializer.SerializeToNode(Assert.Single(settings.Routines.Items), SettingsJson.Options)));
        Assert.False(original.ContainsKey(nameof(AudioRoutine.DisplayOrder)));
        Assert.False(original.ContainsKey(nameof(AudioRoutine.LastRunStatusText)));
    }

    [Theory]
    [InlineData("{'App':{'Theme':'Light'}}")]
    [InlineData("{\"App\":{Theme:\"Light\"}}")]
    [InlineData("{\"App\":{\"Theme\":\"Light\",}}")]
    [InlineData("{\"App\":{/*comment*/\"Theme\":\"Light\"}}")]
    [InlineData("{\"Startup\":{\"Enabled\":\"true\"}}")]
    [InlineData("{\"Startup\":{\"Enabled\":null}}")]
    [InlineData("{\"App\":{\"Theme\":\"UnknownTheme\"}}")]
    [InlineData("{\"AdvancedTuning\":{\"BluetoothReconnect\":{\"MaxAttempts\":\"2\"}}}")]
    public void SettingsDeserialize_RejectsInvalidSyntaxAndTypes(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Settings>(json, SettingsJson.Options));
    }

    [Theory]
    [InlineData("{\"App\":{\"Theme\":999}}", "$.App.Theme")]
    [InlineData("{\"App\":{\"Theme\":-1}}", "$.App.Theme")]
    [InlineData("{\"App\":{\"Theme\":\"999\"}}", "$.App.Theme")]
    [InlineData("{\"Overlay\":{\"Position\":999}}", "$.Overlay.Position")]
    [InlineData("{\"Diagnostics\":{\"DeviceReferenceFileMode\":999}}", "$.Diagnostics.DeviceReferenceFileMode")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Triggers\":[{\"Kind\":999}]}]}}", "$.Routines.Items[0].Triggers[0].Kind")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Triggers\":[{\"ApplicationMode\":999,\"Kind\":\"Application\"}]}]}}", "$.Routines.Items[0].Triggers[0].ApplicationMode")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Triggers\":[{\"TitleMatchMode\":999,\"Kind\":\"Application\"}]}]}}", "$.Routines.Items[0].Triggers[0].TitleMatchMode")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Triggers\":[{\"NetworkDirection\":999,\"Kind\":\"Application\"}]}]}}", "$.Routines.Items[0].Triggers[0].NetworkDirection")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Triggers\":[{\"Days\":[7],\"Kind\":\"Scheduled\"}]}]}}", "$.Routines.Items[0].Triggers[0].Days[0]")]
    public void SettingsReadAndImport_RejectUndefinedEnumsBeforeNormalization(string json, string path)
    {
        JsonException readError = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Settings>(json, SettingsJson.Options));
        Assert.Equal(path, readError.Path);
        foreach (bool replace in new[] { false, true })
        {
            JsonObject document = JsonNode.Parse(json)!.AsObject();
            document[nameof(Settings.SchemaVersion)] = Settings.CurrentSchemaVersion;
            JsonException importError = Assert.Throws<JsonException>(() => SettingsTransferService.ParseImportedSettings(document.ToJsonString(), new Settings(), replace));
            Assert.Equal(path, importError.Path);
        }
    }

    [Theory]
    [InlineData("{\"Overlay\":{\"Enabledd\":false}}", "Enabledd")]
    [InlineData("{\"overlay\":{\"Enabledd\":null}}", "Enabledd")]
    [InlineData("{\"AdvancedTuning\":{\"BluetoothReconnect\":{\"MaxAttempt\":2}}}", "MaxAttempt")]
    [InlineData("{\"Hotkeys\":{\"App\":{\"UnsupportedProperty\":null}}}", "UnsupportedProperty")]
    [InlineData("{\"DeviceSwitching\":{\"Output\":{\"CycleDevices\":[{\"Id\":\"one\",\"Nmae\":\"Speakers\"}]}}}", "Nmae")]
    [InlineData("{\"DeviceSwitching\":{\"Input\":{\"CycleDevices\":[{\"DisplayOrder\":2}]}}}", "DisplayOrder")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Name\":\"One\",\"ScheduleDay\":null}]}}", "ScheduleDay")]
    [InlineData("{\"Routines\":{\"Items\":[{\"LastRunStatusText\":\"Success\"}]}}", "LastRunStatusText")]
    public void SettingsImports_RejectUnknownAndNonPersistedNestedProperties(string json, string property)
    {
        var current = new Settings { App = new() { Theme = AppTheme.Dark } };
        string original = SettingsTransferService.SerializeSettings(current);
        foreach (bool replace in new[] { false, true })
        {
            JsonObject document = JsonNode.Parse(json)!.AsObject();
            document[nameof(Settings.SchemaVersion)] = Settings.CurrentSchemaVersion;
            JsonException error = Assert.Throws<JsonException>(() => SettingsTransferService.ParseImportedSettings(document.ToJsonString(), current, replace));
            Assert.Contains(property, error.Message, StringComparison.Ordinal);
            Assert.Equal(original, SettingsTransferService.SerializeSettings(current));
        }
    }

    [Theory]
    [InlineData("{\"Triggers\":[{\"Kind\":999}]}")]
    [InlineData("{\"Triggers\":[{\"ApplicationMode\":\"999\",\"Kind\":\"Application\"}]}")]
    [InlineData("{\"Triggers\":[{\"TitleMatchMode\":999,\"Kind\":\"Application\"}]}")]
    [InlineData("{\"Triggers\":[{\"NetworkDirection\":-1,\"Kind\":\"Application\"}]}")]
    [InlineData("{\"Triggers\":[{\"Days\":[0,7],\"Kind\":\"Scheduled\"}]}")]
    public void RoutineImports_RejectUndefinedEnumsInEveryEnvelope(string json)
    {
        foreach (string payload in new[] { json, $"[{json}]", $"{{\"Routine\":{json}}}", $"{{\"Routines\":[{json}]}}" })
        {
            Assert.Throws<JsonException>(() => RoutineTransferService.ParseSingleRoutine(payload));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsImports_AcceptDefinedEnumsAndPreserveTheirExportRepresentation(bool replace)
    {
        const string json = """
        {
          "SchemaVersion": "1.0.0",
          "Overlay": {
            "Position": 5
          },
          "Routines": {
            "Items": [
              {
                "Name": "音楽 \uD83C\uDFA7",
                "Triggers": [
                  {
                    "Kind": 5,
                    "Days": [
                      "sUnDaY",
                      "Saturday"
                    ]
                  }
                ]
              }
            ]
          },
          "App": {
            "theme": "dArK"
          },
          "Diagnostics": {
            "DeviceReferenceFileMode": "2"
          }
        }
        """;
        Settings settings = SettingsTransferService.ParseImportedSettings(json, new Settings(), replace);

        Assert.Equal(AppTheme.Dark, settings.App.Theme);
        Assert.Equal(OverlayPosition.BottomRight, settings.Overlay.Position);
        Assert.Equal(DeviceReferenceFileMode.Hashed, settings.Diagnostics.DeviceReferenceFileMode);
        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(RoutineTriggerKind.Scheduled, routine.TriggerKind);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], routine.ScheduleDays);
        using JsonDocument exported = JsonDocument.Parse(SettingsTransferService.SerializeSettings(settings));
        Assert.Equal("Dark", exported.RootElement.GetProperty("App").GetProperty("Theme").GetString());
        Assert.Equal("BottomRight", exported.RootElement.GetProperty("Overlay").GetProperty("Position").GetString());
        Assert.Equal("Scheduled", exported.RootElement.GetProperty("Routines").GetProperty("Items")[0].GetProperty("Triggers")[0].GetProperty("Kind").GetString());
        Assert.Equal("Saturday", exported.RootElement.GetProperty("Routines").GetProperty("Items")[0].GetProperty("Triggers")[0].GetProperty("Days")[1].GetString());
    }

    [Fact]
    public void SettingsExport_RejectsUndefinedEnums()
    {
        var settings = new Settings { App = new() { Theme = (AppTheme)999 } };

        Assert.Throws<JsonException>(() => SettingsTransferService.SerializeSettings(settings));
    }

    [Theory]
    [InlineData("{\"App\":{\"Theme\":\"Light\",\"Theme\":\"Dark\"}}")]
    [InlineData("{\"App\":{\"Theme\":\"Light\",\"theme\":\"Dark\"}}")]
    [InlineData("{\"Overlay\":{\"Enabled\":true,\"enabled\":false}}")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Name\":\"One\",\"name\":\"Two\"}]}}")]
    public void SettingsImports_RejectAmbiguousProperties(string json)
    {
        Assert.ThrowsAny<JsonException>(() => SettingsTransferService.ParseImportedSettings(json, new Settings(), replaceImport: false));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Settings>(json, SettingsJson.Options));
    }

    [Theory]
    [InlineData("{\"Name\":12}")]
    [InlineData("{\"Name\":\"One\",\"name\":\"Two\"}")]
    [InlineData("{\"Triggers\":[{\"Time\":\"25:00:00\",\"Kind\":\"Scheduled\"}]}")]
    [InlineData("{\"Triggers\":[{\"Kind\":\"UnknownTrigger\"}]}")]
    [InlineData("{\"MasterVolumePercent\":\"50\"}")]
    public void RoutineImports_RejectInvalidTypesAndAmbiguousProperties(string json)
    {
        Assert.ThrowsAny<JsonException>(() => RoutineTransferService.ParseSingleRoutine(json));
    }

    [Fact]
    public void MergeSettings_PreservesOmittedAndNullValuesWithoutMutatingCurrentSettings()
    {
        var current = new Settings
        {
            Overlay = new OverlaySettings { Enabled = true, DurationSeconds = 4 },
            App = new() { Theme = AppTheme.Dark }
        };
        const string json = """
        {"overlay":{"enabled":false,"DurationSeconds":null},"App":{"Theme":null}}
        """;

        Settings imported = SettingsTransferService.ParseImportedSettings(json, current, replaceImport: false);

        Assert.Equal(AppTheme.Dark, imported.App.Theme);
        Assert.False(imported.Overlay.Enabled);
        Assert.Equal(4, imported.Overlay.DurationSeconds);
        Assert.True(current.Overlay.Enabled);
        Assert.NotSame(current.Overlay, imported.Overlay);
    }

    [Fact]
    public void ReplaceSettings_NormalizesNullSectionsAndReplacesDefaultCollections()
    {
        const string json = """
        {"SchemaVersion":"1.0.0","Routines":null,"Hotkeys":null,"DeviceSwitching":{"Output":{"SwitchRoles":["Console"]}}}
        """;

        Settings imported = SettingsTransferService.ParseImportedSettings(json, new Settings(), replaceImport: true);

        Assert.Empty(imported.Routines.Items);
        Assert.NotNull(imported.Hotkeys.App);
        Assert.Equal(["Console"], imported.DeviceSwitching.Output.SwitchRoles);
    }

    [Fact]
    public void SettingsClone_PreservesExtensionDataTypesAndUnicode()
    {
        const string json = """
        {"SchemaVersion":"99.0.0","Future":{"Text":"音楽 🎧","Date":"2026-09-07T12:00:00Z","Number":1234567890123456789,"Nothing":null}}
        """;
        Settings source = JsonSerializer.Deserialize<Settings>(json, SettingsJson.Options)!;
        Settings clone = source.Clone();
        source.ExtensionData!.Clear();

        JsonElement future = clone.ExtensionData!["Future"];
        Assert.Equal("音楽 🎧", future.GetProperty("Text").GetString());
        Assert.Equal(JsonValueKind.String, future.GetProperty("Date").ValueKind);
        Assert.Equal(1234567890123456789, future.GetProperty("Number").GetInt64());
        Assert.Equal(JsonValueKind.Null, future.GetProperty("Nothing").ValueKind);
        using JsonDocument exported = JsonDocument.Parse(SettingsTransferService.SerializeSettings(clone));
        Assert.True(JsonElement.DeepEquals(future, exported.RootElement.GetProperty("Future")));
    }

    [Theory]
    [InlineData("{\"Overlay\":{\"Enabled\":true,\"Enabl\\u0065d\":false}}")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Name\":\"One\",\"n\\u0061me\":\"Two\"}]}}")]
    public void SettingsImports_RejectEscapedDuplicateNames(string json)
    {
        Assert.Throws<JsonException>(() => SettingsTransferService.ParseImportedSettings(json, new Settings(), replaceImport: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedCollections_RemainIndependentAndUsableAfterDocumentDisposal(bool replaceImport)
    {
        var current = new Settings
        {
            Routines = new RoutinesSettings { Items = [new AudioRoutine { Id = "old", Name = "Original" }] },
        };
        const string json = """
        {
          "SchemaVersion": "1.0.0",
          "routines": {
            "items": [
              {
                "Id": "new",
                "Name": "音楽 🎧",
                "Triggers": [
                  {
                    "Days": [
                      "Monday",
                      "Friday"
                    ],
                    "Kind": "Scheduled"
                  }
                ]
              }
            ]
          }
        }
        """;

        Settings imported = SettingsTransferService.ParseImportedSettings(json, current, replaceImport);
        AudioRoutine routine = Assert.Single(imported.Routines.Items);
        routine.ScheduleDays.Add(DayOfWeek.Saturday);
        string exported = SettingsTransferService.SerializeSettings(imported);
        Settings reimported = SettingsTransferService.ParseImportedSettings(exported, null, replaceImport: true);

        Assert.Equal("音楽 🎧", Assert.Single(reimported.Routines.Items).Name);
        Assert.Contains(DayOfWeek.Saturday, reimported.Routines.Items[0].ScheduleDays);
        Assert.Equal("Original", Assert.Single(current.Routines.Items).Name);
        Assert.Empty(current.Routines.Items[0].ScheduleDays);
    }

    [Fact]
    public void RoutineTrigger_SerializesOnlyKindRelevantFields_AndRejectsIntegerDayOfWeek()
    {
        var appRoutine = new AudioRoutine
        {
            Id = "app-routine",
            Name = "Game",
            MasterVolumePercent = 60,
            Triggers = [new RoutineTrigger { Id = "t1", Kind = RoutineTriggerKind.Application, AppPath = @"C:\Apps\Game.exe" }],
        };
        using JsonDocument appDoc = JsonDocument.Parse(JsonSerializer.Serialize(appRoutine, SettingsJson.Options));
        JsonElement appTrigger = appDoc.RootElement.GetProperty("Triggers")[0];
        Assert.Equal("Application", appTrigger.GetProperty("Kind").GetString());
        Assert.Equal(@"C:\Apps\Game.exe", appTrigger.GetProperty("AppPath").GetString());
        Assert.False(appTrigger.TryGetProperty("TimeZoneId", out _));
        Assert.False(appTrigger.TryGetProperty("Time", out _));
        Assert.False(appTrigger.TryGetProperty("Days", out _));
        Assert.False(appTrigger.TryGetProperty("NetworkName", out _));
        Assert.False(appTrigger.TryGetProperty("Device", out _));
        Assert.False(appDoc.RootElement.TryGetProperty("MicVolumePercent", out _));

        var scheduledRoutine = new AudioRoutine
        {
            Id = "sched-routine",
            Name = "Evening",
            MasterVolumePercent = 40,
            Triggers = [new RoutineTrigger { Id = "t2", Kind = RoutineTriggerKind.Scheduled, Time = new TimeOnly(21, 0), Days = [DayOfWeek.Monday], TimeZoneId = "UTC" }],
        };
        using JsonDocument schedDoc = JsonDocument.Parse(JsonSerializer.Serialize(scheduledRoutine, SettingsJson.Options));
        JsonElement schedTrigger = schedDoc.RootElement.GetProperty("Triggers")[0];
        Assert.Equal("UTC", schedTrigger.GetProperty("TimeZoneId").GetString());
        Assert.Equal("Monday", schedTrigger.GetProperty("Days")[0].GetString());
        Assert.False(schedTrigger.TryGetProperty("AppPath", out _));
        Assert.False(schedTrigger.TryGetProperty("NetworkName", out _));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoutineTrigger>("""{"Kind":"Scheduled","Days":[1]}""", SettingsJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoutineTrigger>("""{"Kind":"Scheduled","Days":["Monday, Tuesday"]}""", SettingsJson.Options));
    }
}
