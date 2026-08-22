using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{


    [Fact]
    public void SetStartupEnabled_PersistsSettingsAndTargetsTheGuiExecutable()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        settingsService.SaveSettings(new Settings { RunAtStartup = false });

        const string registryPath = @"SOFTWARE\AudioPilot.Tests\HeadlessStartup";
        const string registryValueName = "AudioPilotHeadlessStartup";
        var registry = new InMemoryUserRegistryAccessor();
        string guiExecutablePath = Path.Combine(AppContext.BaseDirectory, "AudioPilot.exe");
        var startupService = new StartupService(
            registryPath,
            registryValueName,
            Logger.Instance,
            registry,
            guiExecutablePath);
        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(
            settingsService,
            startupService,
            new AudioDeviceService(new FakeInputListenPropertyWriter()),
            new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance), LazyThreadSafetyMode.ExecutionAndPublication));
        using var runner = new LocalHeadlessCommandRunner(bundle);

        bool enabled = runner.SetStartupEnabled(enabled: true);

        Assert.True(enabled);
        Assert.True(settingsService.LoadSettings().RunAtStartup);
        Assert.Equal($"\"{guiExecutablePath}\" -startup", registry.GetValue(registryPath, registryValueName));

        bool disabled = runner.SetStartupEnabled(enabled: false);

        Assert.True(disabled);
        Assert.False(settingsService.LoadSettings().RunAtStartup);
        Assert.Null(registry.GetValue(registryPath, registryValueName));
    }


    [Fact]
    public void ExportConfig_ZipArchive_UsesSettingsTransferService()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        settingsService.SaveSettings(new Settings
        {
            Theme = AppTheme.Dark,
            RunAtStartup = true,
        });

        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(
            settingsService,
            new StartupService(),
            new AudioDeviceService(new FakeInputListenPropertyWriter()),
            new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

        using var runner = new LocalHeadlessCommandRunner(bundle);
        string exportPath = Path.Combine(workspace.Root, "config-export.zip");

        var (success, output) = runner.ExportConfig(exportPath, allowAnyPath: true, jsonOutput: false, redactOutput: false);

        Assert.True(success);
        Assert.Contains("config-export-success", output, StringComparison.Ordinal);
        Assert.True(File.Exists(exportPath));

        string importJson = SettingsTransferService.ReadImportText(exportPath);
        Settings exported = JsonConvert.DeserializeObject<Settings>(importJson) ?? throw new InvalidOperationException("Failed to deserialize exported settings.");
        Assert.Equal(AppTheme.Dark, exported.Theme);
        Assert.True(exported.RunAtStartup);
    }


    [Fact]
    public void ImportConfig_ZipReplace_UsesSettingsTransferService()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        settingsService.SaveSettings(new Settings
        {
            Theme = AppTheme.Dark,
            RunAtStartup = true,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = "Ctrl+Alt+1",
                }
            }
        });

        string importPath = Path.Combine(workspace.Root, "config-import.zip");
        SettingsTransferService.ExportSettings(new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.Light,
            RunAtStartup = false,
            DeviceSwitching = new DeviceSwitchingSettings
            {
                Output = new DeviceSwitchingOutputSettings
                {
                    SwitchHotkey = string.Empty,
                }
            }
        }, importPath);

        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(
            settingsService,
            new StartupService(),
            new AudioDeviceService(new FakeInputListenPropertyWriter()),
            new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

        using var runner = new LocalHeadlessCommandRunner(bundle);

        var (success, output) = runner.ImportConfig(importPath, replaceImport: true, allowAnyPath: true, jsonOutput: false, redactOutput: false);

        Assert.True(success);
        Assert.Contains("config-import-success", output, StringComparison.Ordinal);

        Settings imported = settingsService.LoadSettings();
        Assert.Equal(AppTheme.Light, imported.Theme);
        Assert.False(imported.RunAtStartup);
        Assert.Equal(string.Empty, imported.DeviceSwitching.Output.SwitchHotkey);
    }


    [Fact]
    public void ImportConfig_Replace_RejectsOlderSchemaVersion()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var settingsService = new SettingsService(workspace.PrimaryDir, workspace.FallbackDir);
        settingsService.SaveSettings(new Settings
        {
            SchemaVersion = Settings.CurrentSchemaVersion,
            Theme = AppTheme.Dark,
            RunAtStartup = false,
        });

        string importPath = Path.Combine(workspace.Root, "config-import-old-schema.json");
        File.WriteAllText(importPath, """
        {
          "SchemaVersion": "0.9.0",
          "Theme": "Light",
          "RunAtStartup": true
        }
        """);

        AppRuntimeServiceBundle bundle = AppRuntimeServiceBundle.Create(
            settingsService,
            new StartupService(),
            new AudioDeviceService(new FakeInputListenPropertyWriter()),
            new Lazy<BluetoothReconnectCoordinator>(() => new BluetoothReconnectCoordinator(new BluetoothReconnectService(), Logger.Instance), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

        using var runner = new LocalHeadlessCommandRunner(bundle);

        var (success, output) = runner.ImportConfig(importPath, replaceImport: true, allowAnyPath: true, jsonOutput: false, redactOutput: false);

        Assert.False(success);
        Assert.Contains("config-import-invalid-data", output, StringComparison.Ordinal);
        Assert.Contains("unsupported schema version", output, StringComparison.OrdinalIgnoreCase);

        Settings imported = settingsService.LoadSettings();
        Assert.Equal(Settings.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.Equal(AppTheme.Dark, imported.Theme);
        Assert.False(imported.RunAtStartup);
    }


    [Fact]
    public async Task ExecuteAsync_ConfigListJson_ReturnsKnownKeys()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.ConfigList,
            JsonOutput = true,
        });

        Assert.Equal(0, result.ExitCode);
        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("config", parsed["data"]?["kind"]?.Value<string>());
        Assert.Contains("theme", parsed["data"]?["keys"]?.Values<string>() ?? []);
        Assert.Contains("redact-log-content", parsed["data"]?["keys"]?.Values<string>() ?? []);
        Assert.Contains("overlay-position", parsed["data"]?["keys"]?.Values<string>() ?? []);
        JToken? steamDetail = parsed["data"]?["details"]?
            .FirstOrDefault(detail => detail?["key"]?.Value<string>() == "steam-big-picture-monitor-debounce-ms");
        Assert.NotNull(steamDetail);
        Assert.Equal("integer", steamDetail["valueType"]?.Value<string>());
        Assert.Equal("25..2000", steamDetail["range"]?.Value<string>());
        Assert.Equal("150", steamDetail["defaultValue"]?.Value<string>());
    }


    [Fact]
    public async Task ExecuteAsync_RuntimeList_ReturnsKnownKeys()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RuntimeList,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Supported runtime keys:", result.Output, StringComparison.Ordinal);
        Assert.Contains("hotplug-refresh-debounce-ms", result.Output, StringComparison.Ordinal);
        Assert.Contains("mixer-session-refresh-debounce-ms", result.Output, StringComparison.Ordinal);
        Assert.Contains("runtime-only", result.Output, StringComparison.Ordinal);
        Assert.Contains("runtime override; persisted via config", result.Output, StringComparison.Ordinal);
        Assert.Contains("range 25..2000", result.Output, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ExecuteAsync_ConfigGet_UnknownKey_ReturnsSuggestion()
    {
        using var scope = new HeadlessRunnerScope(new Settings());

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.ConfigGet,
            Key = "them",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("Did you mean 'theme'", result.Output, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ExecuteAsync_RuntimeGet_UnknownKey_ReturnsSuggestion()
    {
        using var scope = new HeadlessRunnerScope(new Settings());

        CliExecutionResult result = await scope.Runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.RuntimeGet,
            Key = "hotplug-refresh-debounc-ms",
        });

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("Did you mean 'hotplug-refresh-debounce-ms'", result.Output, StringComparison.Ordinal);
    }

}
