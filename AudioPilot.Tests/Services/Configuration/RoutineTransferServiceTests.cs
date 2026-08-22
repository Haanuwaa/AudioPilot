using System.Text.Json;
using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class RoutineTransferServiceTests
{
    [Theory]
    [InlineData("TriggerKind", "\"Application\"")]
    [InlineData("AdditionalTriggers", "[]")]
    [InlineData("TriggerAppPath", "\"C:\\\\Apps\\\\Game.exe\"")]
    public void Imports_RejectRemovedTopLevelTriggerFields(string field, string value)
    {
        Assert.Throws<InvalidDataException>(() => RoutineTransferService.ParseSingleRoutine("{\"" + field + "\":" + value + "}"));
    }

    [Fact]
    public void Serialization_UsesOneCollectionAndManualOnlyUsesAnEmptyCollection()
    {
        var routine = new AudioRoutine
        {
            MasterVolumePercent = 45,
            Triggers = [new() { Id = "first", Kind = RoutineTriggerKind.SteamBigPicture }, new() { Id = "second", Kind = RoutineTriggerKind.AudioPilotStartup }],
            Conditions = new() { Device = new() { Id = "headset" }, DeviceAvailable = false },
        };
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(routine, SettingsJson.Options));
        Assert.False(serialized.RootElement.TryGetProperty("TriggerKind", out _));
        Assert.False(serialized.RootElement.TryGetProperty("AdditionalTriggers", out _));
        Assert.Equal(2, serialized.RootElement.GetProperty("Triggers").GetArrayLength());
        var imported = RoutineTransferService.ParseSingleRoutine(serialized.RootElement.GetRawText());
        Assert.Equal(["first", "second"], imported.Triggers.Select(static trigger => trigger.Id));
        Assert.False(imported.Conditions.DeviceAvailable);
        imported.Triggers.Clear();
        imported.ShowInTrayMenu = true;
        Assert.Null(imported.ValidateTriggers());
        using var manual = JsonDocument.Parse(JsonSerializer.Serialize(imported, SettingsJson.Options));
        Assert.Equal(0, manual.RootElement.GetProperty("Triggers").GetArrayLength());
    }

    [Theory]
    [InlineData("{\"Conditions\":{\"Unknown\":true}}")]
    [InlineData("{\"Conditions\":{\"Device\":{\"Id\":\"a\",\"Unknown\":true}}}")]
    [InlineData("{\"Triggers\":[{\"Device\":{\"Id\":\"a\",\"Unknown\":true},\"Kind\":\"Application\"}]}")]
    [InlineData("{\"Triggers\":[{\"DeviceTransition\":99,\"Kind\":\"Application\"}]}")]
    public void Requirements_RejectUnknownFieldsAndInvalidEnums(string json)
    {
        Assert.Throws<JsonException>(() => RoutineTransferService.ParseSingleRoutine(json));
    }

    [Fact]
    public void Triggers_SurviveImportAndNormalization()
    {
        const string json = """
            {"Name":"Desk", "MasterVolumePercent":35, "Triggers":[{"Id":"steam","Kind":"SteamBigPicture"}], "RestorePreviousAudioOnDeactivate":true}
            """;
        AudioRoutine routine = RoutineTransferService.ParseSingleRoutine(json);
        var settings = new Settings { Routines = new RoutinesSettings { Items = [routine] } };
        SettingsValidationService.Normalize(settings);
        Assert.Equal("steam", Assert.Single(settings.Routines.Items[0].Triggers).Id);
        Assert.True(settings.Routines.Items[0].RestorePreviousAudioOnDeactivate);
    }

    [Fact]
    public void Triggers_RejectUnknownNestedProperties()
    {
        Assert.Throws<JsonException>(() => RoutineTransferService.ParseSingleRoutine("""
            {"Name":"Desk","Triggers":[{"Kind":"SteamBigPicture","Unexpected":true}]}
            """));
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":\"0.9.0\",\"Routines\":[{\"Name\":\"Desk\"}]}")]
    [InlineData("{\"SchemaVersion\":\"0.9.0\",\"Routine\":{\"Name\":\"Desk\"}}")]
    [InlineData("{\"SchemaVersion\":\"1.0.0\",\"Routines\":[],\"Routine\":{\"Name\":\"Desk\"}}")]
    [InlineData("{\"SchemaVersion\":\"1.0.0\",\"Routines\":[{\"Name\":\"Desk\"}],\"Unexpected\":null}")]
    [InlineData("{\"Routine\":{\"Name\":\"Desk\"},\"Unexpected\":true}")]
    public void ParseRoutineCollection_RejectsUnsupportedEnvelope(string json)
    {
        Assert.Throws<InvalidDataException>(() => RoutineTransferService.ParseRoutineCollection(json));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"1.0.0\",\"routine\":{\"name\":\"Desk\"}}")]
    [InlineData("{\"routines\":[{\"name\":\"Desk\"}]}")]
    public void ParseRoutineCollection_AcceptsSupportedCaseInsensitiveEnvelope(string json)
    {
        Assert.Equal("Desk", Assert.Single(RoutineTransferService.ParseRoutineCollection(json)).Name);
    }

    [Fact]
    public void ScheduledReminder_SurvivesTransferAndValidation()
    {
        const string json = """
        {
          "Id": "reminder",
          "Name": "Bedtime",
          "OutputDeviceId": "out-1",
          "Triggers": [
            {
              "Kind": "Scheduled",
              "Time": "21:00:00",
              "NotifyBeforeRun": true
            }
          ]
        }
        """;
        AudioRoutine imported = RoutineTransferService.ParseSingleRoutine(json);
        Assert.True(imported.NotifyBeforeScheduledRun);
        var settings = new Settings();
        settings.Routines.Items = [imported];
        SettingsValidationService.Normalize(settings);
        Assert.True(Assert.Single(settings.Routines.Items).NotifyBeforeScheduledRun);
        string exported = JsonSerializer.Serialize(settings.Routines.Items[0].Clone());
        Assert.True(RoutineTransferService.ParseSingleRoutine(exported).NotifyBeforeScheduledRun);
    }

    [Fact]
    public void ParseSingleRoutine_ParsesRawRoutineObject()
    {
        const string json = """
        {
          "Name": "Desk",
          "Enabled": true,
          "OutputDeviceId": "out-1",
          "OutputDeviceName": "Speakers",
          "Hotkey": "Ctrl+Alt+D"
        }
        """;

        AudioRoutine routine = RoutineTransferService.ParseSingleRoutine(json);

        Assert.Equal("Desk", routine.Name);
        Assert.Equal("out-1", routine.OutputDeviceId);
        Assert.Equal(RoutineTriggerKind.Hotkey, routine.TriggerKind);
    }

    [Fact]
    public void ParseRoutineCollection_ParsesEnvelopeDocument()
    {
        const string json = """
        {
            "SchemaVersion": "1.0.0",
            "Routines": [
                {
                    "Id": "routine-1",
                    "Name": "Desk",
                    "Enabled": true,
                    "OutputDeviceId": "out-1",
                    "OutputDeviceName": "Speakers"
                }
            ]
        }
        """;

        List<AudioRoutine> routines = RoutineTransferService.ParseRoutineCollection(json);

        AudioRoutine routine = Assert.Single(routines);
        Assert.Equal("routine-1", routine.Id);
        Assert.Equal("Desk", routine.Name);
    }

    [Fact]
    public void ParseRoutineCollection_ParsesProcessFocusApplicationFields()
    {
        const string json = """
        {
          "SchemaVersion": "1.0.0",
          "Routines": [
            {
              "Id": "routine-focus",
              "Name": "Discord Focus",
              "Enabled": true,
              "OutputDeviceId": "out-1",
              "OutputDeviceName": "Speakers",
              "SwitchOutputPerApp": true,
              "Triggers": [
                {
                  "Kind": "Application",
                  "AppPath": "C:\\Users\\ExampleUser\\AppData\\Local\\Discord\\Update.exe",
                  "ApplicationMode": "ProcessFocus",
                  "TitlePattern": "voice|stream",
                  "TitleMatchMode": "Regex"
                }
              ]
            }
          ]
        }
        """;

        AudioRoutine routine = Assert.Single(RoutineTransferService.ParseRoutineCollection(json));

        Assert.Equal("routine-focus", routine.Id);
        Assert.Equal(RoutineTriggerKind.Application, routine.TriggerKind);
        Assert.Equal(ApplicationTriggerMode.ProcessFocus, routine.ApplicationTriggerMode);
        Assert.Equal("voice|stream", routine.ApplicationTriggerTitlePattern);
        Assert.Equal(ApplicationTriggerTitleMatchMode.Regex, routine.ApplicationTriggerTitleMatchMode);
        Assert.True(routine.SwitchOutputPerApp);
    }

    [Fact]
    public void ParseRoutineCollection_ThrowsForUnsupportedProperty()
    {
        const string json = """
        {
            "Name": "Desk",
            "Unexpected": true
        }
        """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => RoutineTransferService.ParseRoutineCollection(json));

        Assert.Contains("unsupported property", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRoutineCollection_ThrowsForRemovedTimingProperty()
    {
        const string json = """
        {
            "Name": "Desk",
            "Enabled": true,
            "ExecutionDelayMs": 250
        }
        """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => RoutineTransferService.ParseRoutineCollection(json));

        Assert.Contains("ExecutionDelayMs", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsupported property", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRoutineCollection_ThrowsForComputedRoutineProperty()
    {
        const string json = """
        {
            "Name": "Desk",
            "TriggerSummary": "Hotkey: Ctrl+Alt+D"
        }
        """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => RoutineTransferService.ParseRoutineCollection(json));

        Assert.Contains("unsupported property", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
