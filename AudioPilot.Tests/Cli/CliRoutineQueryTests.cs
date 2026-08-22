using System.Text.Json;
using System.Text.Json.Nodes;
using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliRoutineQueryTests
{
    [Theory]
    [InlineData("get", CliAction.RoutineGet)]
    [InlineData("next", CliAction.RoutineNext)]
    public void RoutineQuery_ParsesAndSurvivesForwarding(string operation, CliAction expectedAction)
    {
        Assert.True(CliCommand.TryParse(["routine", operation, "Desk setup", "--json", "--redact"], out CliCommand command, out string? error), error);
        Assert.True(CliCommand.TryFromPipePayload(command.ToPipePayload(), out CliCommand forwarded));
        Assert.Equal(expectedAction, forwarded.Action);
        Assert.Equal("Desk setup", forwarded.Key);
        Assert.True(forwarded.JsonOutput);
        Assert.True(forwarded.RedactOutput);
    }

    [Theory]
    [InlineData("routine", "get")]
    [InlineData("routine", "get", "desk", "unexpected")]
    [InlineData("routine", "get", "desk", "--replace")]
    [InlineData("routine", "next")]
    [InlineData("routine", "next", "desk", "unexpected")]
    public void RoutineGet_RejectsInvalidArguments(params string[] args)
    {
        Assert.False(CliCommand.TryParse(args, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(" routine-1 ", "routine-1")]
    [InlineData("DESK", "routine-1")]
    [InlineData("shared", "shared")]
    public async Task RoutineGet_ResolvesIdBeforeNameWithoutExecuting(string selector, string expectedId)
    {
        var runtime = new FakeRuntime
        {
            Routines =
            [
                new AudioRoutine { Id = "first", Name = "shared" },
                new AudioRoutine { Id = "second", Name = "shared" },
                new AudioRoutine { Id = "routine-1", Name = "Desk", Enabled = false },
                new AudioRoutine { Id = "shared", Name = "Other" },
            ]
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineGet, Key = selector, JsonOutput = true }, runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedId, JsonNode.Parse(result.Output!)!["data"]!["id"]!.GetValue<string>());
        Assert.Null(runtime.LastRoutineSelector);
        Assert.False(runtime.RefreshCalled);
    }

    [Theory]
    [InlineData("Private duplicate", 5, "routine-selector-ambiguous")]
    [InlineData("Private missing", 5, "routine-not-found")]
    [InlineData("'Private title'", 5, "routine-not-found")]
    [InlineData(" ", 2, "missing-routine-selector")]
    public async Task RoutineGet_ReturnsRedactedErrors(string selector, int exitCode, string code)
    {
        var runtime = new FakeRuntime
        {
            Routines = [new AudioRoutine { Name = "Private duplicate" }, new AudioRoutine { Name = "Private duplicate" }]
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineGet, Key = selector, JsonOutput = true, RedactOutput = true }, runtime);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, JsonNode.Parse(result.Output!)!["data"]!["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("Private", result.Output);
        Assert.Null(runtime.LastRoutineSelector);
    }

    [Theory]
    [InlineData("disabled", "routine-disabled")]
    [InlineData("unscheduled", "routine-not-scheduled")]
    [InlineData("no-targets", "routine-has-no-targets")]
    public async Task RoutineNext_RejectsInactiveSchedulesWithoutExecuting(string state, string expectedCode)
    {
        var runtime = new FakeRuntime
        {
            Routines = [new AudioRoutine
            {
                Id = "routine-1", Enabled = state != "disabled", TriggerKind = state == "unscheduled" ? RoutineTriggerKind.Hotkey : RoutineTriggerKind.Scheduled,
                MasterVolumePercent = state == "no-targets" ? null : 30,
            }]
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.RoutineNext, Key = "routine-1", JsonOutput = true }, runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(expectedCode, JsonNode.Parse(result.Output!)!["data"]!["error"]!["code"]!.GetValue<string>());
        Assert.Null(runtime.LastRoutineSelector);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoutineNext_OutputIncludesUnambiguousTimesAndOptionalReminder(bool reminder)
    {
        var routine = new AudioRoutine { Id = "routine-1", Name = "Private morning", TriggerKind = RoutineTriggerKind.Scheduled, NotifyBeforeScheduledRun = reminder };
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        DateTime occurrence = new(2026, 3, 8, 10, 0, 0, DateTimeKind.Utc);

        using JsonDocument document = JsonDocument.Parse(CliOutputFormatter.FormatRoutineNextOccurrence(routine, zone, occurrence, jsonOutput: true, redactOutput: true));
        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal("routine-1", data.GetProperty("id").GetString());
        Assert.DoesNotContain("Private", data.ToString());
        Assert.Equal("2026-03-08T10:00:00.0000000Z", data.GetProperty("occurrenceUtc").GetString());
        Assert.Equal("2026-03-08T03:00:00.0000000-07:00", data.GetProperty("occurrenceLocal").GetString());
        Assert.Equal(zone.Id, data.GetProperty("timeZoneId").GetString());
        Assert.Equal(reminder ? "2026-03-08T09:59:00.0000000Z" : null, data.GetProperty("reminderUtc").GetString());
        string text = CliOutputFormatter.FormatRoutineNextOccurrence(routine, zone, occurrence, jsonOutput: false, redactOutput: true);
        Assert.Contains("-07:00", text);
        Assert.DoesNotContain("Private", text);
        Assert.Equal(reminder, text.Contains("Reminder UTC", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, RoutineTriggerKind.Application)]
    [InlineData(true, RoutineTriggerKind.Application)]
    [InlineData(false, RoutineTriggerKind.Network)]
    [InlineData(true, RoutineTriggerKind.Network)]
    [InlineData(false, RoutineTriggerKind.Scheduled)]
    [InlineData(true, RoutineTriggerKind.Scheduled)]
    [InlineData(false, RoutineTriggerKind.DeviceChange)]
    [InlineData(true, RoutineTriggerKind.DeviceChange)]
    public void RoutineDetails_RedactsSensitiveFieldsWithoutChangingConfiguration(bool jsonOutput, RoutineTriggerKind triggerKind)
    {
        var routine = new AudioRoutine
        {
            TriggerKind = triggerKind,
            ApplicationTriggerMode = ApplicationTriggerMode.ProcessFocus,
            Id = "routine-1",
            Name = "Private routine",
            OutputDeviceId = "Private output ID",
            OutputDeviceName = "Private speakers",
            InputDeviceId = "Private input ID",
            InputDeviceName = "Private microphone",
            TriggerAppPath = @"C:\Private\app.exe",
            ApplicationTriggerTitlePattern = "Private title",
            TriggerNetworkName = "Private network",
            MasterVolumePercent = 35,
            MicVolumePercent = 45,
            ScheduleDays = [DayOfWeek.Friday, DayOfWeek.Monday],
            ScheduleTime = new TimeOnly(7, 5),
            NotifyBeforeScheduledRun = triggerKind == RoutineTriggerKind.Scheduled,
            EnforceTargetsOnDeviceChange = triggerKind == RoutineTriggerKind.DeviceChange,
        };
        JsonObject original = JsonSerializer.SerializeToNode(routine)!.AsObject();

        string redacted = CliOutputFormatter.FormatRoutineDetails(routine, jsonOutput, redactOutput: true);
        string raw = CliOutputFormatter.FormatRoutineDetails(routine, jsonOutput);

        Assert.DoesNotContain("Private", redacted);
        if (triggerKind == RoutineTriggerKind.Application)
        {
            Assert.Contains("Private title", raw);
            Assert.Contains("Private", routine.TriggerAppPath);
        }
        if (triggerKind == RoutineTriggerKind.Network)
        {
            Assert.Contains("Private network", raw);
        }
        Assert.True(JsonNode.DeepEquals(original, JsonSerializer.SerializeToNode(routine)));
        if (jsonOutput)
        {
            JsonObject data = (JsonObject)JsonNode.Parse(redacted)!["data"]!;
            Assert.Equal(original.Select(static p => p.Key).Order(StringComparer.OrdinalIgnoreCase),
                data.Select(static p => p.Key).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(35, data["masterVolumePercent"]!.GetValue<int>());
            Assert.Equal(45, data["micVolumePercent"]!.GetValue<int>());
            Assert.Equal(triggerKind == RoutineTriggerKind.Scheduled, data["notifyBeforeScheduledRun"]!.GetValue<bool>());
            Assert.Equal(triggerKind == RoutineTriggerKind.DeviceChange, data["enforceTargetsOnDeviceChange"]!.GetValue<bool>());
            Assert.Equal("07:05", data["scheduleTime"]!.GetValue<string>());
            Assert.Equal(["Monday", "Friday"], data["scheduleDays"]!.AsArray().Select(static value => value!.GetValue<string>()));
        }
    }
}
