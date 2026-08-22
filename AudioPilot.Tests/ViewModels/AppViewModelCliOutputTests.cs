using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelCliOutputTests
{
    [Fact]
    public void FormatStartupStatus_Text_ReturnsEnabledOrDisabled()
    {
        Assert.Equal("enabled", AppViewModel.FormatStartupStatus(startupEnabled: true, jsonOutput: false));
        Assert.Equal("disabled", AppViewModel.FormatStartupStatus(startupEnabled: false, jsonOutput: false));
    }

    [Fact]
    public void FormatStartupStatus_Json_ReturnsBooleanProperty()
    {
        string json = AppViewModel.FormatStartupStatus(startupEnabled: true, jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.0.0", doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("startupEnabled").GetBoolean());
    }

    [Fact]
    public void FormatStatusSnapshot_Text_ReturnsExpectedLines()
    {
        string text = AppViewModel.FormatStatusSnapshot(
            startupEnabled: true,
            availableOutputDevices: 3,
            availableInputDevices: 2,
            configuredOutputCycleDevices: 2,
            configuredInputCycleDevices: 1,
            currentInputListenEnabled: true,
            listenMonitorTargetOutputDeviceName: "Speakers",
            jsonOutput: false);

        string[] lines = text.Split(Environment.NewLine);
        Assert.Equal(7, lines.Length);
        Assert.Equal("startup: enabled", lines[0]);
        Assert.Equal("available output devices: 3", lines[1]);
        Assert.Equal("available input devices: 2", lines[2]);
        Assert.Equal("configured output cycle devices: 2", lines[3]);
        Assert.Equal("configured input cycle devices: 1", lines[4]);
        Assert.Equal("listen to input: enabled", lines[5]);
        Assert.Equal("listen monitor target output: Speakers", lines[6]);
    }

    [Fact]
    public void FormatStatusSnapshot_Json_ReturnsExpectedProperties()
    {
        string json = AppViewModel.FormatStatusSnapshot(
            startupEnabled: false,
            availableOutputDevices: 4,
            availableInputDevices: 1,
            configuredOutputCycleDevices: 3,
            configuredInputCycleDevices: 0,
            currentInputListenEnabled: false,
            listenMonitorTargetOutputDeviceName: "Headset",
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.False(root.GetProperty("startupEnabled").GetBoolean());
        Assert.Equal(4, root.GetProperty("availableOutputDevices").GetInt32());
        Assert.Equal(1, root.GetProperty("availableInputDevices").GetInt32());
        Assert.Equal(3, root.GetProperty("configuredOutputCycleDevices").GetInt32());
        Assert.Equal(0, root.GetProperty("configuredInputCycleDevices").GetInt32());
        Assert.False(root.GetProperty("currentInputListenEnabled").GetBoolean());
        Assert.Equal("Headset", root.GetProperty("listenMonitorTargetOutputDeviceName").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public void FormatStatusSnapshot_Text_Redact_ReplacesSensitiveValues()
    {
        string text = AppViewModel.FormatStatusSnapshot(
            startupEnabled: true,
            availableOutputDevices: 3,
            availableInputDevices: 2,
            configuredOutputCycleDevices: 2,
            configuredInputCycleDevices: 1,
            currentInputListenEnabled: true,
            listenMonitorTargetOutputDeviceName: "Speakers",
            warnings: ["Output switch hotkey value 'BadHotkey' is invalid."],
            jsonOutput: false,
            redactOutput: true);

        Assert.Contains("listen monitor target output: device[", text);
        Assert.DoesNotContain("Speakers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BadHotkey", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStatusSnapshot_Text_WithWarnings_AppendsWarningSection()
    {
        string text = AppViewModel.FormatStatusSnapshot(
            startupEnabled: true,
            availableOutputDevices: 2,
            availableInputDevices: 1,
            configuredOutputCycleDevices: 1,
            configuredInputCycleDevices: 1,
            currentInputListenEnabled: null,
            listenMonitorTargetOutputDeviceName: null,
            warnings:
            [
                "Output switch hotkey value 'BadHotkey' is invalid. Set a valid combination."
            ],
            jsonOutput: false);

        Assert.Contains("warnings:", text);
        Assert.Contains("- Output switch hotkey", text);
    }

    [Fact]
    public void FormatStatusSnapshot_Json_WithWarnings_IncludesWarningsArray()
    {
        string json = AppViewModel.FormatStatusSnapshot(
            startupEnabled: false,
            availableOutputDevices: 1,
            availableInputDevices: 1,
            configuredOutputCycleDevices: 0,
            configuredInputCycleDevices: 0,
            currentInputListenEnabled: null,
            listenMonitorTargetOutputDeviceName: null,
            warnings: ["warning-a", "warning-b"],
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        JsonElement warnings = root.GetProperty("warnings");
        Assert.Equal(2, warnings.GetArrayLength());
        Assert.Equal("warning-a", warnings[0].GetString());
        Assert.Equal("warning-b", warnings[1].GetString());
    }

    [Fact]
    public void FormatStatusSnapshot_WithBluetoothReconnectSettings_IncludesReconnectFields()
    {
        string text = AppViewModel.FormatStatusSnapshot(
            startupEnabled: true,
            availableOutputDevices: 2,
            availableInputDevices: 1,
            configuredOutputCycleDevices: 2,
            configuredInputCycleDevices: 1,
            currentInputListenEnabled: null,
            listenMonitorTargetOutputDeviceName: null,
            warnings: [],
            bluetoothReconnectEnabled: true,
            bluetoothReconnectMaxAttempts: AppConstants.Bluetooth.ReconnectMaxAttemptsDefault,
            bluetoothReconnectAttemptTimeoutMs: 1200,
            bluetoothReconnectCooldownMs: 5000,
            bluetoothReconnectOnlyLikely: true,
            outputSwitchDebounceMs: 125,
            inputSwitchDebounceMs: 150,
            switchRetryDelayMs: 50,
            switchRetryMaxDelayMs: 100,
            switchMaxRetries: 3,
            hotplugRefreshDebounceMs: 350,
            hotplugConnectedOverlaySuppressAfterSwitchMs: 2200,
            mixerSessionRefreshDebounceMs: 250,
            mixerSnapshotCacheInteractiveMs: 100,
            mixerSnapshotCacheBackgroundMs: 300,
            resumeHotkeyRetryDelayMs: 300,
            mixerDiagnosticsSummaryWindowSeconds: 30,
            mixerCacheWindowDiagnosticsLogEveryNRefreshes: 20,
            bluetoothReconnectSuccessObservedRecheckIntervalMs: 220,
            bluetoothReconnectTimeoutCircuitThreshold: 2,
            bluetoothReconnectTimeoutCircuitOpenMs: 180000,
            jsonOutput: false);

        Assert.Contains("bluetooth reconnect enabled: True", text);
        Assert.Contains($"bluetooth reconnect max attempts: {AppConstants.Bluetooth.ReconnectMaxAttemptsDefault}", text);
        Assert.Contains("bluetooth reconnect attempt timeout ms: 1200", text);
        Assert.Contains("runtime output switch debounce ms: 125", text);
    }

    [Fact]
    public void FormatStatusSnapshot_Json_WithBluetoothReconnectSettings_IncludesReconnectObject()
    {
        string json = AppViewModel.FormatStatusSnapshot(
            startupEnabled: true,
            availableOutputDevices: 2,
            availableInputDevices: 1,
            configuredOutputCycleDevices: 2,
            configuredInputCycleDevices: 1,
            currentInputListenEnabled: null,
            listenMonitorTargetOutputDeviceName: null,
            warnings: [],
            bluetoothReconnectEnabled: true,
            bluetoothReconnectMaxAttempts: AppConstants.Bluetooth.ReconnectMaxAttemptsDefault,
            bluetoothReconnectAttemptTimeoutMs: 1200,
            bluetoothReconnectCooldownMs: 5000,
            bluetoothReconnectOnlyLikely: true,
            outputSwitchDebounceMs: 125,
            inputSwitchDebounceMs: 150,
            switchRetryDelayMs: 50,
            switchRetryMaxDelayMs: 100,
            switchMaxRetries: 3,
            hotplugRefreshDebounceMs: 350,
            hotplugConnectedOverlaySuppressAfterSwitchMs: 2200,
            mixerSessionRefreshDebounceMs: 250,
            mixerSnapshotCacheInteractiveMs: 100,
            mixerSnapshotCacheBackgroundMs: 300,
            resumeHotkeyRetryDelayMs: 300,
            mixerDiagnosticsSummaryWindowSeconds: 30,
            mixerCacheWindowDiagnosticsLogEveryNRefreshes: 20,
            bluetoothReconnectSuccessObservedRecheckIntervalMs: 220,
            bluetoothReconnectTimeoutCircuitThreshold: 2,
            bluetoothReconnectTimeoutCircuitOpenMs: 180000,
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement reconnect = doc.RootElement.GetProperty("data").GetProperty("bluetoothReconnect");
        JsonElement runtime = doc.RootElement.GetProperty("data").GetProperty("runtimeTuning");
        Assert.True(reconnect.GetProperty("enabled").GetBoolean());
        Assert.Equal(1200, reconnect.GetProperty("attemptTimeoutMs").GetInt32());
        Assert.Equal(125, runtime.GetProperty("outputSwitchDebounceMs").GetInt32());
    }

    [Fact]
    public void FormatDiagnosticsStatus_Text_ReturnsExpectedSummaryLines()
    {
        string text = AppViewModel.FormatDiagnosticsStatus(
            logFilePath: "C:/logs/AudioPilot.log",
            logFileExists: true,
            logFileBytes: 1024,
            logBackupDirectory: "C:/logs/backups",
            logBackupFiles: ["AudioPilot.log.bak"],
            logBackupRetentionCount: 5,
            logBackupMaxAgeDays: 14,
            settingsPath: "C:/cfg/settings.json",
            settingsBackupDirectory: "C:/cfg/backups",
            settingsBackupFiles: ["settings.json.bak", "settings.json.bak.1"],
            settingsBackupRetentionCount: 5,
            jsonOutput: false);

        Assert.Contains("log file: AudioPilot.log", text);
        Assert.Contains("log backups: 1", text);
        Assert.Contains("settings backups: 2", text);
        Assert.Contains("settings retention: count=5", text);
        Assert.Contains("paths redacted: True", text);
    }

    [Fact]
    public void FormatDiagnosticsStatus_Json_ContainsDiagnosticsFields()
    {
        string json = AppViewModel.FormatDiagnosticsStatus(
            logFilePath: "C:/logs/AudioPilot.log",
            logFileExists: false,
            logFileBytes: 0,
            logBackupDirectory: "C:/logs/backups",
            logBackupFiles: [],
            logBackupRetentionCount: 5,
            logBackupMaxAgeDays: 14,
            settingsPath: "C:/cfg/settings.json",
            settingsBackupDirectory: "C:/cfg/backups",
            settingsBackupFiles: [],
            settingsBackupRetentionCount: 5,
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.Equal("AudioPilot.log", root.GetProperty("logFilePath").GetString());
        Assert.False(root.GetProperty("logFileExists").GetBoolean());
        Assert.True(root.GetProperty("pathsRedacted").GetBoolean());
        Assert.Equal(5, root.GetProperty("logBackupRetentionCount").GetInt32());
        Assert.Equal(14, root.GetProperty("logBackupMaxAgeDays").GetInt32());
        Assert.Equal(5, root.GetProperty("settingsBackupRetentionCount").GetInt32());
    }

    [Fact]
    public void FormatDiagnosticsStatus_Json_WithSensitivePathsEnabled_KeepsFullPaths()
    {
        string json = AppViewModel.FormatDiagnosticsStatus(
            logFilePath: "C:/logs/AudioPilot.log",
            logFileExists: false,
            logFileBytes: 0,
            logBackupDirectory: "C:/logs/backups",
            logBackupFiles: [],
            logBackupRetentionCount: 5,
            logBackupMaxAgeDays: 14,
            settingsPath: "C:/cfg/settings.json",
            settingsBackupDirectory: "C:/cfg/backups",
            settingsBackupFiles: [],
            settingsBackupRetentionCount: 5,
            jsonOutput: true,
            includeSensitivePaths: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.Equal("C:/logs/AudioPilot.log", root.GetProperty("logFilePath").GetString());
        Assert.False(root.GetProperty("pathsRedacted").GetBoolean());
    }

    [Fact]
    public void FormatDiagnosticsStatus_Json_Redact_OverridesShowPaths()
    {
        string json = AppViewModel.FormatDiagnosticsStatus(
            logFilePath: "C:/logs/AudioPilot.log",
            logFileExists: false,
            logFileBytes: 0,
            logBackupDirectory: "C:/logs/backups",
            logBackupFiles: [],
            logBackupRetentionCount: 5,
            logBackupMaxAgeDays: 14,
            settingsPath: "C:/cfg/settings.json",
            settingsBackupDirectory: "C:/cfg/backups",
            settingsBackupFiles: [],
            settingsBackupRetentionCount: 5,
            jsonOutput: true,
            includeSensitivePaths: true,
            redactOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.StartsWith("path[", root.GetProperty("logFilePath").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("pathsRedacted").GetBoolean());
    }

    [Fact]
    public void FormatDiagnosticsStatus_WithBluetoothReconnectSettings_IncludesReconnectObject()
    {
        string json = AppViewModel.FormatDiagnosticsStatus(
            logFilePath: "C:/logs/AudioPilot.log",
            logFileExists: true,
            logFileBytes: 10,
            logBackupDirectory: "C:/logs/backups",
            logBackupFiles: [],
            logBackupRetentionCount: 5,
            logBackupMaxAgeDays: 14,
            settingsPath: "C:/cfg/settings.json",
            settingsBackupDirectory: "C:/cfg/backups",
            settingsBackupFiles: [],
            settingsBackupRetentionCount: 5,
            bluetoothReconnectEnabled: true,
            bluetoothReconnectMaxAttempts: AppConstants.Bluetooth.ReconnectMaxAttemptsDefault,
            bluetoothReconnectAttemptTimeoutMs: 1200,
            bluetoothReconnectCooldownMs: 5000,
            bluetoothReconnectOnlyLikely: true,
            outputSwitchDebounceMs: 125,
            inputSwitchDebounceMs: 150,
            switchRetryDelayMs: 50,
            switchRetryMaxDelayMs: 100,
            switchMaxRetries: 3,
            hotplugRefreshDebounceMs: 350,
            hotplugConnectedOverlaySuppressAfterSwitchMs: 2200,
            mixerSessionRefreshDebounceMs: 250,
            mixerSnapshotCacheInteractiveMs: 100,
            mixerSnapshotCacheBackgroundMs: 300,
            resumeHotkeyRetryDelayMs: 300,
            mixerDiagnosticsSummaryWindowSeconds: 30,
            mixerCacheWindowDiagnosticsLogEveryNRefreshes: 20,
            bluetoothReconnectSuccessObservedRecheckIntervalMs: 220,
            bluetoothReconnectTimeoutCircuitThreshold: 2,
            bluetoothReconnectTimeoutCircuitOpenMs: 180000,
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");
        JsonElement reconnect = root.GetProperty("bluetoothReconnect");
        JsonElement runtime = root.GetProperty("runtimeTuning");

        Assert.True(reconnect.GetProperty("enabled").GetBoolean());
        Assert.Equal(1200, reconnect.GetProperty("attemptTimeoutMs").GetInt32());
        Assert.Equal(5000, reconnect.GetProperty("cooldownMs").GetInt32());
        Assert.Equal(125, runtime.GetProperty("outputSwitchDebounceMs").GetInt32());
    }

    [Fact]
    public void FormatDeviceList_Text_Empty_ReturnsFriendlyMessage()
    {
        string text = AppViewModel.FormatDeviceList("output", [], jsonOutput: false);

        Assert.Equal("No active output devices found.", text);
    }

    [Fact]
    public void FormatDeviceList_Text_WithDevices_ReturnsNumberedList()
    {
        var devices = new List<CycleDevice>
        {
            new() { Id = "z", Name = "Speakers" },
            new() { Id = "b", Name = "Headset" },
        };

        string text = AppViewModel.FormatDeviceList("output", devices, jsonOutput: false);

        Assert.Equal($"1. Headset{Environment.NewLine}2. Speakers", text);
    }

    [Fact]
    public void FormatDeviceList_Text_IsDeterministicallySortedByNameThenId()
    {
        var devices = new List<CycleDevice>
        {
            new() { Id = "z", Name = "Speakers" },
            new() { Id = "a", Name = "speakers" },
            new() { Id = "m", Name = "Headset" },
        };

        string text = AppViewModel.FormatDeviceList("output", devices, jsonOutput: false);

        Assert.Equal($"1. Headset{Environment.NewLine}2. speakers{Environment.NewLine}3. Speakers", text);
    }

    [Fact]
    public void FormatDeviceList_Json_ReturnsKindAndDevices()
    {
        var devices = new List<CycleDevice>
        {
            new() { Id = "in-2", Name = "Mic" },
            new() { Id = "in-1", Name = "mic" },
        };

        string json = AppViewModel.FormatDeviceList("input", devices, jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.Equal("input", root.GetProperty("kind").GetString());
        JsonElement device = root.GetProperty("devices")[0];
        Assert.Equal("in-1", device.GetProperty("id").GetString());
        Assert.Equal("mic", device.GetProperty("name").GetString());
    }

    [Fact]
    public void FormatDeviceList_Json_Redact_RedactsIdsAndNames()
    {
        var devices = new List<CycleDevice>
        {
            new() { Id = "in-2", Name = "Mic" },
        };

        string json = AppViewModel.FormatDeviceList("input", devices, jsonOutput: true, redactOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement device = doc.RootElement.GetProperty("data").GetProperty("devices")[0];

        Assert.StartsWith("device-id[", device.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("device[", device.GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCycleList_Text_Empty_ReturnsFriendlyMessage()
    {
        string text = AppViewModel.FormatCycleList("input", [], jsonOutput: false);

        Assert.Equal("No configured input cycle devices.", text);
    }

    [Fact]
    public void FormatCycleList_Text_WithDevices_ReturnsOrderedList()
    {
        var cycle = new List<CycleDevice>
        {
            new() { Id = "o-1", Name = "Speakers" },
            new() { Id = "o-2", Name = "Monitor" },
        };

        string text = AppViewModel.FormatCycleList("output", cycle, jsonOutput: false);

        Assert.Equal($"1. Speakers{Environment.NewLine}2. Monitor", text);
    }

    [Fact]
    public void FormatCycleList_Json_ReturnsOrderedDevicePayload()
    {
        var cycle = new List<CycleDevice>
        {
            new() { Id = "o-1", Name = "Speakers" },
        };

        string json = AppViewModel.FormatCycleList("output", cycle, jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.Equal("output", root.GetProperty("kind").GetString());
        JsonElement first = root.GetProperty("devices")[0];
        Assert.Equal(1, first.GetProperty("order").GetInt32());
        Assert.Equal("o-1", first.GetProperty("id").GetString());
        Assert.Equal("Speakers", first.GetProperty("name").GetString());
    }

    [Fact]
    public void FormatCycleValidation_Text_Valid_ReturnsValidMessage()
    {
        string text = AppViewModel.FormatCycleValidation(
            "output",
            [],
            [],
            jsonOutput: false);

        Assert.Equal("output cycle is valid.", text);
    }

    [Fact]
    public void FormatCycleValidation_Text_Issues_ReturnsIssueLines()
    {
        string text = AppViewModel.FormatCycleValidation(
            "input",
            ["Mic A"],
            ["Mic B"],
            jsonOutput: false);

        string[] lines = text.Split(Environment.NewLine);
        Assert.Equal("input cycle has issues:", lines[0]);
        Assert.Contains("duplicate devices: Mic A", lines[1]);
        Assert.Contains("disconnected devices: Mic B", lines[2]);
    }

    [Fact]
    public void FormatCycleValidation_Json_ReturnsValidityAndLists()
    {
        string json = AppViewModel.FormatCycleValidation(
            "output",
            ["Speakers"],
            ["Monitor"],
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.Equal("output", root.GetProperty("kind").GetString());
        Assert.False(root.GetProperty("isValid").GetBoolean());
        Assert.Equal("Speakers", root.GetProperty("duplicateDeviceNames")[0].GetString());
        Assert.Equal("Monitor", root.GetProperty("disconnectedDeviceNames")[0].GetString());
    }

    [Fact]
    public void FormatCycleValidation_Json_Redact_RedactsDeviceNames()
    {
        string json = AppViewModel.FormatCycleValidation(
            "output",
            ["Speakers"],
            ["Monitor"],
            jsonOutput: true,
            redactOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.StartsWith("device[", root.GetProperty("duplicateDeviceNames")[0].GetString(), StringComparison.Ordinal);
        Assert.StartsWith("device[", root.GetProperty("disconnectedDeviceNames")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCycleTest_Text_Pass_ReturnsPassMessage()
    {
        string text = AppViewModel.FormatCycleTest(
            kind: "output",
            configuredCount: 3,
            connectedConfiguredCount: 3,
            hasDefaultInputDevice: true,
            reasons: [],
            jsonOutput: false);

        Assert.Equal("output cycle test passed.", text);
    }

    [Fact]
    public void FormatCycleTest_Text_Fail_ReturnsReasons()
    {
        string text = AppViewModel.FormatCycleTest(
            kind: "input",
            configuredCount: 1,
            connectedConfiguredCount: 1,
            hasDefaultInputDevice: false,
            reasons: ["no-alternate-connected-device", AppConstants.Audio.ErrorCodes.CyclePreflight.NoDefaultInputDevice],
            jsonOutput: false);

        Assert.Equal($"input cycle test failed: no-alternate-connected-device, {AppConstants.Audio.ErrorCodes.CyclePreflight.NoDefaultInputDevice}", text);
    }

    [Fact]
    public void FormatCycleTest_Json_ReturnsPreflightPayload()
    {
        string json = AppViewModel.FormatCycleTest(
            kind: "input",
            configuredCount: 2,
            connectedConfiguredCount: 1,
            hasDefaultInputDevice: true,
            reasons: ["no-alternate-connected-device"],
            jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.Equal("input", root.GetProperty("kind").GetString());
        Assert.Equal(2, root.GetProperty("configuredCount").GetInt32());
        Assert.Equal(1, root.GetProperty("connectedConfiguredCount").GetInt32());
        Assert.True(root.GetProperty("hasDefaultInputDevice").GetBoolean());
        Assert.False(root.GetProperty("canSwitch").GetBoolean());
        Assert.Equal("no-alternate-connected-device", root.GetProperty("reasons")[0].GetString());
    }

    [Fact]
    public void SerializeCliJson_WrapsSchemaVersionAndData()
    {
        string json = AppViewModel.SerializeCliJson(new { Value = 7 });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.0.0", doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("data").GetProperty("value").GetInt32());
    }

    [Fact]
    public void FormatConfigValidation_TextValid_ReturnsValidMessage()
    {
        string text = AppViewModel.FormatConfigValidation([], jsonOutput: false);

        Assert.Equal("configuration is valid.", text);
    }

    [Fact]
    public void FormatConfigValidation_TextWarnings_ReturnsWarningLines()
    {
        string text = AppViewModel.FormatConfigValidation(
            ["Output switch hotkey is invalid."],
            jsonOutput: false);

        Assert.Contains("configuration has warnings:", text);
        Assert.Contains("- Output switch hotkey is invalid.", text);
    }

    [Fact]
    public void FormatConfigValidation_Json_ReturnsExpectedShape()
    {
        string json = AppViewModel.FormatConfigValidation(["warning-a"], jsonOutput: true);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement.GetProperty("data");

        Assert.False(root.GetProperty("isValid").GetBoolean());
        Assert.Equal("warning-a", root.GetProperty("warnings")[0].GetString());
    }
}

