using AudioPilot.Cli;
using AudioPilot.Logging;
using AudioPilot.Models;
using Newtonsoft.Json.Linq;
using Windows.Media.Control;

namespace AudioPilot.Tests.Cli;

public sealed class CliOutputFormatterTests
{
    [Fact]
    public void FormatMediaStatus_Json_UsesCliEnvelopeShape()
    {
        string json = CliOutputFormatter.FormatMediaStatus(
            new MediaOverlaySessionSnapshot(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                "Fixture Title",
                "Fixture Artist",
                "Fixture Album",
                "fixture-source",
                42),
            jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.True(data["hasSession"]?.Value<bool>());
        Assert.Equal("Playing", data["playbackStatus"]?.Value<string>());
        Assert.Equal("Fixture Title", data["title"]?.Value<string>());
        Assert.Equal("Fixture Artist", data["artist"]?.Value<string>());
        Assert.Equal("Fixture Album", data["albumTitle"]?.Value<string>());
        Assert.Equal("fixture-source", data["sourceAppUserModelId"]?.Value<string>());
        Assert.Equal(42, data["positionSeconds"]?.Value<long>());
    }

    [Fact]
    public void FormatMediaStatus_JsonRedact_RedactsUserContent()
    {
        string json = CliOutputFormatter.FormatMediaStatus(
            new MediaOverlaySessionSnapshot(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                "Fixture Title",
                "Fixture Artist",
                "Fixture Album",
                "fixture-source",
                7),
            jsonOutput: true,
            redactOutput: true);

        JObject data = Assert.IsType<JObject>(JObject.Parse(json)["data"]);
        Assert.True(data["hasSession"]?.Value<bool>());
        Assert.StartsWith("media[", data["title"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("media[", data["artist"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("media[", data["albumTitle"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("source[", data["sourceAppUserModelId"]?.Value<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture Title", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMediaStatus_TextWithoutSession_ReturnsNoCurrentMedia()
    {
        string text = CliOutputFormatter.FormatMediaStatus(MediaOverlaySessionSnapshot.Empty, jsonOutput: false);

        Assert.Equal("No current media", text);
    }

    [Fact]
    public void FormatMuteStatus_Json_UsesCliEnvelopeShape()
    {
        string json = CliOutputFormatter.FormatMuteStatus(target: "mic", enabled: true, jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.True(data["success"]?.Value<bool>());
        Assert.Equal("mic", data["target"]?.Value<string>());
        Assert.True(data["enabled"]?.Value<bool>());
        Assert.Equal("mute-mic-status", data["diagCode"]?.Value<string>());
    }

    [Fact]
    public void FormatVolumeResult_Json_UsesCliEnvelopeAndClampsPercent()
    {
        string json = CliOutputFormatter.FormatVolumeResult(
            kind: "master",
            percent: 150.4f,
            muted: true,
            jsonOutput: true,
            diagCode: "volume-get-success",
            deviceId: "out-1");

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.True(data["success"]?.Value<bool>());
        Assert.Equal("master", data["kind"]?.Value<string>());
        Assert.Equal("out-1", data["deviceId"]?.Value<string>());
        Assert.Equal(100, data["percent"]?.Value<int>());
        Assert.True(data["muted"]?.Value<bool>());
        Assert.Equal("volume-get-success", data["diagCode"]?.Value<string>());
    }

    [Fact]
    public void FormatVolumeError_Text_UsesDiagCodePrefix()
    {
        string text = CliOutputFormatter.FormatVolumeError(
            kind: "master",
            diagCode: "volume-get-failed",
            message: "No output device matched 'desk'.",
            jsonOutput: false,
            deviceId: "out-1");

        Assert.Equal("[diag-code:volume-get-failed] No output device matched 'desk'.", text);
    }

    [Fact]
    public void FormatDeviceGetResult_Json_UsesCliEnvelopeShape()
    {
        string json = CliOutputFormatter.FormatDeviceGetResult(
            kind: "output",
            device: new CycleDevice { Id = "out-3", Name = "Headset" },
            jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.Equal("output", data["kind"]?.Value<string>());
        Assert.Equal("device-get-success", data["diagCode"]?.Value<string>());
        Assert.Equal("out-3", data["device"]?["id"]?.Value<string>());
        Assert.Equal("Headset", data["device"]?["name"]?.Value<string>());
    }

    [Fact]
    public void FormatDeviceGetResult_JsonRedact_RedactsNameAndIdButPreservesDiagCode()
    {
        string json = CliOutputFormatter.FormatDeviceGetResult(
            kind: "output",
            device: new CycleDevice { Id = "out-3", Name = "Headset" },
            jsonOutput: true,
            redactOutput: true);

        JObject root = JObject.Parse(json);
        JToken data = Assert.IsType<JObject>(root["data"]);

        Assert.Equal("device-get-success", data["diagCode"]?.Value<string>());
        Assert.StartsWith("device-id[", data["device"]?["id"]?.Value<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("Headset", json, StringComparison.Ordinal);
        Assert.DoesNotContain("out-3", json, StringComparison.Ordinal);
        Assert.StartsWith("device[", data["device"]?["name"]?.Value<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCycleMutationResult_Json_UsesCliEnvelopeShape()
    {
        string json = CliOutputFormatter.FormatCycleMutationResult(
            kind: "output",
            action: "add",
            diagCode: "cycle-add-success",
            cycleDevices:
            [
                new CycleDevice { Id = "out-3", Name = "Headset" },
                new CycleDevice { Id = "out-4", Name = "Speakers" },
            ],
            deviceId: "out-3",
            deviceName: "Headset",
            jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.True(data["success"]?.Value<bool>());
        Assert.Equal("output", data["kind"]?.Value<string>());
        Assert.Equal("add", data["action"]?.Value<string>());
        Assert.Equal("cycle-add-success", data["diagCode"]?.Value<string>());
        Assert.Equal("out-3", data["deviceId"]?.Value<string>());
        Assert.Equal("Headset", data["deviceName"]?.Value<string>());

        JArray cycle = Assert.IsType<JArray>(data["cycle"]);
        Assert.Equal(2, cycle.Count);
        Assert.Equal(1, cycle[0]?["order"]?.Value<int>());
        Assert.Equal("out-3", cycle[0]?["id"]?.Value<string>());
        Assert.Equal("Headset", cycle[0]?["name"]?.Value<string>());
    }

    [Fact]
    public void FormatCycleMutationResult_Text_UsesDiagCodePrefix()
    {
        string text = CliOutputFormatter.FormatCycleMutationResult(
            kind: "output",
            action: "add",
            diagCode: "cycle-add-success",
            cycleDevices:
            [
                new CycleDevice { Id = "out-3", Name = "Headset" },
            ],
            deviceId: "out-3",
            deviceName: "Headset",
            jsonOutput: false);

        Assert.StartsWith("[diag-code:cycle-add-success] Added 'Headset' to output cycle.", text, StringComparison.Ordinal);
        Assert.Contains("1. Headset", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCycleMutationResult_JsonRedact_RedactsNamesAndIds()
    {
        string json = CliOutputFormatter.FormatCycleMutationResult(
            kind: "output",
            action: "add",
            diagCode: "cycle-add-success",
            cycleDevices:
            [
                new CycleDevice { Id = "out-3", Name = "Headset" },
            ],
            deviceId: "out-3",
            deviceName: "Headset",
            jsonOutput: true,
            redactOutput: true);

        JObject root = JObject.Parse(json);
        JToken data = Assert.IsType<JObject>(root["data"]);

        Assert.StartsWith("device-id[", data["deviceId"]?.Value<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("Headset", json, StringComparison.Ordinal);
        Assert.DoesNotContain("out-3", json, StringComparison.Ordinal);
        Assert.StartsWith("device[", data["deviceName"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device-id[", data["cycle"]?[0]?["id"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device[", data["cycle"]?[0]?["name"]?.Value<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRoutineExportResult_JsonRedact_RedactsPathButPreservesContract()
    {
        string json = CliOutputFormatter.FormatRoutineExportResult(
            path: @"C:\temp\routines.json",
            routineCount: 2,
            jsonOutput: true,
            redactOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.True(data["success"]?.Value<bool>());
        Assert.Equal("routine-export-success", data["diagCode"]?.Value<string>());
        Assert.Equal(2, data["routineCount"]?.Value<int>());
        Assert.DoesNotContain(@"C:\temp\routines.json", json, StringComparison.Ordinal);
        Assert.StartsWith("path[", data["exportPath"]?.Value<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLogExportResult_JsonManifest_IncludesEntriesAndDetailLevel()
    {
        var result = new LogArchiveExportResult(
            ExportPath: @"C:\temp\logs.zip",
            ExportedFileCount: 2,
            ExportedBytes: 42,
            Entries:
            [
                new LogArchiveExportEntryResult("exported", "current", @"C:\logs\AudioPilot.log", "AudioPilot.log", 20),
                new LogArchiveExportEntryResult("missing-at-export", "backup", @"C:\logs\backups\AudioPilot.log.bak1", "backups/AudioPilot.log.bak1", 0),
            ]);

        string json = CliOutputFormatter.FormatLogExportResult(result, CliDiagnosticsExportDetailLevel.Manifest, jsonOutput: true);

        JObject root = JObject.Parse(json);
        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.Equal("manifest", data["detailLevel"]?.Value<string>());
        Assert.True(data["partialExport"]?.Value<bool>());
        Assert.Equal(1, data["missingAtExportCount"]?.Value<int>());
        Assert.Equal(2, data["fileCount"]?.Value<int>());
        Assert.Equal(42, data["exportedBytes"]?.Value<long>());
        JArray entries = Assert.IsType<JArray>(data["entries"]);
        Assert.Equal(2, entries.Count);
        Assert.Equal("exported", entries[0]?["status"]?.Value<string>());
        Assert.Equal("current", entries[0]?["sourceKind"]?.Value<string>());
        Assert.Equal("AudioPilot.log", entries[0]?["archiveEntry"]?.Value<string>());
    }

    [Fact]
    public void FormatLogExportResult_JsonSummary_IncludesExplicitPartialExportFields()
    {
        var result = new LogArchiveExportResult(
            ExportPath: @"C:\temp\logs.zip",
            ExportedFileCount: 1,
            ExportedBytes: 20,
            Entries:
            [
                new LogArchiveExportEntryResult("exported", "current", @"C:\logs\AudioPilot.log", "AudioPilot.log", 20),
                new LogArchiveExportEntryResult("missing-at-export", "backup", @"C:\logs\backups\AudioPilot.log.bak1", "backups/AudioPilot.log.bak1", 0),
            ]);

        string json = CliOutputFormatter.FormatLogExportResult(result, CliDiagnosticsExportDetailLevel.Summary, jsonOutput: true);

        JObject root = JObject.Parse(json);
        JToken data = Assert.IsType<JObject>(root["data"]);
        Assert.Equal("summary", data["detailLevel"]?.Value<string>());
        Assert.True(data["partialExport"]?.Value<bool>());
        Assert.Equal(1, data["missingAtExportCount"]?.Value<int>());
        Assert.Null(data["entries"]);
    }

    [Fact]
    public void FormatLogExportResult_TextManifest_IncludesManifestLines()
    {
        var result = new LogArchiveExportResult(
            ExportPath: @"C:\temp\logs.zip",
            ExportedFileCount: 1,
            ExportedBytes: 20,
            Entries:
            [
                new LogArchiveExportEntryResult("exported", "current", @"C:\logs\AudioPilot.log", "AudioPilot.log", 20),
            ]);

        string text = CliOutputFormatter.FormatLogExportResult(result, CliDiagnosticsExportDetailLevel.Manifest, jsonOutput: false);

        Assert.Contains("Exported 1 log file", text, StringComparison.Ordinal);
        Assert.Contains("manifest:", text, StringComparison.Ordinal);
        Assert.Contains("exported current log -> AudioPilot.log", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\logs\AudioPilot.log", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLogExportResult_TextSummary_IncludesPartialExportSignal_WhenEntriesWereMissing()
    {
        var result = new LogArchiveExportResult(
            ExportPath: @"C:\temp\logs.zip",
            ExportedFileCount: 1,
            ExportedBytes: 20,
            Entries:
            [
                new LogArchiveExportEntryResult("exported", "current", @"C:\logs\AudioPilot.log", "AudioPilot.log", 20),
                new LogArchiveExportEntryResult("missing-at-export", "backup", @"C:\logs\backups\AudioPilot.log.bak1", "backups/AudioPilot.log.bak1", 0),
            ]);

        string text = CliOutputFormatter.FormatLogExportResult(result, CliDiagnosticsExportDetailLevel.Summary, jsonOutput: false);

        Assert.Contains("Exported 1 log file", text, StringComparison.Ordinal);
        Assert.Contains("1 additional entry was unavailable during export.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatLogExportResult_TextManifest_IncludesPartialExportSignal_WhenEntriesWereMissing()
    {
        var result = new LogArchiveExportResult(
            ExportPath: @"C:\temp\logs.zip",
            ExportedFileCount: 1,
            ExportedBytes: 20,
            Entries:
            [
                new LogArchiveExportEntryResult("exported", "current", @"C:\logs\AudioPilot.log", "AudioPilot.log", 20),
                new LogArchiveExportEntryResult("missing-at-export", "backup", @"C:\logs\backups\AudioPilot.log.bak1", "backups/AudioPilot.log.bak1", 0),
            ]);

        string text = CliOutputFormatter.FormatLogExportResult(result, CliDiagnosticsExportDetailLevel.Manifest, jsonOutput: false);

        Assert.Contains("1 additional entry was unavailable during export.", text, StringComparison.Ordinal);
        Assert.Contains("missing-at-export backup log -> backups/AudioPilot.log.bak1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExecutionHistory_JsonRedact_RedactsNamesQuotedLiteralsAndStructuredTargets()
    {
        var entries = new[]
        {
            new ExecutionHistoryEntry(
                OpId: "op-1",
                TimestampUtc: new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                Kind: ExecutionHistoryKind.Routine,
                Source: "cli",
                Action: "routine-run",
                Success: true,
                Skipped: false,
                Summary: "Routine 'Desk' completed.",
                Reason: "Triggered by 'Discord.exe'.",
                RoutineId: "routine-1",
                RoutineName: "Desk",
                OutputDeviceName: "Headset",
                InputDeviceName: "Desk Mic",
                Target: "Output: Headset | Input: Desk Mic | Master: 40%")
        };

        string json = CliOutputFormatter.FormatExecutionHistory(entries, jsonOutput: true, redactOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());

        JToken entry = Assert.Single(Assert.IsType<JArray>(root["data"]?["entries"]));
        Assert.Equal("op-1", entry["opId"]?.Value<string>());
        Assert.Equal("routine", entry["kind"]?.Value<string>());
        Assert.StartsWith("routine[", entry["routineName"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device[", entry["outputDeviceName"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device[", entry["inputDeviceName"]?.Value<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("Desk", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Discord.exe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Headset", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Desk Mic", json, StringComparison.Ordinal);
        Assert.Contains("Master: 40%", entry["target"]?.Value<string>(), StringComparison.Ordinal);
        Assert.Contains("Output: device[", entry["target"]?.Value<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRoutineList_JsonRedact_RedactsUnquotedTriggerAndTargetSummaries()
    {
        var routines = new[]
        {
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Office",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-office",
                OutputDeviceName = "Conference Speakers",
                InputDeviceId = "in-office",
                InputDeviceName = "Conference Mic",
            },
        };

        string json = CliOutputFormatter.FormatRoutineList(routines, jsonOutput: true, redactOutput: true);

        JObject root = JObject.Parse(json);
        JToken item = Assert.Single(Assert.IsType<JArray>(root["data"]?["routines"]));
        Assert.DoesNotContain("OfficeWiFi", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Conference Speakers", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Conference Mic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("out-office", json, StringComparison.Ordinal);
        Assert.DoesNotContain("in-office", json, StringComparison.Ordinal);
        Assert.Contains("network[", item["triggerSummary"]?.Value<string>(), StringComparison.Ordinal);
        Assert.Contains("Output: device[", item["targetSummary"]?.Value<string>(), StringComparison.Ordinal);
        Assert.StartsWith("device-id[", item["outputDeviceId"]?.Value<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRoutineList_Json_UsesStoredScheduleTimeZone()
    {
        var routines = new[]
        {
            new AudioRoutine
            {
                Id = "routine-schedule",
                Name = "Pacific Morning",
                TriggerKind = RoutineTriggerKind.Scheduled,
                ScheduleTime = new TimeOnly(9, 0),
                ScheduleDays = [DayOfWeek.Monday],
                ScheduleTimeZoneId = "Pacific Standard Time",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
        };

        string json = CliOutputFormatter.FormatRoutineList(routines, jsonOutput: true);

        JObject item = (JObject)Assert.Single((JArray)JObject.Parse(json)["data"]!["routines"]!);
        Assert.Equal("Pacific Standard Time", item["scheduleTimeZone"]?.Value<string>());
    }

    [Fact]
    public void FormatRoutineRunResult_Json_UsesStoredScheduleTimeZone()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-schedule",
            Name = "Pacific Morning",
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(9, 0),
            ScheduleDays = [DayOfWeek.Monday],
            ScheduleTimeZoneId = "Pacific Standard Time",
        };

        string json = CliOutputFormatter.FormatRoutineRunResult(routine, appliedOutputDeviceName: null, appliedInputDeviceName: null, jsonOutput: true);

        JObject data = (JObject)JObject.Parse(json)["data"]!;
        Assert.Equal("Pacific Standard Time", data["scheduleTimeZone"]?.Value<string>());
    }

    [Fact]
    public void FormatRoutineError_Json_UsesStoredScheduleTimeZone()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-schedule",
            Name = "Pacific Morning",
            TriggerKind = RoutineTriggerKind.Scheduled,
            ScheduleTime = new TimeOnly(9, 0),
            ScheduleDays = [DayOfWeek.Monday],
            ScheduleTimeZoneId = "Pacific Standard Time",
        };

        string json = CliOutputFormatter.FormatRoutineError(
            exitCode: 2,
            errorCode: "routine-run-failed",
            message: "Scheduled routine failed.",
            jsonOutput: true,
            routine: routine);

        JObject error = (JObject)JObject.Parse(json)["data"]!["error"]!;
        Assert.Equal("Pacific Standard Time", error["scheduleTimeZone"]?.Value<string>());
    }

    [Fact]
    public void FormatNetworkList_JsonRedact_RedactsNetworkNamesAndIncludesCount()
    {
        string json = CliOutputFormatter.FormatNetworkList(["OfficeWiFi", "HomeNet"], jsonOutput: true, redactOutput: true);

        JObject data = (JObject)JObject.Parse(json)["data"]!;
        JArray networks = Assert.IsType<JArray>(data["networks"]);
        Assert.Equal(2, data["count"]?.Value<int>());
        Assert.All(networks.Select(static item => item.Value<string>()), network => Assert.StartsWith("network[", network, StringComparison.Ordinal));
        Assert.DoesNotContain("OfficeWiFi", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HomeNet", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatNetworkList_TextRedact_RedactsNetworkNames()
    {
        string text = CliOutputFormatter.FormatNetworkList(["OfficeWiFi"], jsonOutput: false, redactOutput: true);

        Assert.StartsWith("network[", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OfficeWiFi", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatConfigValidation_JsonRedact_RedactsDisconnectedDeviceNames()
    {
        string json = CliOutputFormatter.FormatConfigValidation(
            ["[diag-code:output-cycle-disconnected-devices] Output cycle includes disconnected devices: Conference Speakers, Desk Headset. Reconnect those output devices."],
            jsonOutput: true,
            redactOutput: true);

        Assert.DoesNotContain("Conference Speakers", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Desk Headset", json, StringComparison.Ordinal);
        Assert.Contains("device[", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExecutionHistoryDetail_Text_IncludesOptionalFields()
    {
        var entry = new ExecutionHistoryEntry(
            OpId: "op-2",
            TimestampUtc: new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Kind: ExecutionHistoryKind.Switch,
            Source: "cli",
            Action: "switch-output",
            Success: false,
            Skipped: false,
            Summary: "Output switch failed.",
            Reason: "Device was unavailable.",
            OutputDeviceName: "Headset",
            Target: "Speakers",
            OutputSucceeded: false,
            DiagCode: "switch-output-failed",
            ElapsedMs: 42.5,
            Details: new Dictionary<string, string>
            {
                ["trigger"] = "cli",
            });

        string text = CliOutputFormatter.FormatExecutionHistoryDetail(entry, jsonOutput: false);

        Assert.Contains("opId: op-2", text, StringComparison.Ordinal);
        Assert.Contains("kind: switch", text, StringComparison.Ordinal);
        Assert.Contains("summary: Output switch failed.", text, StringComparison.Ordinal);
        Assert.Contains("reason: Device was unavailable.", text, StringComparison.Ordinal);
        Assert.Contains("outputDeviceName: Headset", text, StringComparison.Ordinal);
        Assert.Contains("target: Speakers", text, StringComparison.Ordinal);
        Assert.Contains("outputSucceeded: False", text, StringComparison.Ordinal);
        Assert.Contains("diagCode: switch-output-failed", text, StringComparison.Ordinal);
        Assert.Contains("elapsedMs: 42.5", text, StringComparison.Ordinal);
        Assert.Contains("detail.trigger: cli", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDiagnosticBundleExportResult_JsonManifest_IncludesBundleMetadata()
    {
        var result = new DiagnosticBundleExportResult(
            ExportPath: @"C:\temp\bundle.zip",
            IncludeSensitive: false,
            PartialExport: true,
            ExportedFileCount: 3,
            ExportedBytes: 1234,
            Entries:
            [
                new DiagnosticBundleExportEntryResult("exported", "diagnostics", "diagnostics/status.json", 100),
                new DiagnosticBundleExportEntryResult("missing", "logs", "logs/", 0),
            ]);

        string json = CliOutputFormatter.FormatDiagnosticBundleExportResult(result, CliDiagnosticsExportDetailLevel.Manifest, jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.True(root["data"]?["success"]?.Value<bool>());
        Assert.Equal("diagnostics-export-bundle-success", root["data"]?["diagCode"]?.Value<string>());
        Assert.Equal("redacted", root["data"]?["redactionMode"]?.Value<string>());
        Assert.True(root["data"]?["partialExport"]?.Value<bool>());
        Assert.Equal(3, root["data"]?["fileCount"]?.Value<int>());
        Assert.Equal(1234, root["data"]?["exportedBytes"]?.Value<long>());
        Assert.NotNull(root["data"]?["entries"]);
        Assert.DoesNotContain(@"C:\temp", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDiagnosticBundleExportResult_JsonSensitive_IncludesRawExportPath()
    {
        var result = new DiagnosticBundleExportResult(
            ExportPath: @"C:\temp\bundle.zip",
            IncludeSensitive: true,
            PartialExport: false,
            ExportedFileCount: 1,
            ExportedBytes: 100,
            Entries:
            [
                new DiagnosticBundleExportEntryResult("exported", "metadata", "manifest.json", 100),
            ]);

        string json = CliOutputFormatter.FormatDiagnosticBundleExportResult(result, CliDiagnosticsExportDetailLevel.Summary, jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal("sensitive", root["data"]?["redactionMode"]?.Value<string>());
        Assert.Equal(@"C:\temp\bundle.zip", root["data"]?["exportPath"]?.Value<string>());
    }

    [Fact]
    public void FormatExecutionHistoryNotFound_Json_UsesCliEnvelopeShape()
    {
        string json = CliOutputFormatter.FormatExecutionHistoryNotFound("op-missing", jsonOutput: true);

        JObject root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.False(root["data"]?["success"]?.Value<bool>());
        Assert.Equal("diagnostics-history-not-found", root["data"]?["diagCode"]?.Value<string>());
        Assert.Contains("op-missing", root["data"]?["error"]?.Value<string>(), StringComparison.Ordinal);
    }
}
