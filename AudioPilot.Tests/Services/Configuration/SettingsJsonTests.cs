using System.Text.Json;
using System.Text.Json.Nodes;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class SettingsJsonTests
{
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
            TriggerKind = trigger,
            TriggerAppPath = @"C:\Apps\Player.exe",
            SwitchOutputPerApp = true,
            ApplicationTriggerMode = trigger == RoutineTriggerKind.Application ? ApplicationTriggerMode.ProcessFocus : ApplicationTriggerMode.AppLaunch,
            ApplicationTriggerTitlePattern = "voice|stream",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
            RestorePreviousAudioOnDeactivate = true,
            ShowInTrayMenu = true,
            EnforceTargetsOnDeviceChange = trigger == RoutineTriggerKind.DeviceChange,
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
    [InlineData("{'Theme':'Light'}")]
    [InlineData("{Theme:\"Light\"}")]
    [InlineData("{\"Theme\":\"Light\",}")]
    [InlineData("{/*comment*/\"Theme\":\"Light\"}")]
    [InlineData("{\"RunAtStartup\":\"true\"}")]
    [InlineData("{\"RunAtStartup\":null}")]
    [InlineData("{\"Theme\":\"UnknownTheme\"}")]
    [InlineData("{\"AdvancedTuning\":{\"BluetoothReconnect\":{\"MaxAttempts\":\"2\"}}}")]
    public void SettingsDeserialize_RejectsInvalidSyntaxAndTypes(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Settings>(json, SettingsJson.Options));
    }

    [Theory]
    [InlineData("{\"Theme\":999}", "$.Theme")]
    [InlineData("{\"Theme\":-1}", "$.Theme")]
    [InlineData("{\"Theme\":\"999\"}", "$.Theme")]
    [InlineData("{\"Overlay\":{\"Position\":999}}", "$.Overlay.Position")]
    [InlineData("{\"Miscellaneous\":{\"DeviceReferenceFileMode\":999}}", "$.Miscellaneous.DeviceReferenceFileMode")]
    [InlineData("{\"Routines\":{\"Items\":[{\"TriggerKind\":999}]}}", "$.Routines.Items[0].TriggerKind")]
    [InlineData("{\"Routines\":{\"Items\":[{\"ApplicationTriggerMode\":999}]}}", "$.Routines.Items[0].ApplicationTriggerMode")]
    [InlineData("{\"Routines\":{\"Items\":[{\"ApplicationTriggerTitleMatchMode\":999}]}}", "$.Routines.Items[0].ApplicationTriggerTitleMatchMode")]
    [InlineData("{\"Routines\":{\"Items\":[{\"NetworkTriggerDirection\":999}]}}", "$.Routines.Items[0].NetworkTriggerDirection")]
    [InlineData("{\"Routines\":{\"Items\":[{\"ScheduleDays\":[7]}]}}", "$.Routines.Items[0].ScheduleDays[0]")]
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
    [InlineData("{\"Hotkeys\":{\"Global\":{\"AdditionalStandaloneKey\":null}}}", "AdditionalStandaloneKey")]
    [InlineData("{\"DeviceSwitching\":{\"Output\":{\"CycleDevices\":[{\"Id\":\"one\",\"Nmae\":\"Speakers\"}]}}}", "Nmae")]
    [InlineData("{\"DeviceSwitching\":{\"Input\":{\"CycleDevices\":[{\"DisplayOrder\":2}]}}}", "DisplayOrder")]
    [InlineData("{\"Routines\":{\"Items\":[{\"Name\":\"One\",\"ScheduleDay\":null}]}}", "ScheduleDay")]
    [InlineData("{\"Routines\":{\"Items\":[{\"LastRunStatusText\":\"Success\"}]}}", "LastRunStatusText")]
    public void SettingsImports_RejectUnknownAndNonPersistedNestedProperties(string json, string property)
    {
        var current = new Settings { Theme = AppTheme.Dark };
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
    [InlineData("{\"TriggerKind\":999}")]
    [InlineData("{\"ApplicationTriggerMode\":\"999\"}")]
    [InlineData("{\"ApplicationTriggerTitleMatchMode\":999}")]
    [InlineData("{\"NetworkTriggerDirection\":-1}")]
    [InlineData("{\"ScheduleDays\":[0,7]}")]
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
        {"SchemaVersion":"1.0.0","theme":"dArK","Overlay":{"Position":5},"Miscellaneous":{"DeviceReferenceFileMode":"2"},"Routines":{"Items":[{"TriggerKind":5,"Name":"音楽 🎧","ScheduleDays":[0,6]}]}}
        """;
        Settings settings = SettingsTransferService.ParseImportedSettings(json, new Settings(), replace);

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(OverlayPosition.BottomRight, settings.Overlay.Position);
        Assert.Equal(DeviceReferenceFileMode.Hashed, settings.Miscellaneous.DeviceReferenceFileMode);
        AudioRoutine routine = Assert.Single(settings.Routines.Items);
        Assert.Equal(RoutineTriggerKind.Scheduled, routine.TriggerKind);
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], routine.ScheduleDays);
        using JsonDocument exported = JsonDocument.Parse(SettingsTransferService.SerializeSettings(settings));
        Assert.Equal("Dark", exported.RootElement.GetProperty("Theme").GetString());
        Assert.Equal("BottomRight", exported.RootElement.GetProperty("Overlay").GetProperty("Position").GetString());
        Assert.Equal("Scheduled", exported.RootElement.GetProperty("Routines").GetProperty("Items")[0].GetProperty("TriggerKind").GetString());
        Assert.Equal(6, exported.RootElement.GetProperty("Routines").GetProperty("Items")[0].GetProperty("ScheduleDays")[1].GetInt32());
    }

    [Fact]
    public void SettingsExport_RejectsUndefinedEnums()
    {
        var settings = new Settings { Theme = (AppTheme)999 };

        Assert.Throws<JsonException>(() => SettingsTransferService.SerializeSettings(settings));
    }

    [Theory]
    [InlineData("{\"Theme\":\"Light\",\"Theme\":\"Dark\"}")]
    [InlineData("{\"Theme\":\"Light\",\"theme\":\"Dark\"}")]
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
    [InlineData("{\"ScheduleTime\":\"25:00:00\"}")]
    [InlineData("{\"TriggerKind\":\"UnknownTrigger\"}")]
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
            Theme = AppTheme.Dark,
            Overlay = new OverlaySettings { Enabled = true, DurationSeconds = 4 },
        };
        const string json = """{"Theme":null,"overlay":{"enabled":false,"DurationSeconds":null}}""";

        Settings imported = SettingsTransferService.ParseImportedSettings(json, current, replaceImport: false);

        Assert.Equal(AppTheme.Dark, imported.Theme);
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
        {"SchemaVersion":"1.0.0","routines":{"items":[{"Id":"new","Name":"音楽 🎧","ScheduleDays":[1,5],"TriggerKind":"Scheduled"}]}}
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
}
