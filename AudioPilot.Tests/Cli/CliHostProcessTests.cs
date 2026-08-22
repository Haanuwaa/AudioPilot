using System.Diagnostics;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

[Collection("CliHostProcess")]
public sealed class CliHostProcessTests
{
    [Fact]
    public void ProcessBoundary_UnexpectedFailure_ReturnsStablePrivacySafeTextError()
    {
        const string sensitiveDetails = @"C:\Users\ExampleUser\secret.json";
        var error = new StringWriter();
        var metadata = new List<string>();

        int exitCode = CliHostProcessBoundary.Execute(
            ["status"],
            error,
            () => throw new InvalidOperationException(sensitiveDetails),
            metadata.Add);

        Assert.Equal(CliHostProcessBoundary.UnexpectedFailureExitCode, exitCode);
        Assert.Contains("[diag-code:internal-error]", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetails, error.ToString(), StringComparison.Ordinal);
        string loggedMetadata = Assert.Single(metadata);
        Assert.Contains("exceptionType=InvalidOperationException", loggedMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetails, loggedMetadata, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessBoundary_UnexpectedFailure_PreservesJsonErrorContract()
    {
        var error = new StringWriter();

        int exitCode = CliHostProcessBoundary.Execute(
            ["status", "--json"],
            error,
            static () => throw new FormatException("sensitive parser input"));

        Assert.Equal(CliHostProcessBoundary.UnexpectedFailureExitCode, exitCode);
        JObject parsed = JObject.Parse(error.ToString());
        Assert.Equal("internal-error", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(CliHostProcessBoundary.UnexpectedFailureExitCode, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
        Assert.DoesNotContain("sensitive parser input", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliHost_InvalidUsage_ReturnsExitCodeTwo()
    {
        ProcessResult result = await RunCliAsync(["devices"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("[diag-code:invalid-usage] Missing devices arguments", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AudioPilot.Cli.exe help devices", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", result.StdErr, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));
    }

    [Fact]
    public async Task CliHost_InvalidUsageSuggestion_IsSurfacedInTextMode()
    {
        ProcessResult result = await RunCliAsync(["help", "volum"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Did you mean 'volume'", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));
    }

    [Fact]
    public async Task CliHost_InvalidUsageJson_WritesJsonErrorEnvelope()
    {
        ProcessResult result = await RunCliAsync(["devices", "--json"]);

        Assert.Equal(2, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));
        Assert.DoesNotContain("Usage:", result.StdErr, StringComparison.Ordinal);

        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, parsed["schemaVersion"]?.Value<string>());
        Assert.Equal("invalid-usage", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Contains("Missing devices arguments", parsed["data"]?["error"]?["message"]?.Value<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public async Task CliHost_Help_WritesUsageTextToStdOut()
    {
        ProcessResult result = await RunCliAsync(["help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("AudioPilot CLI", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("audio-pilot status [--json] [--redact]", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public async Task CliHost_InternalDocsSyncCheck_WritesInSyncStatus()
    {
        ProcessResult result = await RunCliAsync(["internal-docs-sync", "--check"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("docs\\CLI.md is in sync", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public void CliHost_CompletionPowerShell_WritesScriptToStdOut()
    {
        var singleInstance = new FakeCliHostSingleInstance();

        RuntimeResult result = RunRuntime(["completion", "powershell"], singleInstance: singleInstance);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Register-ArgumentCompleter -Native", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
        Assert.Null(singleInstance.ReceivedPayload);
    }

    [Fact]
    public void CliHost_CompletionBash_WritesScriptToStdOut()
    {
        RuntimeResult result = RunRuntime(["completion", "bash"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("complete -F _audiopilot_complete AudioPilot.Cli.exe audio-pilot", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public void CliHost_HelpVersionAndCompletion_DoNotCreateHeadlessRunner()
    {
        int headlessRunnerCreations = 0;
        var dependencies = new CliHostRuntimeDependencies(
            () => new FakeCliHostSingleInstance(),
            () =>
            {
                headlessRunnerCreations++;
                return new FakeCliHeadlessRunner();
            },
            () => false,
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, CliHostRuntime.Execute(["help"], dependencies));
        Assert.Equal(0, CliHostRuntime.Execute(["version"], dependencies));
        Assert.Equal(0, CliHostRuntime.Execute(["completion", "powershell"], dependencies));
        Assert.Equal(0, headlessRunnerCreations);
    }

    [Fact]
    public void CliHost_HelpTopic_WritesTopicHelpToStdOut()
    {
        RuntimeResult result = RunRuntime(["help", "diagnostics"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("AudioPilot CLI - diagnostics", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("audio-pilot diagnostics export-logs", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("audio-pilot diagnostics reset-per-app-audio", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("audio-pilot routine list", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public void CliHost_NewHelpTopicAlias_WritesTopicHelpToStdOut()
    {
        RuntimeResult result = RunRuntime(["volume", "help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("AudioPilot CLI - volume", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("audio-pilot volume get master|mic", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("audio-pilot routine list", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public void CliHost_SwitchHelp_WritesExactUnsupportedFlagGuidance()
    {
        RuntimeResult result = RunRuntime(["help", "switch"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen.", result.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StdErr));
    }

    [Fact]
    public void CliHost_CycleAndWaitHelp_WritesValidationScopeNotes()
    {
        RuntimeResult cycleResult = RunRuntime(["help", "cycle"]);
        RuntimeResult waitResult = RunRuntime(["help", "wait"]);

        Assert.Equal(0, cycleResult.ExitCode);
        Assert.Contains("cycle reorder expects the full current cycle device list in the new order; the parser rejects blank or duplicate ids, and execution verifies the configured cycle membership.", cycleResult.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(cycleResult.StdErr));

        Assert.Equal(0, waitResult.ExitCode);
        Assert.Contains("Use --output or --input to scope the wait to one device class; the parser rejects passing both flags together, and omitting both lets either class satisfy the wait.", waitResult.StdOut, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(waitResult.StdErr));
    }

    [Fact]
    public async Task CliHost_ForwardsToRunningInstance_AndReturnsForwardedExitCodeAndOutput()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 5,
            LastSignalOutputValue = "forwarded-precondition",
        };

        RuntimeResult result = RunRuntime(["status"], singleInstance: singleInstance);

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("forwarded-precondition", result.StdOut, StringComparison.OrdinalIgnoreCase);

        string payload = Assert.IsType<string>(singleInstance.ReceivedPayload);
        Assert.StartsWith("{", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliHost_ForwardsRoutineRun_ToRunningInstance()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 0,
            LastSignalOutputValue = "forwarded-routine-ok",
        };

        RuntimeResult result = RunRuntime(["routine", "run", "Desk", "--json"], singleInstance: singleInstance);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("forwarded-routine-ok", result.StdOut, StringComparison.OrdinalIgnoreCase);

        string payload = Assert.IsType<string>(singleInstance.ReceivedPayload);
        Assert.True(CliCommand.TryFromPipePayload(payload, out CliCommand? command));
        Assert.Equal(CliAction.RoutineRun, command.Action);
        Assert.True(command.JsonOutput);
        Assert.Equal("Desk", command.Key);
    }

    [Fact]
    public void CliHost_ForwardedInvalidPayloadMessage_IsSurfaced()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 6,
            LastSignalErrorCodeValue = "forwarded-protocol-mismatch",
            LastSignalErrorMessageValue = "The running AudioPilot instance uses an incompatible CLI forwarding protocol.",
            LastSignalProtocolVersionValue = 1,
        };

        RuntimeResult result = RunRuntime(["status"], singleInstance: singleInstance);

        Assert.Equal(6, result.ExitCode);
        Assert.Contains("incompatible CLI forwarding protocol", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliHost_ForwardsRoutineRunJsonPayload_ToRunningInstance()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-discord",
            Name = "Discord",
            Enabled = true,
            OutputDeviceId = "out-2",
            OutputDeviceName = "Headset",
            TriggerKind = RoutineTriggerKind.SteamBigPicture,
            RestorePreviousAudioOnDeactivate = true,
            ShowInTrayMenu = true,
        };

        string forwardedJson = CliOutputFormatter.FormatRoutineRunResult(routine, "Headset", null, jsonOutput: true);
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 0,
            LastSignalOutputValue = forwardedJson,
        };

        RuntimeResult result = RunRuntime(["routine", "run", "Discord", "--json"], singleInstance: singleInstance);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdOut));

        JObject parsed = JObject.Parse(result.StdOut);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, parsed["schemaVersion"]?.Value<string>());
        JToken data = Assert.IsType<JObject>(parsed["data"]);
        Assert.Equal("routine-discord", data["id"]?.Value<string>());
        Assert.Equal("Steam Big Picture", data["triggerMode"]?.Value<string>());
        Assert.False(data["usesApplicationTrigger"]?.Value<bool>());
        Assert.Equal(string.Empty, data["triggerAppPath"]?.Value<string>());
        Assert.True(data["restorePreviousAudioOnDeactivate"]?.Value<bool>());
        Assert.False(data["switchOutputPerApp"]?.Value<bool>());
        Assert.False(data["showInTrayMenu"]?.Value<bool>());
        Assert.Equal("Steam Big Picture | Restore on exit", data["triggerSummary"]?.Value<string>());
        AssertRoutineTimingFieldsOmitted((JObject)data);
        Assert.Equal("Headset", data["appliedOutputDeviceName"]?.Value<string>());

        string payload = Assert.IsType<string>(singleInstance.ReceivedPayload);
        Assert.True(CliCommand.TryFromPipePayload(payload, out CliCommand? command));
        Assert.Equal(CliAction.RoutineRun, command.Action);
        Assert.True(command.JsonOutput);
        Assert.Equal("Discord", command.Key);
    }

    private static void AssertRoutineTimingFieldsOmitted(JObject data)
    {
        Assert.False(data.ContainsKey("executionDelayMs"));
        Assert.False(data.ContainsKey("cooldownSeconds"));
        Assert.False(data.ContainsKey("triggerAppStableForMs"));
        Assert.False(data.ContainsKey("timingPreset"));
        Assert.False(data.ContainsKey("timingSummary"));
    }

    [Fact]
    public void CliHost_ForwardsDiagnosticsHistory_ToRunningInstance()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 0,
            LastSignalOutputValue = "forwarded-history-ok",
        };

        RuntimeResult result = RunRuntime(["diagnostics", "history", "--limit", "7", "--type", "switch", "--json", "--redact"], singleInstance: singleInstance);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("forwarded-history-ok", result.StdOut, StringComparison.OrdinalIgnoreCase);

        string payload = Assert.IsType<string>(singleInstance.ReceivedPayload);
        Assert.True(CliCommand.TryFromPipePayload(payload, out CliCommand? command));
        Assert.Equal(CliAction.DiagnosticsHistory, command.Action);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
        Assert.Equal(7, command.Limit);
        Assert.Equal("switch", command.Key);
    }

    [Fact]
    public void CliHost_ForwardsDiagnosticsHistoryDetail_ToRunningInstance()
    {
        string forwardedJson = CliOutputFormatter.FormatExecutionHistoryDetail(
            new ExecutionHistoryEntry(
                OpId: "op-forwarded-1",
                TimestampUtc: new DateTimeOffset(2026, 3, 21, 12, 0, 0, TimeSpan.Zero),
                Kind: ExecutionHistoryKind.Mute,
                Source: "cli",
                Action: "mute-mic-on",
                Success: true,
                Skipped: false,
                Summary: "Microphone mute enabled.",
                Target: "mic",
                Enabled: true),
            jsonOutput: true);

        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 0,
            LastSignalOutputValue = forwardedJson,
        };

        RuntimeResult result = RunRuntime(["diagnostics", "history-detail", "op-forwarded-1", "--json", "--redact"], singleInstance: singleInstance);

        Assert.Equal(0, result.ExitCode);
        JObject parsed = JObject.Parse(result.StdOut);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, parsed["schemaVersion"]?.Value<string>());
        Assert.Equal("op-forwarded-1", parsed["data"]?["opId"]?.Value<string>());
        Assert.Equal("mute-mic-on", parsed["data"]?["action"]?.Value<string>());

        string payload = Assert.IsType<string>(singleInstance.ReceivedPayload);
        Assert.True(CliCommand.TryFromPipePayload(payload, out CliCommand? command));
        Assert.Equal(CliAction.DiagnosticsHistoryDetail, command.Action);
        Assert.True(command.JsonOutput);
        Assert.True(command.RedactOutput);
        Assert.Equal("op-forwarded-1", command.Key);
    }

    [Fact]
    public async Task CliHost_ShowWithoutUiHost_ReturnsUiHostUnavailableExitCode()
    {
        RuntimeResult result = RunRuntime(["show"]);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("[diag-code:ui-host-unavailable]", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("No running UI host instance is available", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliHost_ForwardingFailure_ReturnsExitCodeFour()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = false,
            LastSignalFailureKindValue = SingleInstanceSignalFailureKind.None,
        };

        RuntimeResult result = RunRuntime(["status"], singleInstance: singleInstance);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("[diag-code:forwarding-failed]", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliHost_ForwardingFailureJson_WritesJsonErrorEnvelope()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = false,
            LastSignalFailureKindValue = SingleInstanceSignalFailureKind.None,
        };

        RuntimeResult result = RunRuntime(["status", "--json"], singleInstance: singleInstance);

        Assert.Equal(4, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));

        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal("forwarding-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(4, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public void CliHost_UnresponsiveUiHost_ReturnsDistinctDiagnostics()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = false,
            LastSignalFailureKindValue = SingleInstanceSignalFailureKind.ConnectionFailed,
        };

        RuntimeResult result = RunRuntime(["status"], singleInstance: singleInstance);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("[diag-code:ui-host-unresponsive]", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("not responding", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CliHost_InvalidUiHostForwardingResponse_ReturnsDistinctDiagnostics()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = false,
            LastSignalFailureKindValue = SingleInstanceSignalFailureKind.InvalidResponse,
        };

        RuntimeResult result = RunRuntime(["status", "--json"], singleInstance: singleInstance);

        Assert.Equal(4, result.ExitCode);
        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal("ui-host-invalid-response", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(4, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public async Task CliHost_HeadlessRuntimeFailure_ReturnsExitCodeThree()
    {
        RuntimeResult result = RunRuntime(["status"], forceHeadlessFailure: true);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("[diag-code:headless-runtime-failed]", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("Headless command execution failed for 'Status'.", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliHost_HeadlessRunnerExceptionJson_WritesJsonErrorEnvelope()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            ExceptionToThrow = new InvalidOperationException("boom")
        };

        RuntimeResult result = RunRuntime(["status", "--json"], headlessRunner: headlessRunner);

        Assert.Equal(7, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));

        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal("headless-runtime-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(7, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public void CliHost_HeadlessRunnerIOException_UsesMappedDiagnostics()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            ExceptionToThrow = new IOException("disk full")
        };

        RuntimeResult result = RunRuntime(["status"], headlessRunner: headlessRunner);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("[diag-code:headless-io-failed]", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("reading or writing required files", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CliHost_HeadlessRunnerIOExceptionJson_UsesMappedDiagnostics()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            ExceptionToThrow = new IOException("disk full")
        };

        RuntimeResult result = RunRuntime(["status", "--json"], headlessRunner: headlessRunner);

        Assert.Equal(7, result.ExitCode);
        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal("headless-io-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(7, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public void CliHost_ForwardedRefreshFailure_ReturnsRefreshContract()
    {
        var singleInstance = new FakeCliHostSingleInstance
        {
            TryAcquireResult = false,
            LastSignalExistingSucceededValue = true,
            LastSignalExitCodeValue = 7,
            LastSignalErrorCodeValue = "refresh-failed",
            LastSignalErrorMessageValue = "Refresh command failed.",
            LastSignalProtocolVersionValue = 1,
        };

        RuntimeResult result = RunRuntime(["refresh", "--json"], singleInstance: singleInstance);

        Assert.Equal(7, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));

        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal("refresh-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Refresh command failed.", parsed["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(7, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public void CliHost_HeadlessRefreshFailure_ReturnsRefreshContract()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            ExceptionToThrow = new InvalidOperationException("refresh boom")
        };

        RuntimeResult result = RunRuntime(["refresh"], headlessRunner: headlessRunner);

        Assert.Equal(7, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));
        Assert.Contains("[diag-code:refresh-failed]", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("Refresh command failed.", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CliHost_HeadlessRefreshFailureJson_WritesRefreshErrorEnvelope()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            ExceptionToThrow = new InvalidOperationException("refresh boom")
        };

        RuntimeResult result = RunRuntime(["refresh", "--json"], headlessRunner: headlessRunner);

        Assert.Equal(7, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StdOut));

        JObject parsed = JObject.Parse(result.StdErr);
        Assert.Equal("refresh-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal("Refresh command failed.", parsed["data"]?["error"]?["message"]?.Value<string>());
        Assert.Equal(7, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

    [Fact]
    public void CliHost_HeadlessRunnerResult_IsWrittenToStdOut()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            Result = new CliExecutionResult(0, "Desk [enabled] | Output: Speakers"),
        };

        RuntimeResult result = RunRuntime(["routine", "list"], headlessRunner: headlessRunner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Desk [enabled]", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Output: Speakers", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CliAction.RoutineList, headlessRunner.ReceivedCommand?.Action);
    }

    [Fact]
    public void CliHost_HeadlessRunnerJsonResult_IsPassedThrough()
    {
        JObject envelope = JObject.FromObject(new
        {
            schemaVersion = CliOutputFormatter.JsonSchemaVersion,
            data = new
            {
                routines = new[]
                {
                    new
                    {
                        id = "routine-discord",
                        triggerMode = "Steam Big Picture",
                        targetSummary = "Output: Headset"
                    }
                }
            }
        });

        var headlessRunner = new FakeCliHeadlessRunner
        {
            Result = new CliExecutionResult(0, envelope.ToString(Formatting.None)),
        };

        RuntimeResult result = RunRuntime(["routine", "list", "--json"], headlessRunner: headlessRunner);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdOut));

        JObject parsed = JObject.Parse(result.StdOut);
        Assert.Equal(CliOutputFormatter.JsonSchemaVersion, parsed["schemaVersion"]?.Value<string>());

        JArray routines = Assert.IsType<JArray>(parsed["data"]?["routines"]);
        JToken routine = Assert.Single(routines);
        Assert.Equal("routine-discord", routine["id"]?.Value<string>());
        Assert.Equal("Steam Big Picture", routine["triggerMode"]?.Value<string>());
        Assert.Equal("Output: Headset", routine["targetSummary"]?.Value<string>());
    }

    [Fact]
    public void CliHost_RoutineCreateWithoutUiHost_RunsHeadless()
    {
        var headlessRunner = new FakeCliHeadlessRunner
        {
            Result = new CliExecutionResult(0, "created")
        };

        RuntimeResult result = RunRuntime(["routine", "create", "routine.json"], headlessRunner: headlessRunner);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CliAction.RoutineCreate, headlessRunner.ReceivedCommand?.Action);
        Assert.Equal("routine.json", headlessRunner.ReceivedCommand?.Key);
    }

    private static RuntimeResult RunRuntime(
        string[] args,
        FakeCliHostSingleInstance? singleInstance = null,
        FakeCliHeadlessRunner? headlessRunner = null,
        bool forceHeadlessFailure = false)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        singleInstance ??= new FakeCliHostSingleInstance();
        headlessRunner ??= new FakeCliHeadlessRunner();

        int exitCode = CliHostRuntime.Execute(
            args,
            new CliHostRuntimeDependencies(
                () => singleInstance,
                () => headlessRunner,
                () => forceHeadlessFailure,
                output,
                error));

        return new RuntimeResult(exitCode, output.ToString(), error.ToString());
    }

    private static async Task<ProcessResult> RunCliAsync(string[] args, IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        string cliPath = ResolveCliPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environmentVariables != null)
        {
            foreach ((string key, string value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        bool started = process.Start();
        Assert.True(started, "Failed to start CLI host process.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string ResolveCliPath()
    {
        string cliPath = Path.Combine(AppContext.BaseDirectory, "AudioPilot.Cli.exe");
        Assert.True(File.Exists(cliPath), $"CLI executable not found: {cliPath}");

        return cliPath;
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    private readonly record struct RuntimeResult(int ExitCode, string StdOut, string StdErr);

}

[CollectionDefinition("CliHostProcess", DisableParallelization = true)]
public sealed class CliHostProcessCollection
{
}
