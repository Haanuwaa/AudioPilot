using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Configuration;

public sealed class RoutineTransferServiceTests
{
    [Fact]
    public void ParseSingleRoutine_ParsesRawRoutineObject()
    {
        const string json = """
        {
            "Name": "Desk",
            "Enabled": true,
            "OutputDeviceId": "out-1",
            "OutputDeviceName": "Speakers",
            "TriggerKind": "Hotkey",
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
    public void ParseRoutineCollection_RoundTripsProcessFocusApplicationFields()
    {
        var source = new AudioRoutine
        {
            Id = "routine-focus",
            Name = "Discord Focus",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
            SwitchOutputPerApp = true,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            ApplicationTriggerTitlePattern = "voice|stream",
            ApplicationTriggerTitleMatchMode = ApplicationTriggerTitleMatchMode.Regex,
        };

        string json = RoutineTransferService.SerializeRoutines([source]);

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
