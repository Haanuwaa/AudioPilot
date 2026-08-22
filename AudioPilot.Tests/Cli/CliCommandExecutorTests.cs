using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed class CliCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Status_ReturnsRuntimeStatusOutput()
    {
        var runtime = new FakeRuntime
        {
            StatusText = "ok"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.Status }, runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MediaStatus_ReturnsRuntimeMediaStatusOutput()
    {
        var runtime = new FakeRuntime
        {
            MediaStatusText = "playback status: Playing"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.MediaStatus, RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("playback status: Playing", result.Output);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_MediaStatus_AwaitsAsynchronousRuntimeCompletion()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime
        {
            MediaStatusAsyncOverride = () => completion.Task,
        };

        Task<CliExecutionResult> execution = CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.MediaStatus },
            runtime);

        Assert.False(execution.IsCompleted);
        completion.SetResult("asynchronous media status");

        CliExecutionResult result = await execution;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("asynchronous media status", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkList_AwaitsAsynchronousRuntimeCompletion()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime
        {
            NetworkListAsyncOverride = () => completion.Task,
        };

        Task<CliExecutionResult> execution = CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.NetworkList },
            runtime);

        Assert.False(execution.IsCompleted);
        completion.SetResult("asynchronous network list");

        CliExecutionResult result = await execution;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("asynchronous network list", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsStatus_ReturnsRuntimeDiagnosticsOutput()
    {
        var runtime = new FakeRuntime
        {
            DiagnosticsStatusText = "diag-ok"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.DiagnosticsStatus }, runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("diag-ok", result.Output);
        Assert.False(runtime.LastDiagnosticsShowPaths);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsStatusShowPaths_ForwardsShowPathsFlag()
    {
        var runtime = new FakeRuntime
        {
            DiagnosticsStatusText = "diag-ok"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.DiagnosticsStatus, ShowPaths = true }, runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("diag-ok", result.Output);
        Assert.True(runtime.LastDiagnosticsShowPaths);
    }

    [Fact]
    public async Task ExecuteAsync_RefreshWithJson_ReturnsSuccessEnvelope()
    {
        var runtime = new FakeRuntime();

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.Refresh, JsonOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.True(runtime.RefreshCalled);
        Assert.NotNull(result.Output);

        var root = JObject.Parse(result.Output!);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.True(root["data"]?["success"]?.Value<bool>());
        Assert.Equal("refresh-success", root["data"]?["diagCode"]?.Value<string>());
    }

    [Fact]
    public async Task ExecuteAsync_RefreshFailure_ThrowsRuntimeException()
    {
        var runtime = new FakeRuntime
        {
            RefreshException = new InvalidOperationException("refresh boom")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CliCommandExecutor.ExecuteAsync(new CliCommand { Action = CliAction.Refresh }, runtime));

        Assert.True(runtime.RefreshCalled);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportLogs_ForwardsPathAllowAnyPathAndDetailLevel()
    {
        var runtime = new FakeRuntime
        {
            LogExportResult = (true, "logs-exported")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsExportLogs, Key = "logs.zip", AllowAnyPath = true, DiagnosticsExportDetailLevel = CliDiagnosticsExportDetailLevel.Manifest, RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("logs-exported", result.Output);
        Assert.Equal("logs.zip", runtime.LastExportPath);
        Assert.True(runtime.LastExportAllowAnyPath);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Manifest, runtime.LastExportDetailLevel);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportLogs_Failure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            LogExportResult = (false, "logs-export-failed")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsExportLogs, Key = "logs.zip" },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("logs-export-failed", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportBundle_ForwardsPathAllowAnyPathDetailAndSensitivity()
    {
        var runtime = new FakeRuntime
        {
            BundleExportResult = (true, "bundle-exported")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsExportBundle, Key = "bundle.zip", AllowAnyPath = true, DiagnosticsExportDetailLevel = CliDiagnosticsExportDetailLevel.Manifest, IncludeSensitive = true, JsonOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("bundle-exported", result.Output);
        Assert.Equal("bundle.zip", runtime.LastExportPath);
        Assert.True(runtime.LastExportAllowAnyPath);
        Assert.Equal(CliDiagnosticsExportDetailLevel.Manifest, runtime.LastExportDetailLevel);
        Assert.True(runtime.LastIncludeSensitive);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportBundle_Failure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            BundleExportResult = (false, "bundle-export-failed")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsExportBundle, Key = "bundle.zip" },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("bundle-export-failed", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsExportBundle_AwaitsDeferredRuntimeOperation()
    {
        var completion = new TaskCompletionSource<(bool Success, string Output)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime
        {
            BundleExportAsyncOverride = () => completion.Task,
        };

        Task<CliExecutionResult> execution = CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsExportBundle, Key = "bundle.zip" },
            runtime);

        Assert.False(execution.IsCompleted);
        completion.TrySetResult((true, "deferred-bundle-exported"));

        CliExecutionResult result = await execution;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("deferred-bundle-exported", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsResetPerAppAudio_ReturnsRuntimeResult()
    {
        var runtime = new FakeRuntime
        {
            ResetPerAppAudioResult = (true, "per-app-reset")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsResetPerAppAudio, JsonOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("per-app-reset", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsResetPerAppAudio_Failure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            ResetPerAppAudioResult = (false, "per-app-reset-failed")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsResetPerAppAudio },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("per-app-reset-failed", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsHistory_ForwardsLimitTypeAndRedactFlag()
    {
        var runtime = new FakeRuntime
        {
            DiagnosticsHistoryText = "history-ok"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsHistory, Limit = 4, Key = "routine", RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("history-ok", result.Output);
        Assert.Equal(4, runtime.LastDiagnosticsLimit);
        Assert.Equal("routine", runtime.LastDiagnosticsType);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_DiagnosticsHistoryDetail_NotFound_ReturnsExitCodeFive()
    {
        var runtime = new FakeRuntime
        {
            DiagnosticsHistoryDetailResult = (false, "history-missing")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DiagnosticsHistoryDetail, Key = "op-404", RedactOutput = true },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("history-missing", result.Output);
        Assert.Equal("op-404", runtime.LastDiagnosticsHistoryOpId);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineList_ForwardsRedactFlag()
    {
        var runtime = new FakeRuntime
        {
            RoutineListText = "routines"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineList, RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routines", result.Output);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchOutputFailure_ReturnsPreconditionExitCode()
    {
        var runtime = new FakeRuntime
        {
            SwitchOutputResult = false
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.SwitchOutput },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("Output switch precondition failed.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_StartupEnableFailure_ReturnsExecutionFailureExitCode()
    {
        var runtime = new FakeRuntime
        {
            StartupEnableResult = false
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.StartupEnable },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("Failed to enable startup.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CycleValidationInvalid_ReturnsExitCodeFive()
    {
        var runtime = new FakeRuntime
        {
            ValidationResult = (false, "invalid")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.CycleValidateOutput },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("invalid", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CycleAddOutput_ReturnsRuntimeResult()
    {
        var runtime = new FakeRuntime
        {
            CycleMutationResult = (true, "cycle-added")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.CycleAddOutput, Key = "out-1", RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cycle-added", result.Output);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineExport_Failure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            RoutineExportResult = (false, "routine-export-failed")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineExport, Key = "routines.json", AllowAnyPath = true },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("routine-export-failed", result.Output);
        Assert.True(runtime.LastExportAllowAnyPath);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigGet_ReturnsValue()
    {
        var runtime = new FakeRuntime
        {
            ConfigGetResult = (true, "Dark", null)
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ConfigGet, Key = "theme" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Dark", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeGet_ReturnsValue()
    {
        var runtime = new FakeRuntime
        {
            RuntimeGetResult = (true, "350", null)
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RuntimeGet, Key = "hotplug-refresh-debounce-ms" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("350", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeSetFailure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            RuntimeSetResult = (false, "invalid range")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RuntimeSet, Key = "hotplug-refresh-debounce-ms", Value = "1" },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("invalid range", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigGetUnknownKey_ReturnsPreconditionFailure()
    {
        var runtime = new FakeRuntime
        {
            ConfigGetResult = (false, null, "Unknown config key 'missing-key'.")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ConfigGet, Key = "missing-key" },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("Unknown config key 'missing-key'.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigSetFailure_ReturnsPreconditionFailure()
    {
        var runtime = new FakeRuntime
        {
            ConfigSetResult = (false, "value must be one of: System, Light, Dark")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ConfigSet, Key = "theme", Value = "Nope" },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("value must be one of: System, Light, Dark", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RuntimeGetUnknownKey_ReturnsPreconditionFailure()
    {
        var runtime = new FakeRuntime
        {
            RuntimeGetResult = (false, null, "Unknown runtime key 'missing-key'.")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RuntimeGet, Key = "missing-key" },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("Unknown runtime key 'missing-key'.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MuteMicToggleFailure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            ToggleMuteMicResult = false
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.MuteMicToggle },
            runtime);

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ListenToggleFailure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            ToggleListenResult = false
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ListenToggle },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("Failed to toggle input listen state.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ListenOnSuccess_ReturnsZero()
    {
        var runtime = new FakeRuntime
        {
            SetListenResult = true,
            ListenStatusText = "listen to input: enabled"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ListenOn },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("listen to input: enabled", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeGetMaster_ReturnsRuntimeOutput()
    {
        var runtime = new FakeRuntime
        {
            VolumeResult = (true, "master-volume")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.VolumeGetMaster },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("master-volume", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeGetMic_ForwardsDeviceId()
    {
        var runtime = new FakeRuntime
        {
            VolumeResult = (true, "mic-volume")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.VolumeGetMic, Key = "in-2" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("in-2", runtime.LastVolumeDeviceId);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeSetMicFailure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            VolumeResult = (false, "volume-set-failed")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.VolumeSetMic, Value = "15" },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("volume-set-failed", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeSetMaster_ForwardsDeviceIdAndPercent()
    {
        var runtime = new FakeRuntime
        {
            VolumeResult = (true, "volume-set")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.VolumeSetMaster, Key = "out-4", Value = "35" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out-4", runtime.LastVolumeDeviceId);
        Assert.Equal(35f, runtime.LastVolumePercent);
    }

    [Fact]
    public async Task ExecuteAsync_VolumeSetMasterMissingPercent_ReturnsInvalidUsage()
    {
        var runtime = new FakeRuntime();

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.VolumeSetMaster },
            runtime);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Missing volume percent.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_DevicesGetOutput_ReturnsRuntimeOutput()
    {
        var runtime = new FakeRuntime
        {
            DeviceGetResult = (true, "device-get")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DevicesGetOutput, Key = "Speakers", RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("device-get", result.Output);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_DevicesFindInput_NoMatchesReturnsPreconditionFailure()
    {
        var runtime = new FakeRuntime
        {
            DeviceFindResult = (false, "device-find-none")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.DevicesFindInput, Key = "usb" },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("device-find-none", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineList_ReturnsRuntimeOutput()
    {
        var runtime = new FakeRuntime
        {
            RoutineListText = "routines"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineList },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routines", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineRun_ForwardsRuntimeResult()
    {
        var runtime = new FakeRuntime
        {
            RunRoutineResult = new CliExecutionResult(0, "routine-ok")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineRun, Key = "routine-1" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routine-ok", result.Output);
        Assert.Equal("routine-1", runtime.LastRoutineSelector);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineEnable_ForwardsEnabledFlag()
    {
        var runtime = new FakeRuntime
        {
            SetRoutineEnabledResult = new CliExecutionResult(0, "enabled")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineEnable, Key = "routine-1" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.True(runtime.LastRoutineEnabledValue);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineCreate_ForwardsPathAndAllowAnyPath()
    {
        var runtime = new FakeRuntime
        {
            CreateRoutineResult = new CliExecutionResult(0, "created")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineCreate, Key = "routine.json", AllowAnyPath = true, RedactOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routine.json", runtime.LastRoutinePath);
        Assert.True(runtime.LastRoutineAllowAnyPath);
        Assert.True(runtime.LastRedactOutput);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineUpdate_ForwardsSelectorAndPath()
    {
        var runtime = new FakeRuntime
        {
            UpdateRoutineResult = new CliExecutionResult(0, "updated")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineUpdate, Key = "routine-1", Value = "routine.json", AllowAnyPath = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routine-1", runtime.LastRoutineSelector);
        Assert.Equal("routine.json", runtime.LastRoutinePath);
        Assert.True(runtime.LastRoutineAllowAnyPath);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineDelete_ForwardsSelector()
    {
        var runtime = new FakeRuntime
        {
            DeleteRoutineResult = new CliExecutionResult(0, "deleted")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineDelete, Key = "routine-1" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routine-1", runtime.LastRoutineSelector);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineImport_ForwardsReplaceAndPath()
    {
        var runtime = new FakeRuntime
        {
            ImportRoutinesResult = new CliExecutionResult(0, "imported")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineImport, Key = "routines.json", ReplaceImport = true, AllowAnyPath = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("routines.json", runtime.LastRoutinePath);
        Assert.True(runtime.LastReplaceImport);
        Assert.True(runtime.LastRoutineAllowAnyPath);
    }

    [Fact]
    public async Task ExecuteAsync_RoutineDisableMissingSelector_ReturnsInvalidUsage()
    {
        var runtime = new FakeRuntime();

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.RoutineDisable },
            runtime);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Missing routine selector.", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigValidateWithWarnings_ReturnsExitCodeFive()
    {
        var runtime = new FakeRuntime
        {
            ConfigValidationResult = (false, "configuration has warnings")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ConfigValidate },
            runtime);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("configuration has warnings", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchDryRun_UsesPreviewPath()
    {
        var runtime = new FakeRuntime
        {
            PreviewResult = (true, "preview-ok")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.SwitchOutput, DryRun = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("preview-ok", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchOutputJsonSuccess_ReturnsJsonPayload()
    {
        var runtime = new FakeRuntime
        {
            SwitchOutputResult = true
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.SwitchOutput, JsonOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Output);
        Assert.Contains("\"diagCode\": \"switch-success\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"output\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchInputJsonSuccess_ReturnsJsonPayload()
    {
        var runtime = new FakeRuntime
        {
            SwitchInputResult = true
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.SwitchInput, JsonOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Output);
        Assert.Contains("\"diagCode\": \"switch-success\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"input\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchRequireCurrentMismatch_ReturnsPreconditionFailure()
    {
        var runtime = new FakeRuntime
        {
            CurrentOutputDeviceId = "actual-device"
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.SwitchOutput, Key = "required-device" },
            runtime);

        Assert.Equal(5, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WaitForDevice_UsesRuntimeAndReturnsResult()
    {
        var runtime = new FakeRuntime
        {
            WaitResult = (true, "found")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.WaitForDevice, Key = "dev-1", Value = "1000" },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("found", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigExportFailure_ReturnsExecutionFailure()
    {
        var runtime = new FakeRuntime
        {
            ExportResult = (false, "export-failed")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ConfigExport, Key = "path.json" },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("export-failed", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigExport_ForwardsAllowAnyPathFlag()
    {
        var runtime = new FakeRuntime
        {
            ExportResult = (true, "exported")
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.ConfigExport, Key = "path.json", AllowAnyPath = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.True(runtime.LastExportAllowAnyPath);
    }

    [Fact]
    public void BuildJsonErrorPayload_UsesCliEnvelopeShape()
    {
        string json = CliCommandExecutor.BuildJsonErrorPayload(5, "precondition-failed", "Precondition failed.");

        var root = JObject.Parse(json);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.NotNull(root["data"]);
        Assert.Equal("precondition-failed", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Precondition failed.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(5, root["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public void BuildRuntimeUnavailableResult_JsonOutput_UsesCliEnvelopeShape()
    {
        CliExecutionResult result = CliCommandExecutor.BuildRuntimeUnavailableResult(jsonOutput: true);

        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.Output);
        var root = JObject.Parse(result.Output!);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.Equal("app-not-ready", root["data"]?["error"]?["code"]?.Value<string>());
    }

    [Fact]
    public async Task ExecuteAsync_MuteMicOnFailureWithJson_ReturnsJsonErrorEnvelope()
    {
        var runtime = new FakeRuntime
        {
            SetMuteMicResult = false,
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.MuteMicOn, JsonOutput = true },
            runtime);

        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.Output);

        var root = JObject.Parse(result.Output!);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.Equal("mute-mic-set-failed", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Failed to set microphone mute.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(3, root["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public async Task ExecuteAsync_MuteMicOnWithJson_ReturnsMuteStatusEnvelope()
    {
        var runtime = new FakeRuntime
        {
            MuteStatusText = CliOutputFormatter.FormatMuteStatus(target: "mic", enabled: true, jsonOutput: true),
        };

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.MuteMicOn, JsonOutput = true },
            runtime);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Output);

        var root = JObject.Parse(result.Output!);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.Equal("mic", root["data"]?["target"]?.Value<string>());
        Assert.True(root["data"]?["enabled"]?.Value<bool>());
        Assert.Equal("mute-mic-status", root["data"]?["diagCode"]?.Value<string>());
    }

    [Fact]
    public async Task ExecuteAsync_VolumeSetMasterInvalidPercentWithJson_ReturnsJsonErrorEnvelope()
    {
        var runtime = new FakeRuntime();

        CliExecutionResult result = await CliCommandExecutor.ExecuteAsync(
            new CliCommand { Action = CliAction.VolumeSetMaster, Value = "101", JsonOutput = true },
            runtime);

        Assert.Equal(2, result.ExitCode);
        Assert.NotNull(result.Output);

        var root = JObject.Parse(result.Output!);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, root["schemaVersion"]?.Value<string>());
        Assert.Equal("invalid-volume-percent", root["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Invalid volume percent. Use a number between 0 and 100.", root["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(2, root["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public async Task ExecuteAsync_ConfigExportThenImportMerge_RoundTripsAndMergesExpectedFields()
    {
        using var workspace = new TestSettingsWorkspace(nameof(CliCommandExecutorTests));
        string exportPath = Path.Combine(workspace.Root, "config-roundtrip.json");
        var runtime = new FileBackedConfigRuntime(new Settings
        {
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+1"
                }
            }
        });

        bool exportParsed = CliCommand.TryParse(["config", "export", exportPath], out CliCommand exportCommand, out string? exportError);
        Assert.True(exportParsed);
        Assert.Null(exportError);

        CliExecutionResult exportResult = await CliCommandExecutor.ExecuteAsync(exportCommand, runtime);

        Assert.Equal(0, exportResult.ExitCode);
        Assert.True(File.Exists(exportPath));

        File.WriteAllText(exportPath, """
        {
          "Theme": "Light"
        }
        """);

        bool importParsed = CliCommand.TryParse(["config", "import", exportPath, "--merge"], out CliCommand importCommand, out string? importError);
        Assert.True(importParsed);
        Assert.Null(importError);

        CliExecutionResult importResult = await CliCommandExecutor.ExecuteAsync(importCommand, runtime);

        Assert.Equal(0, importResult.ExitCode);
        Assert.Equal(AppTheme.Light, runtime.Settings.Theme);
        Assert.True(runtime.Settings.RunAtStartup);
        Assert.Equal("Ctrl+Alt+1", runtime.Settings.DeviceSwitching.Output.SwitchHotkey);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigExportThenImportReplace_RoundTripsAndReplacesExpectedFields()
    {
        using var workspace = new TestSettingsWorkspace(nameof(CliCommandExecutorTests));
        string exportPath = Path.Combine(workspace.Root, "config-roundtrip-replace.json");
        var runtime = new FileBackedConfigRuntime(new Settings
        {
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+1"
                }
            }
        });

        bool exportParsed = CliCommand.TryParse(["config", "export", exportPath], out CliCommand exportCommand, out string? exportError);
        Assert.True(exportParsed);
        Assert.Null(exportError);

        CliExecutionResult exportResult = await CliCommandExecutor.ExecuteAsync(exportCommand, runtime);

        Assert.Equal(0, exportResult.ExitCode);
        Assert.True(File.Exists(exportPath));

        File.WriteAllText(exportPath, """
                    {
                        "SchemaVersion": "1.0.0",
                        "Theme": "Light"
                    }
                    """);

        bool importParsed = CliCommand.TryParse(["config", "import", exportPath, "--replace"], out CliCommand importCommand, out string? importError);
        Assert.True(importParsed);
        Assert.Null(importError);

        CliExecutionResult importResult = await CliCommandExecutor.ExecuteAsync(importCommand, runtime);

        Assert.Equal(0, importResult.ExitCode);
        Assert.Equal(AppTheme.Light, runtime.Settings.Theme);
        Assert.False(runtime.Settings.RunAtStartup);
        Assert.Equal(string.Empty, runtime.Settings.DeviceSwitching.Output.SwitchHotkey);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigImportReplace_RejectsOlderSchemaVersion()
    {
        using var workspace = new TestSettingsWorkspace(nameof(CliCommandExecutorTests));
        string importPath = Path.Combine(workspace.Root, "config-import-old-schema.json");
        var runtime = new FileBackedConfigRuntime(new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.Dark,
            RunAtStartup = false,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+1"
                }
            }
        });

        File.WriteAllText(importPath, """
                    {
                        "SchemaVersion": "0.9.0",
                        "Theme": "Light",
                        "RunAtStartup": true
                    }
                    """);

        bool importParsed = CliCommand.TryParse(["config", "import", importPath, "--replace"], out CliCommand importCommand, out string? importError);
        Assert.True(importParsed);
        Assert.Null(importError);

        CliExecutionResult importResult = await CliCommandExecutor.ExecuteAsync(importCommand, runtime);

        Assert.NotEqual(0, importResult.ExitCode);
        Assert.Contains("config-import-invalid-data", importResult.Output, StringComparison.Ordinal);
        Assert.Contains("unsupported schema version", importResult.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Settings.CurrentSchemaVersion, runtime.Settings.SchemaVersion);
        Assert.Equal(AppTheme.Dark, runtime.Settings.Theme);
        Assert.False(runtime.Settings.RunAtStartup);
    }

    [Fact]
    public async Task ExecuteAsync_ConfigExportZipThenImportReplace_RoundTripsThroughZip()
    {
        using var workspace = new TestSettingsWorkspace(nameof(CliCommandExecutorTests));
        string exportPath = Path.Combine(workspace.Root, "config-roundtrip.zip");
        var runtime = new FileBackedConfigRuntime(new Settings
        {
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+1"
                }
            }
        });

        bool exportParsed = CliCommand.TryParse(["config", "export", exportPath], out CliCommand exportCommand, out string? exportError);
        Assert.True(exportParsed);
        Assert.Null(exportError);

        CliExecutionResult exportResult = await CliCommandExecutor.ExecuteAsync(exportCommand, runtime);

        Assert.Equal(0, exportResult.ExitCode);
        Assert.True(File.Exists(exportPath));

        SettingsTransferService.ExportSettings(new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.Light,
            RunAtStartup = false,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = string.Empty
                }
            }
        }, exportPath);

        bool importParsed = CliCommand.TryParse(["config", "import", exportPath, "--replace"], out CliCommand importCommand, out string? importError);
        Assert.True(importParsed);
        Assert.Null(importError);

        CliExecutionResult importResult = await CliCommandExecutor.ExecuteAsync(importCommand, runtime);

        Assert.Equal(0, importResult.ExitCode);
        Assert.Equal(AppTheme.Light, runtime.Settings.Theme);
        Assert.False(runtime.Settings.RunAtStartup);
        Assert.Equal(string.Empty, runtime.Settings.DeviceSwitching.Output.SwitchHotkey);
    }
    private sealed class FileBackedConfigRuntime(Settings settings) : ICliCommandRuntime
    {
        public Settings Settings { get; private set; } = settings;

        public void ShowWindow() { }
        public void HideWindow() { }
        public void MediaPlayPause() { }
        public void MediaNextTrack() { }
        public void MediaPreviousTrack() { }
        public Task<string> GetMediaStatusAsync(bool jsonOutput, bool redactOutput) => Task.FromResult("media-status");
        public bool ToggleMuteMic() => true;
        public bool SetMuteMic(bool enabled) => true;
        public bool ToggleMuteSound() => true;
        public bool SetMuteSound(bool enabled) => true;
        public bool ToggleDeafen() => true;
        public bool SetDeafen(bool enabled) => true;
        public bool ToggleListenToInput() => true;
        public bool SetListenToInput(bool enabled) => true;
        public string GetMuteStatus(string target, bool jsonOutput) => "mute-status";
        public string GetListenStatus(bool jsonOutput, bool redactOutput) => "listen-status";
        public (bool Success, string Output) GetVolume(bool playback, string? deviceId, bool jsonOutput) => (true, "volume");
        public (bool Success, string Output) SetVolume(bool playback, string? deviceId, float percent, bool jsonOutput) => (true, "volume");
        public string GetRoutineList(bool jsonOutput, bool redactOutput) => CliOutputFormatter.FormatRoutineList(Settings.Routines?.Items ?? [], jsonOutput, redactOutput);
        public Task<CliExecutionResult> RunRoutineAsync(string routineSelector, bool jsonOutput, bool redactOutput) => Task.FromResult(new CliExecutionResult(0, "routine-run"));
        public CliExecutionResult SetRoutineEnabled(string routineSelector, bool enabled, bool jsonOutput, bool redactOutput) => new(0, "routine-updated");
        public CliExecutionResult CreateRoutine(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => new(0, "routine-created");
        public CliExecutionResult UpdateRoutine(string routineSelector, string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => new(0, "routine-updated");
        public CliExecutionResult DeleteRoutine(string routineSelector, bool jsonOutput, bool redactOutput) => new(0, "routine-deleted");
        public CliExecutionResult ImportRoutines(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput) => new(0, "routine-imported");
        public ValueTask<bool> SwitchOutputAsync(bool muteMic, bool muteSound, bool deafen, bool reverse) => ValueTask.FromResult(true);
        public ValueTask<bool> SwitchInputAsync(bool reverse) => ValueTask.FromResult(true);
        public Task RefreshAsync() => Task.CompletedTask;
        public bool SetStartupEnabled(bool enabled) => true;
        public bool OpenStartupSettings() => true;
        public string GetStartupStatus(bool jsonOutput) => "enabled";
        public string GetStatus(bool jsonOutput, bool redactOutput) => "status";
        public string GetDiagnosticsStatus(bool jsonOutput, bool showPaths, bool redactOutput) => "diagnostics";
        public string GetDiagnosticsHistory(bool jsonOutput, int? limit, string? type, bool redactOutput) => "history";
        public (bool Found, string Output) GetDiagnosticsHistoryDetail(string opId, bool jsonOutput, bool redactOutput) => (false, "history-detail-missing");
        public string GetDeviceList(bool output, bool jsonOutput, bool redactOutput) => "devices";
        public (bool Found, string Output) GetDevice(bool output, string selector, bool jsonOutput, bool redactOutput) => (true, "device");
        public (bool Found, string Output) FindDevices(bool output, string query, bool jsonOutput, bool redactOutput) => (true, "device-find");
        public string GetCycle(bool output, bool jsonOutput, bool redactOutput) => "cycle";
        public (bool IsValid, string Output) GetCycleValidation(bool output, bool jsonOutput, bool redactOutput) => (true, "valid");
        public (bool CanSwitch, string Output) GetCycleTest(bool output, bool jsonOutput, bool redactOutput) => (true, "ok");
        public (bool Success, string Output) AddCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => (true, "cycle-added");
        public (bool Success, string Output) RemoveCycleDevice(bool output, string deviceId, bool jsonOutput, bool redactOutput) => (true, "cycle-removed");
        public (bool Success, string Output) ReorderCycle(bool output, IReadOnlyList<string> deviceIds, bool jsonOutput, bool redactOutput) => (true, "cycle-reordered");
        public (bool CanSwitch, string Output) PreviewSwitch(bool output, bool reverse, bool jsonOutput, bool redactOutput) => (true, "preview");
        public string? GetCurrentDeviceId(bool output) => output ? "out-1" : "in-1";
        public Task<(bool Found, string Output)> WaitForDeviceAsync(string deviceId, int timeoutMs, bool outputOnly, bool inputOnly, bool jsonOutput, bool redactOutput) => Task.FromResult((true, "found"));
        public (bool Found, string? Value, string? Error) GetConfig(string key) => (false, null, "not-used");
        public string GetConfigList(bool jsonOutput) => CliOutputFormatter.FormatSupportedKeyList("config", CliConfigManager.GetKnownKeys(), jsonOutput);
        public (bool Updated, string? Error) SetConfig(string key, string value) => (false, "not-used");
        public (bool Found, string? Value, string? Error) GetRuntime(string key) => (false, null, "not-used");
        public string GetRuntimeList(bool jsonOutput) => CliOutputFormatter.FormatSupportedKeyList("runtime", CliRuntimeManager.GetKnownKeys(), jsonOutput);
        public (bool Updated, string? Error) SetRuntime(string key, string value) => (false, "not-used");
        public (bool IsValid, string Output) GetConfigValidation(bool jsonOutput, bool redactOutput) => (true, "valid");
        public (bool Success, string Output) ExportLogs(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool jsonOutput, bool redactOutput) => (true, "logs-exported");
        public Task<(bool Success, string Output)> ExportDiagnosticBundleAsync(string path, bool allowAnyPath, CliDiagnosticsExportDetailLevel detailLevel, bool includeSensitive, bool jsonOutput) => Task.FromResult((true, "bundle-exported"));
        public (bool Success, string Output) ResetPerAppAudioRouting(bool jsonOutput) => (true, "per-app-reset");
        public (bool Success, string Output) ExportRoutines(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput) => (true, "routines-exported");

        public (bool Success, string Output) ExportConfig(string path, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                SettingsTransferService.ExportSettings(Settings, fullPath);
                return (true, "exported");
            }
            catch
            {
                return (false, "export-failed");
            }
        }

        public Task<string> GetNetworkListAsync(bool jsonOutput, bool redactOutput) => Task.FromResult(jsonOutput ? "{\"Success\":true,\"Count\":0,\"Networks\":[]}" : "No networks detected.");

        public (bool Success, string Output) ImportConfig(string path, bool replaceImport, bool allowAnyPath, bool jsonOutput, bool redactOutput)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    return (false, "import-file-missing");
                }

                string importJson = SettingsTransferService.ReadImportText(fullPath);
                Settings imported = SettingsTransferService.ParseImportedSettings(importJson, Settings, replaceImport);
                Settings = imported;
                return (true, "imported");
            }
            catch (InvalidDataException ex)
            {
                return BuildConfigImportFailure("config-import-invalid-data", ex.Message, jsonOutput);
            }
            catch (JsonException)
            {
                return BuildConfigImportFailure("config-import-invalid-json", "Imported config is not valid JSON.", jsonOutput);
            }
            catch
            {
                return BuildConfigImportFailure("config-import-failed", "Failed to import config.", jsonOutput);
            }
        }

        private static (bool Success, string Output) BuildConfigImportFailure(string diagCode, string message, bool jsonOutput)
        {
            return jsonOutput
                ? (false, CliOutputFormatter.SerializeCliJson(new { Success = false, DiagCode = diagCode, Error = message }))
                : (false, $"[diag-code:{diagCode}] {message}");
        }
    }
}
