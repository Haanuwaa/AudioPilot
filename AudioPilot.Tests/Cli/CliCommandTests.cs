using AudioPilot.Cli;

namespace AudioPilot.Tests.Cli;

public sealed class CliCommandTests
{
    public static TheoryData<string[], CliAction> RedactCapableCommands => new()
    {
        { ["media", "status", "--redact"], CliAction.MediaStatus },
        { ["switch", "output", "--redact"], CliAction.SwitchOutput },
        { ["switch", "input", "--redact"], CliAction.SwitchInput },
        { ["wait", "--wait-for-device", "dev-1", "--redact"], CliAction.WaitForDevice },
        { ["diagnostics", "status", "--redact"], CliAction.DiagnosticsStatus },
        { ["diagnostics", "history", "--redact"], CliAction.DiagnosticsHistory },
        { ["diagnostics", "history-detail", "op-1", "--redact"], CliAction.DiagnosticsHistoryDetail },
        { ["diagnostics", "export-logs", "logs.zip", "--redact"], CliAction.DiagnosticsExportLogs },
        { ["listen", "toggle", "--redact"], CliAction.ListenToggle },
        { ["routine", "list", "--redact"], CliAction.RoutineList },
        { ["routine", "run", "desk", "--redact"], CliAction.RoutineRun },
        { ["routine", "enable", "routine-1", "--redact"], CliAction.RoutineEnable },
        { ["routine", "disable", "routine-1", "--redact"], CliAction.RoutineDisable },
        { ["routine", "create", "routine.json", "--redact"], CliAction.RoutineCreate },
        { ["routine", "update", "routine-1", "routine.json", "--redact"], CliAction.RoutineUpdate },
        { ["routine", "delete", "routine-1", "--redact"], CliAction.RoutineDelete },
        { ["routine", "import", "routine.json", "--replace", "--redact"], CliAction.RoutineImport },
        { ["routine", "export", "routines.json", "--redact"], CliAction.RoutineExport },
        { ["status", "--redact"], CliAction.Status },
        { ["config", "get", "theme", "--redact"], CliAction.ConfigGet },
        { ["runtime", "get", "hotplug-refresh-debounce-ms", "--redact"], CliAction.RuntimeGet },
        { ["network", "list", "--redact"], CliAction.NetworkList },
        { ["config", "validate", "--redact"], CliAction.ConfigValidate },
        { ["config", "export", "profile.json", "--redact"], CliAction.ConfigExport },
        { ["config", "import", "profile.json", "--merge", "--redact"], CliAction.ConfigImport },
        { ["devices", "list", "output", "--redact"], CliAction.DevicesListOutput },
        { ["devices", "list", "input", "--redact"], CliAction.DevicesListInput },
        { ["devices", "get", "output", "out-1", "--redact"], CliAction.DevicesGetOutput },
        { ["devices", "find", "input", "desk", "--redact"], CliAction.DevicesFindInput },
        { ["cycle", "show", "output", "--redact"], CliAction.CycleShowOutput },
        { ["cycle", "show", "input", "--redact"], CliAction.CycleShowInput },
        { ["cycle", "validate", "output", "--redact"], CliAction.CycleValidateOutput },
        { ["cycle", "validate", "input", "--redact"], CliAction.CycleValidateInput },
        { ["cycle", "test", "output", "--redact"], CliAction.CycleTestOutput },
        { ["cycle", "test", "input", "--redact"], CliAction.CycleTestInput },
        { ["cycle", "add", "output", "out-1", "--redact"], CliAction.CycleAddOutput },
        { ["cycle", "remove", "input", "in-1", "--redact"], CliAction.CycleRemoveInput },
        { ["cycle", "reorder", "output", "out-1", "out-2", "--redact"], CliAction.CycleReorderOutput },
        { ["startup", "status", "--redact"], CliAction.StartupStatus },
    };

    public static TheoryData<string> RedactCapableUsagePrefixes => new()
    {
        { "audio-pilot media status" },
        { "audio-pilot switch output" },
        { "audio-pilot switch input" },
        { "audio-pilot wait --wait-for-device" },
        { "audio-pilot diagnostics status" },
        { "audio-pilot diagnostics history" },
        { "audio-pilot diagnostics history-detail" },
        { "audio-pilot diagnostics export-logs" },
        { "audio-pilot listen toggle|on|off" },
        { "audio-pilot routine list" },
        { "audio-pilot routine run|enable|disable|delete" },
        { "audio-pilot routine create" },
        { "audio-pilot routine update" },
        { "audio-pilot routine import" },
        { "audio-pilot routine export" },
        { "audio-pilot status" },
        { "audio-pilot config get" },
        { "audio-pilot runtime get" },
        { "audio-pilot network list" },
        { "audio-pilot config export" },
        { "audio-pilot config import" },
        { "audio-pilot config validate" },
        { "audio-pilot devices list output|input" },
        { "audio-pilot devices get output|input" },
        { "audio-pilot devices find output|input" },
        { "audio-pilot cycle show|validate|test output|input" },
        { "audio-pilot cycle add|remove output|input" },
        { "audio-pilot cycle reorder output|input" },
        { "audio-pilot startup enable|disable|status" },
    };

    [Fact]
    public void TryParse_MediaStatusJsonRedact_Parses()
    {
        bool ok = CliCommand.TryParse(["media", "status", "--json", "--redact"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.MediaStatus, command.Action);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
    }

    [Fact]
    public void TryParse_MediaStatusWithExtraPositionalArg_Fails()
    {
        bool ok = CliCommand.TryParse(["media", "status", "unexpected"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Unknown flag", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_VolumeGetMasterJson_Parses()
    {
        bool ok = CliCommand.TryParse(["volume", "get", "master", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.VolumeGetMaster, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CompletionPowerShell_Parses()
    {
        bool ok = CliCommand.TryParse(["completion", "powershell"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Completion, command.Action);
        Assert.Equal("powershell", command.Key);
    }

    [Fact]
    public void TryParse_CompletionPwshAlias_NormalizesToPowershell()
    {
        bool ok = CliCommand.TryParse(["completion", "pwsh"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Completion, command.Action);
        Assert.Equal("powershell", command.Key);
    }

    [Fact]
    public void TryParse_CompletionHelp_ParsesAsTopicHelp()
    {
        bool ok = CliCommand.TryParse(["completion", "help"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Help, command.Action);
        Assert.Equal("completion", command.Key);
    }

    [Fact]
    public void TryParse_CompletionUnknownShell_Fails()
    {
        bool ok = CliCommand.TryParse(["completion", "fish"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("powershell|bash", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_VolumeSetMic_Parses()
    {
        bool ok = CliCommand.TryParse(["volume", "set", "mic", "42.5"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.VolumeSetMic, command.Action);
        Assert.Equal("42.5", command.Value);
    }

    [Fact]
    public void TryParse_VolumeSetMasterWithDeviceId_Parses()
    {
        bool ok = CliCommand.TryParse(["volume", "set", "master", "25", "--device-id", "out-2", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.VolumeSetMaster, command.Action);
        Assert.Equal(CliDeviceSelectorResolver.EncodeExactId("out-2"), command.Key);
        Assert.Equal("25", command.Value);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_VolumeGetMasterWithDeviceName_Parses()
    {
        bool ok = CliCommand.TryParse(["volume", "get", "master", "--device", "Speakers"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.VolumeGetMaster, command.Action);
        Assert.Equal(CliDeviceSelectorResolver.EncodeExactName("Speakers"), command.Key);
    }

    [Fact]
    public void TryParse_VolumeGetDuplicateDeviceId_Fails()
    {
        bool ok = CliCommand.TryParse(["volume", "get", "mic", "--device-id", "in-1", "--device-id", "in-2"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Conflicting device selector flags", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void TryParse_VolumeSet_InvalidPercent_Fails(string percent)
    {
        bool ok = CliCommand.TryParse(["volume", "set", "master", percent], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("between 0 and 100", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_VolumeGet_UnknownTarget_Fails()
    {
        bool ok = CliCommand.TryParse(["volume", "get", "speaker"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("volume get master|mic", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_NoArgs_ReturnsNoOp()
    {
        bool ok = CliCommand.TryParse([], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.None, command.Action);
        Assert.True(command.IsNoOpLaunch);
    }

    [Fact]
    public void TryParse_SwitchOutputWithFlags_ParsesAllFlags()
    {
        string[] args = ["switch", "output", "--reverse", "--mute-mic", "--mute-sound", "--deafen"];

        bool ok = CliCommand.TryParse(args, out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.SwitchOutput, command.Action);
        Assert.True(command.Reverse);
        Assert.True(command.MuteMic);
        Assert.True(command.MuteSound);
        Assert.True(command.Deafen);
    }

    [Fact]
    public void TryParse_SwitchInputWithUnsupportedFlags_Fails()
    {
        string[] args = ["switch", "input", "--deafen"];

        bool ok = CliCommand.TryParse(args, out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_StartupStatus_Parses()
    {
        bool ok = CliCommand.TryParse(["startup", "status"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.StartupStatus, command.Action);
    }

    [Fact]
    public void TryParse_RefreshJson_Parses()
    {
        bool ok = CliCommand.TryParse(["refresh", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Refresh, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DiagnosticsRefreshJson_Parses()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "refresh", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Refresh, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_StartupStatusJson_Parses()
    {
        bool ok = CliCommand.TryParse(["startup", "status", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.StartupStatus, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_StartupOpen_Parses()
    {
        bool ok = CliCommand.TryParse(["startup", "open"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.StartupOpen, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_Status_Parses()
    {
        bool ok = CliCommand.TryParse(["status"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Status, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ConfigListJson_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "list", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigList, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RuntimeList_Parses()
    {
        bool ok = CliCommand.TryParse(["runtime", "list"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RuntimeList, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleAddOutput_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "add", "output", "out-1", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleAddOutput, command.Action);
        Assert.Equal("out-1", command.Key);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleReorderInput_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "reorder", "input", "in-2", "in-1", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleReorderInput, command.Action);
        Assert.Equal("in-2\nin-1", command.Value);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleAddOutput_BlankDeviceId_Fails()
    {
        bool ok = CliCommand.TryParse(["cycle", "add", "output", "   "], out CliCommand command, out string? error);

        Assert.False(ok);
        Assert.Equal(CliAction.None, command.Action);
        Assert.Equal("Missing cycle device id. Use: cycle add output|input <deviceId> [--json] [--redact]", error);
    }

    [Fact]
    public void TryParse_CycleReorderInput_BlankDeviceId_Fails()
    {
        bool ok = CliCommand.TryParse(["cycle", "reorder", "input", "in-2", "   ", "--json"], out CliCommand command, out string? error);

        Assert.False(ok);
        Assert.Equal(CliAction.None, command.Action);
        Assert.Equal("Cycle reorder device ids must not be blank. Use: cycle reorder output|input <deviceId...> [--json] [--redact]", error);
    }

    [Fact]
    public void TryParse_CycleReorderInput_DuplicateDeviceIds_Fails()
    {
        bool ok = CliCommand.TryParse(["cycle", "reorder", "input", "in-2", "IN-2", "--json"], out CliCommand command, out string? error);

        Assert.False(ok);
        Assert.Equal(CliAction.None, command.Action);
        Assert.Equal("Duplicate cycle reorder device id 'IN-2'. Pass every existing cycle device id exactly once.", error);
    }

    [Fact]
    public void TryParse_RoutineExport_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "export", "routines.json", "--allow-any-path", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineExport, command.Action);
        Assert.Equal("routines.json", command.Key);
        Assert.True(command.AllowAnyPath);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineCreate_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "create", "routine.json", "--allow-any-path", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineCreate, command.Action);
        Assert.Equal("routine.json", command.Key);
        Assert.True(command.AllowAnyPath);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineUpdate_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "update", "routine-1", "routine.json", "--allow-any-path", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineUpdate, command.Action);
        Assert.Equal("routine-1", command.Key);
        Assert.Equal("routine.json", command.Value);
        Assert.True(command.AllowAnyPath);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineDelete_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "delete", "routine-1", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineDelete, command.Action);
        Assert.Equal("routine-1", command.Key);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineImport_ParsesReplaceMode()
    {
        bool ok = CliCommand.TryParse(["routine", "import", "routine.json", "--replace", "--allow-any-path", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineImport, command.Action);
        Assert.Equal("routine.json", command.Key);
        Assert.True(command.ReplaceImport);
        Assert.True(command.AllowAnyPath);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineImport_WithoutModeDefaultsToMerge()
    {
        bool ok = CliCommand.TryParse(["routine", "import", "routine.json", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineImport, command.Action);
        Assert.False(command.ReplaceImport);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DiagnosticsHelp_ParsesTopicHelp()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "help"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Help, command.Action);
        Assert.Equal("diagnostics", command.Key);
    }

    [Fact]
    public void TryParse_HelpRoutine_ParsesTopicHelp()
    {
        bool ok = CliCommand.TryParse(["help", "routine"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Help, command.Action);
        Assert.Equal("routine", command.Key);
    }

    [Fact]
    public void TryParse_HelpUnknownTopic_Fails()
    {
        bool ok = CliCommand.TryParse(["help", "banana"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Unknown help topic", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_HelpUnknownTopic_SuggestsNearestTopic()
    {
        bool ok = CliCommand.TryParse(["help", "volum"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Did you mean 'volume'", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_UnknownTopLevelCommand_SuggestsNearestCommand()
    {
        bool ok = CliCommand.TryParse(["statsu"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Did you mean 'status'", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_UnknownVolumeCommand_SuggestsNearestCommand()
    {
        bool ok = CliCommand.TryParse(["volume", "gte", "master"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Did you mean 'get'", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_UnknownDevicesTarget_SuggestsNearestTarget()
    {
        bool ok = CliCommand.TryParse(["devices", "list", "outpt"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Did you mean 'output'", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_StatusRedact_Parses()
    {
        bool ok = CliCommand.TryParse(["status", "--redact"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Status, command.Action);
        Assert.True(command.RedactOutput);
    }

    [Theory]
    [MemberData(nameof(RedactCapableCommands))]
    public void TryParse_RedactCapableCommand_ParsesRedactFlag(string[] args, CliAction expectedAction)
    {
        bool ok = CliCommand.TryParse(args, out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(expectedAction, command.Action);
        Assert.True(command.RedactOutput);
    }

    [Fact]
    public void TryParse_DiagnosticsStatusJson_Parses()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "status", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DiagnosticsStatus, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DiagnosticsStatusShowPaths_Parses()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "status", "--show-paths"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DiagnosticsStatus, command.Action);
        Assert.True(command.ShowPaths);
    }

    [Fact]
    public void TryParse_DiagnosticsStatusDuplicateShowPaths_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "status", "--show-paths", "--show-paths"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Duplicate --show-paths", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_NetworkListJsonRedact_Parses()
    {
        bool ok = CliCommand.TryParse(["network", "list", "--json", "--redact"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.NetworkList, command.Action);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
    }

    [Fact]
    public void TryParse_NetworkListUnknownFlag_Fails()
    {
        bool ok = CliCommand.TryParse(["network", "list", "--banana"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Unknown flag", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportLogs_Parses()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-logs", "logs.zip", "--allow-any-path", "--json", "--redact", "--detail", "manifest"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DiagnosticsExportLogs, command.Action);
        Assert.Equal("logs.zip", command.Key);
        Assert.True(command.AllowAnyPath);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Manifest, command.DiagnosticsExportDetailLevel);
    }

    [Fact]
    public void TryParse_DiagnosticsExportLogsMissingPath_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-logs"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Missing diagnostics export path", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportLogsDefaultDetail_IsSummary()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-logs", "logs.zip"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Summary, command.DiagnosticsExportDetailLevel);
    }

    [Fact]
    public void TryParse_DiagnosticsExportLogsInvalidDetail_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-logs", "logs.zip", "--detail", "paths"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("summary or manifest", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportLogsMissingDetailValue_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-logs", "logs.zip", "--detail"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Missing value for --detail", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportLogsDuplicateDetail_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-logs", "logs.zip", "--detail", "summary", "--detail", "manifest"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Duplicate --detail", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportBundle_Parses()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-bundle", "bundle.zip", "--allow-any-path", "--json", "--detail", "manifest", "--include-sensitive"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DiagnosticsExportBundle, command.Action);
        Assert.Equal("bundle.zip", command.Key);
        Assert.True(command.AllowAnyPath);
        Assert.True(command.JsonOutput);
        Assert.True(command.IncludeSensitive);
        Assert.False(command.RedactOutput);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Manifest, command.DiagnosticsExportDetailLevel);
    }

    [Fact]
    public void TryParse_DiagnosticsExportBundleMissingPath_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-bundle"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Missing diagnostics bundle export path", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportBundleRejectsRedactFlag_BecauseDefaultIsRedacted()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-bundle", "bundle.zip", "--redact"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("redacted by default", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportBundleDuplicateIncludeSensitive_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-bundle", "bundle.zip", "--include-sensitive", "--include-sensitive"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Duplicate --include-sensitive", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsExportBundleInvalidDetail_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "export-bundle", "bundle.zip", "--detail", "paths"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("summary or manifest", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DiagnosticsResetPerAppAudio_Parses()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "reset-per-app-audio", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DiagnosticsResetPerAppAudio, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DiagnosticsResetPerAppAudio_WithRedact_Fails()
    {
        bool ok = CliCommand.TryParse(["diagnostics", "reset-per-app-audio", "--redact"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("does not support --redact", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_RoutineRunRedactJson_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "run", "desk", "--json", "--redact"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineRun, command.Action);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
        Assert.Equal("desk", command.Key);
    }

    [Fact]
    public void TryParse_DevicesListOutputJson_Parses()
    {
        bool ok = CliCommand.TryParse(["devices", "list", "output", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DevicesListOutput, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DevicesGetInput_Parses()
    {
        bool ok = CliCommand.TryParse(["devices", "get", "input", "Desk Mic", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DevicesGetInput, command.Action);
        Assert.Equal("Desk Mic", command.Key);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DevicesFindOutput_MultiWordQuery_Parses()
    {
        bool ok = CliCommand.TryParse(["devices", "find", "output", "usb", "headset", "--redact"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DevicesFindOutput, command.Action);
        Assert.Equal("usb headset", command.Key);
        Assert.True(command.RedactOutput);
    }

    [Fact]
    public void TryParse_CycleShowInput_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "show", "input"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleShowInput, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleValidateOutputJson_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "validate", "output", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleValidateOutput, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleValidateInput_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "validate", "input"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleValidateInput, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleTestOutputJson_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "test", "output", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleTestOutput, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_CycleTestInput_Parses()
    {
        bool ok = CliCommand.TryParse(["cycle", "test", "input"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.CycleTestInput, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_DevicesListUnknownFlag_Fails()
    {
        bool ok = CliCommand.TryParse(["devices", "list", "output", "--pretty"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void PipePayload_V1Envelope_RoundTrips()
    {
        var original = new CliCommand
        {
            Action = CliAction.SwitchOutput,
            Reverse = true,
            MuteMic = true,
            MuteSound = false,
            Deafen = true,
            JsonOutput = true,
            RedactOutput = true,
        };

        string payload = original.ToPipePayload();
        Assert.StartsWith("{", payload, StringComparison.Ordinal);

        bool ok = CliCommand.TryFromPipePayload(payload, out CliCommand parsed);

        Assert.True(ok);
        Assert.Equal(original.Action, parsed.Action);
        Assert.Equal(original.Reverse, parsed.Reverse);
        Assert.Equal(original.MuteMic, parsed.MuteMic);
        Assert.Equal(original.MuteSound, parsed.MuteSound);
        Assert.Equal(original.Deafen, parsed.Deafen);
        Assert.Equal(original.JsonOutput, parsed.JsonOutput);
        Assert.Equal(original.RedactOutput, parsed.RedactOutput);
    }

    [Fact]
    public void PipePayload_DiagnosticsExportLogsRoundTripsDetailLevel()
    {
        var original = new CliCommand
        {
            Action = CliAction.DiagnosticsExportLogs,
            Key = "logs.zip",
            AllowAnyPath = true,
            DiagnosticsExportDetailLevel = CliDiagnosticsExportDetailLevel.Manifest,
            JsonOutput = true,
        };

        string payload = original.ToPipePayload();

        bool ok = CliCommand.TryFromPipePayload(payload, out CliCommand parsed);

        Assert.True(ok);
        Assert.Equal(original.Action, parsed.Action);
        Assert.Equal(original.Key, parsed.Key);
        Assert.Equal(original.AllowAnyPath, parsed.AllowAnyPath);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Manifest, parsed.DiagnosticsExportDetailLevel);
        Assert.True(parsed.JsonOutput);
    }

    [Fact]
    public void PipePayload_DiagnosticsExportBundleRoundTripsSensitivityAndDetailLevel()
    {
        var original = new CliCommand
        {
            Action = CliAction.DiagnosticsExportBundle,
            Key = "bundle.zip",
            AllowAnyPath = true,
            DiagnosticsExportDetailLevel = CliDiagnosticsExportDetailLevel.Manifest,
            IncludeSensitive = true,
            JsonOutput = true,
        };

        string payload = original.ToPipePayload();

        bool ok = CliCommand.TryFromPipePayload(payload, out CliCommand parsed);

        Assert.True(ok);
        Assert.Equal(original.Action, parsed.Action);
        Assert.Equal(original.Key, parsed.Key);
        Assert.Equal(original.AllowAnyPath, parsed.AllowAnyPath);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Manifest, parsed.DiagnosticsExportDetailLevel);
        Assert.True(parsed.IncludeSensitive);
        Assert.True(parsed.JsonOutput);
    }

    [Fact]
    public void TryParse_MediaNext_Parses()
    {
        bool ok = CliCommand.TryParse(["media", "next"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.MediaNextTrack, command.Action);
    }

    [Fact]
    public void TryParse_MuteMicOff_Parses()
    {
        bool ok = CliCommand.TryParse(["mute", "mic", "off"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.MuteMicOff, command.Action);
    }

    [Fact]
    public void TryParse_MuteMicJson_Parses()
    {
        bool ok = CliCommand.TryParse(["mute", "mic", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.MuteMicToggle, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_MuteSoundOffJson_Parses()
    {
        bool ok = CliCommand.TryParse(["mute", "sound", "off", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.MuteSoundOff, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ListenToggle_Parses()
    {
        bool ok = CliCommand.TryParse(["listen", "toggle"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ListenToggle, command.Action);
    }

    [Fact]
    public void TryParse_ListenOnJson_Parses()
    {
        bool ok = CliCommand.TryParse(["listen", "on", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ListenOn, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ListenUnknown_Fails()
    {
        bool ok = CliCommand.TryParse(["listen", "enable"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RoutineListJson_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "list", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineList, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineRun_ParsesSelector()
    {
        bool ok = CliCommand.TryParse(["routine", "run", "desk"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineRun, command.Action);
        Assert.Equal("desk", command.Key);
    }

    [Fact]
    public void TryParse_RoutineEnableJson_Parses()
    {
        bool ok = CliCommand.TryParse(["routine", "enable", "routine-1", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RoutineEnable, command.Action);
        Assert.Equal("routine-1", command.Key);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RoutineDisableMissingSelector_Fails()
    {
        bool ok = CliCommand.TryParse(["routine", "disable"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_ConfigGetJson_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "get", "theme", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigGet, command.Action);
        Assert.Equal("theme", command.Key);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ConfigSet_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "set", "theme", "Dark"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigSet, command.Action);
        Assert.Equal("theme", command.Key);
        Assert.Equal("Dark", command.Value);
    }

    [Fact]
    public void TryParse_RuntimeGetJson_Parses()
    {
        bool ok = CliCommand.TryParse(["runtime", "get", "hotplug-refresh-debounce-ms", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RuntimeGet, command.Action);
        Assert.Equal("hotplug-refresh-debounce-ms", command.Key);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_RuntimeSet_Parses()
    {
        bool ok = CliCommand.TryParse(["runtime", "set", "hotplug-refresh-debounce-ms", "400"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.RuntimeSet, command.Action);
        Assert.Equal("hotplug-refresh-debounce-ms", command.Key);
        Assert.Equal("400", command.Value);
    }

    [Fact]
    public void TryParse_ConfigValidate_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "validate"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigValidate, command.Action);
        Assert.False(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ConfigValidateJson_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "validate", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigValidate, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_WaitForDevice_Parses()
    {
        bool ok = CliCommand.TryParse(["wait", "--wait-for-device", "dev-1", "--timeout", "1500", "--output", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.WaitForDevice, command.Action);
        Assert.Equal("dev-1", command.Key);
        Assert.Equal("1500", command.Value);
        Assert.True(command.WaitOutputOnly);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_SwitchOutputDryRunRequireCurrent_Parses()
    {
        bool ok = CliCommand.TryParse(["switch", "output", "--dry-run", "--require-current", "out-1"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.SwitchOutput, command.Action);
        Assert.True(command.DryRun);
        Assert.Equal("out-1", command.Key);
    }

    [Fact]
    public void TryParse_SwitchInputJson_Parses()
    {
        bool ok = CliCommand.TryParse(["switch", "input", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.SwitchInput, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_SwitchInputDryRunRequireCurrent_Parses()
    {
        bool ok = CliCommand.TryParse(["switch", "input", "--dry-run", "--require-current", "in-1"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.SwitchInput, command.Action);
        Assert.True(command.DryRun);
        Assert.Equal("in-1", command.Key);
    }

    [Fact]
    public void TryParse_SwitchInputMuteFlag_FailsWithSupportedFlagGuidance()
    {
        bool ok = CliCommand.TryParse(["switch", "input", "--mute-mic"], out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen.", error);
    }

    [Fact]
    public void TryParse_SwitchOutputBlankRequireCurrent_Fails()
    {
        bool ok = CliCommand.TryParse(["switch", "output", "--require-current", "   "], out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Missing device ID for --require-current.", error);
    }

    [Fact]
    public void TryParse_SwitchOutputDuplicateRequireCurrent_Fails()
    {
        bool ok = CliCommand.TryParse(["switch", "output", "--require-current", "out-1", "--require-current", "out-2"], out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Duplicate --require-current flag.", error);
    }

    [Fact]
    public void TryParse_SwitchDuplicateJson_Fails()
    {
        bool ok = CliCommand.TryParse(["switch", "output", "--json", "--json"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_ConfigExport_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "export", "profile.json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigExport, command.Action);
        Assert.Equal("profile.json", command.Key);
    }

    [Fact]
    public void TryParse_ConfigExportZip_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "export", "profile.zip"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigExport, command.Action);
        Assert.Equal("profile.zip", command.Key);
    }

    [Fact]
    public void TryParse_ConfigExportJson_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "export", "profile.json", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigExport, command.Action);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ConfigImportAllowAnyPath_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "import", "profile.json", "--replace", "--allow-any-path"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigImport, command.Action);
        Assert.True(command.ReplaceImport);
        Assert.True(command.AllowAnyPath);
    }

    [Fact]
    public void TryParse_ConfigExportAllowAnyPath_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "export", "profile.json", "--allow-any-path"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigExport, command.Action);
        Assert.True(command.AllowAnyPath);
    }

    [Fact]
    public void TryParse_ConfigImportReplace_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "import", "profile.json", "--replace"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigImport, command.Action);
        Assert.Equal("profile.json", command.Key);
        Assert.True(command.ReplaceImport);
    }

    [Fact]
    public void TryParse_ConfigImportZipReplace_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "import", "profile.zip", "--replace"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigImport, command.Action);
        Assert.Equal("profile.zip", command.Key);
        Assert.True(command.ReplaceImport);
    }

    [Fact]
    public void TryParse_ConfigImportMergeJson_Parses()
    {
        bool ok = CliCommand.TryParse(["config", "import", "profile.json", "--merge", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigImport, command.Action);
        Assert.False(command.ReplaceImport);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ConfigImport_WithoutModeDefaultsToMerge()
    {
        bool ok = CliCommand.TryParse(["config", "import", "profile.json", "--json"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.ConfigImport, command.Action);
        Assert.False(command.ReplaceImport);
        Assert.True(command.JsonOutput);
    }

    [Fact]
    public void TryParse_ConfigImportConflictingModeFlags_Fails()
    {
        bool ok = CliCommand.TryParse(["config", "import", "profile.json", "--merge", "--replace"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_WaitWithConflictingScope_Fails()
    {
        bool ok = CliCommand.TryParse(["wait", "--wait-for-device", "dev-1", "--output", "--input"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_WaitWithNegativeTimeout_Fails()
    {
        bool ok = CliCommand.TryParse(["wait", "--wait-for-device", "dev-1", "--timeout", "-5"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_WaitWithDuplicateJson_Fails()
    {
        bool ok = CliCommand.TryParse(["wait", "--wait-for-device", "dev-1", "--json", "--json"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Duplicate --json", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_WaitWithDuplicateRedact_Fails()
    {
        bool ok = CliCommand.TryParse(["wait", "--wait-for-device", "dev-1", "--redact", "--redact"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Duplicate --redact", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_SwitchDuplicateRedact_Fails()
    {
        bool ok = CliCommand.TryParse(["switch", "output", "--redact", "--redact"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("Duplicate --redact", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipePayload_ConfigRoundTripsWithArguments()
    {
        var original = new CliCommand
        {
            Action = CliAction.ConfigSet,
            Key = "theme",
            Value = "Dark Mode",
            JsonOutput = true,
        };

        string payload = original.ToPipePayload();

        bool ok = CliCommand.TryFromPipePayload(payload, out CliCommand parsed);

        Assert.True(ok);
        Assert.Equal(original.Action, parsed.Action);
        Assert.Equal(original.Key, parsed.Key);
        Assert.Equal(original.Value, parsed.Value);
        Assert.True(parsed.JsonOutput);
    }

    [Fact]
    public void PipePayload_UnknownProtocolVersion_FailsToParse()
    {
        const string unknownVersionPayload = "{\"kind\":\"cli-command\",\"protocolVersion\":99,\"action\":\"Status\"}";

        bool ok = CliCommand.TryFromPipePayload(unknownVersionPayload, out _);

        Assert.False(ok);
    }

    [Fact]
    public void PipePayload_NonJsonEnvelope_FailsToParse()
    {
        bool ok = CliCommand.TryFromPipePayload("CLI|v1|3|0", out _);

        Assert.False(ok);
    }

    [Theory]
    [MemberData(nameof(RedactCapableUsagePrefixes))]
    public void UsageText_ListsRedactFlag_ForRedactCapableCommands(string commandPrefix)
    {
        string usageLine = CliCommand.UsageText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First(line => line.StartsWith(commandPrefix, StringComparison.Ordinal));

        Assert.Contains("--redact", usageLine, StringComparison.Ordinal);
    }

    [Fact]
    public void GetHelpText_Routine_IncludesDefaultMergeNote()
    {
        string helpText = CliCommand.GetHelpText("routine");

        Assert.Contains("routine import", helpText, StringComparison.Ordinal);
        Assert.Contains("merges by default", helpText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("media")]
    [InlineData("mute")]
    [InlineData("listen")]
    [InlineData("volume")]
    [InlineData("network")]
    [InlineData("switch")]
    [InlineData("wait")]
    [InlineData("startup")]
    public void TryParse_GroupedHelpTopic_Parses(string topic)
    {
        bool ok = CliCommand.TryParse(["help", topic], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Help, command.Action);
        Assert.Equal(topic, command.Key);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("media")]
    [InlineData("mute")]
    [InlineData("listen")]
    [InlineData("volume")]
    [InlineData("network")]
    [InlineData("switch")]
    [InlineData("wait")]
    [InlineData("startup")]
    public void TryParse_TopicHelpAlias_Parses(string topic)
    {
        bool ok = CliCommand.TryParse([topic, "help"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Help, command.Action);
        Assert.Equal(topic, command.Key);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("hide")]
    [InlineData("refresh")]
    [InlineData("status")]
    [InlineData("version")]
    public void TryParse_CoreCommandHelpFlag_ParsesCoreHelp(string verb)
    {
        bool ok = CliCommand.TryParse([verb, "--help"], out CliCommand command, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.Help, command.Action);
        Assert.Equal("core", command.Key);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("hide")]
    [InlineData("version")]
    public void TryParse_ArgumentlessCoreCommandWithExtraArgument_ReturnsInvalidUsage(string verb)
    {
        bool ok = CliCommand.TryParse([verb, "unexpected"], out _, out string? error);

        Assert.False(ok);
        Assert.Contains("Too many arguments", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("media", "play-pause")]
    [InlineData("mute", "mic|sound|deafen")]
    [InlineData("listen", "toggle|on|off")]
    [InlineData("volume", "volume get master|mic")]
    [InlineData("network", "network list")]
    [InlineData("switch", "switch output")]
    [InlineData("wait", "--wait-for-device")]
    [InlineData("startup", "startup open")]
    public void GetHelpText_NewTopic_IncludesTopicUsage(string topic, string expectedToken)
    {
        string helpText = CliCommand.GetHelpText(topic);

        Assert.Contains($"AudioPilot CLI - {topic}", helpText, StringComparison.Ordinal);
        Assert.Contains(expectedToken, helpText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_DiagnosticsHistory_ParsesLimitTypeAndOutputFlags()
    {
        bool ok = CliCommand.TryParse(
            ["diagnostics", "history", "--limit", "7", "--type", "mute", "--json", "--redact"],
            out CliCommand command,
            out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CliAction.DiagnosticsHistory, command.Action);
        Assert.Equal(7, command.Limit);
        Assert.Equal("mute", command.Key);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
    }

    [Fact]
    public void TryParse_DiagnosticsHistory_InvalidLimit_Fails()
    {
        bool ok = CliCommand.TryParse(
            ["diagnostics", "history", "--limit", "0"],
            out _,
            out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("positive integer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipePayload_DiagnosticsHistory_RoundTripsLimitAndType()
    {
        var original = new CliCommand
        {
            Action = CliAction.DiagnosticsHistory,
            JsonOutput = true,
            RedactOutput = true,
            Key = "switch",
            Limit = 5,
        };

        string payload = original.ToPipePayload();

        bool ok = CliCommand.TryFromPipePayload(payload, out CliCommand parsed);

        Assert.True(ok);
        Assert.Equal(CliAction.DiagnosticsHistory, parsed.Action);
        Assert.True(parsed.JsonOutput);
        Assert.True(parsed.RedactOutput);
        Assert.Equal("switch", parsed.Key);
        Assert.Equal(5, parsed.Limit);
    }
}
