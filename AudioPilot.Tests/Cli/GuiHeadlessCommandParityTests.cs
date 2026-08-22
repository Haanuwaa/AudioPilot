using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

[Collection("AppDialogServiceIsolation")]
public sealed class GuiHeadlessCommandParityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MissingDirectTargetHasSameFailureAndDoesNotChangeCycles(bool output) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        var command = new CliCommand
        {
            Action = output ? CliAction.SwitchOutputToDevice : CliAction.SwitchInputToDevice,
            Value = "[id]audiopilot-test-missing-40b34d52",
            JsonOutput = true,
            RedactOutput = true,
        };

        CliExecutionResult gui = await CliCommandExecutor.ExecuteAsync(command, scope.Gui);
        CliExecutionResult headless = await scope.Headless.ExecuteAsync(command);

        Assert.Equal(5, gui.ExitCode);
        Assert.Equal(headless, gui);
        Assert.Contains("device-not-found", gui.Output!);
        Assert.Empty(scope.Harness.ViewModel.OutputCycleDevices);
        Assert.Empty(scope.Harness.ViewModel.InputCycleDevices);
        Assert.Empty(scope.Harness.OverlayMessages);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public Task DeviceWaitTimeoutHasSameScopeAndResult(bool outputOnly, bool inputOnly) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        const string missing = "audiopilot-test-missing-40b34d52";
        (bool guiFound, string guiOutput) = await scope.Gui.WaitForDeviceAsync(missing, 0, outputOnly, inputOnly, true, true);
        (bool headlessFound, string headlessOutput) = await scope.Headless.WaitForDeviceAsync(missing, 0, outputOnly, inputOnly, true, true);

        Assert.False(guiFound);
        Assert.False(headlessFound);
        JsonObject guiData = JsonNode.Parse(guiOutput)!["data"]!.AsObject();
        JsonObject headlessData = JsonNode.Parse(headlessOutput)!["data"]!.AsObject();
        guiData.Remove("elapsedMs");
        headlessData.Remove("elapsedMs");
        Assert.True(JsonNode.DeepEquals(guiData, headlessData), $"GUI: {guiData}\nHeadless: {headlessData}");
        Assert.Equal("wait-device-timeout", guiData["diagCode"]!.GetValue<string>());
    });

    [Fact]
    public Task DeviceWaitCancellationStopsBothRuntimesPromptly() => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        Task<(bool Found, string Output)> gui = scope.Gui.WaitForDeviceAsync("audiopilot-test-missing-40b34d52", 30000, true, false, true, false);
        Task<(bool Found, string Output)> headless = scope.Headless.WaitForDeviceAsync("audiopilot-test-missing-40b34d52", 30000, true, false, true, false);
        Assert.False(gui.IsCompleted);
        Assert.False(headless.IsCompleted);

        scope.RequestCancellation.Cancel();
        scope.Headless.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gui.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False((await headless.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)).Found);
    });

    [Theory]
    [InlineData("preserve-audio-levels", "false", true)]
    [InlineData("preserve-audio-levels", "not-a-boolean", false)]
    [InlineData("not-a-config-key", "false", false)]
    public Task ConfigMutationMatchesAndPersistsOnlyValidChanges(string key, string value, bool expectedSuccess) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        string guiBefore = File.ReadAllText(scope.GuiSettingsPath);
        string headlessBefore = File.ReadAllText(scope.HeadlessSettingsPath);

        var gui = await scope.Gui.SetConfigAsync(key, value);
        var headless = scope.Headless.SetConfig(key, value);

        Assert.Equal(expectedSuccess, gui.Updated);
        Assert.Equal(headless, gui);
        if (expectedSuccess)
        {
            Assert.False(scope.Harness.SettingsService.LoadSettings().DeviceSwitching.PreserveAudioLevels);
            Assert.False(scope.HeadlessSettings.LoadSettings().DeviceSwitching.PreserveAudioLevels);
            Assert.False(scope.Harness.ViewModel.PreserveAudioLevels);
            Assert.Equal(scope.Headless.GetConfig(key), scope.Gui.GetConfig(key));
        }
        else
        {
            Assert.Equal(guiBefore, File.ReadAllText(scope.GuiSettingsPath));
            Assert.Equal(headlessBefore, File.ReadAllText(scope.HeadlessSettingsPath));
            Assert.True(scope.Harness.ViewModel.PreserveAudioLevels);
        }
    });

    [Fact]
    public Task CanceledGuiRequestCannotSwitchOrPersistSettings() => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        string before = File.ReadAllText(scope.GuiSettingsPath);
        scope.RequestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Gui.SetConfigAsync("preserve-audio-levels", "false"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Gui.SwitchToDeviceAsync(new CliCommand
        {
            Action = CliAction.SwitchOutputToDevice,
            Value = "[id]audiopilot-test-missing-40b34d52",
        }));

        Assert.Equal(before, File.ReadAllText(scope.GuiSettingsPath));
        Assert.Empty(scope.Harness.OverlayMessages);
    });

    [Fact]
    public Task RequestCanceledWhileConfigWriteIsQueuedCannotPersist() => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        string before = File.ReadAllText(scope.GuiSettingsPath);
        SemaphoreSlim writeGate = TestPrivateAccess.GetField<SemaphoreSlim>(scope.Harness.ViewModel, "_settingsWriteSemaphore");
        await writeGate.WaitAsync(TestContext.Current.CancellationToken);
        Task<(bool Updated, string? Error)> pending;
        try
        {
            pending = scope.Gui.SetConfigAsync("preserve-audio-levels", "false");
            Assert.False(pending.IsCompleted);
            scope.RequestCancellation.Cancel();
        }
        finally { writeGate.Release(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllText(scope.GuiSettingsPath));
        Assert.True(scope.Harness.ViewModel.PreserveAudioLevels);
    });

    [Theory]
    [InlineData("get", "master", "--device")]
    [InlineData("get", "mic", "--device")]
    [InlineData("set", "master", "--device")]
    [InlineData("set", "mic", "--device")]
    [InlineData("adjust", "master", "--device")]
    [InlineData("adjust", "mic", "--device")]
    [InlineData("get", "master", "--device-id")]
    [InlineData("get", "mic", "--device-id")]
    [InlineData("set", "master", "--device-id")]
    [InlineData("set", "mic", "--device-id")]
    [InlineData("adjust", "master", "--device-id")]
    [InlineData("adjust", "mic", "--device-id")]
    public Task MissingVolumeTargetHasMatchingDiagnosticsAndPrivacy(string operation, string target, string selector) => TestExecutionGuards.RunOnSharedStaAsync(async () =>
    {
        await using var scope = new ParityScope();
        const string missing = "audiopilot-test-missing-40b34d52";
        string[] arguments = operation == "get"
            ? ["volume", operation, target, selector, missing, "--json", "--redact"]
            : ["volume", operation, target, "5", selector, missing, "--json", "--redact"];
        Assert.True(CliCommand.TryParse(arguments, out CliCommand command, out string? error), error);
        string guiBefore = File.ReadAllText(scope.GuiSettingsPath);
        string headlessBefore = File.ReadAllText(scope.HeadlessSettingsPath);

        CliExecutionResult gui = await CliCommandExecutor.ExecuteAsync(command, scope.Gui);
        CliExecutionResult headless = await scope.Headless.ExecuteAsync(command);

        Assert.Equal(3, gui.ExitCode);
        Assert.Equal(headless.ExitCode, gui.ExitCode);
        JsonObject guiData = JsonNode.Parse(gui.Output!)!["data"]!.AsObject();
        JsonObject headlessData = JsonNode.Parse(headless.Output!)!["data"]!.AsObject();
        Assert.False(string.IsNullOrWhiteSpace(guiData["error"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(headlessData["error"]!.GetValue<string>()));
        guiData.Remove("error");
        headlessData.Remove("error");
        Assert.True(JsonNode.DeepEquals(guiData, headlessData), $"GUI: {guiData}\nHeadless: {headlessData}");
        Assert.Equal($"volume-{operation}-failed", guiData["diagCode"]!.GetValue<string>());
        Assert.DoesNotContain(missing, gui.Output!, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, headless.Output!, StringComparison.Ordinal);
        Assert.Equal(guiBefore, File.ReadAllText(scope.GuiSettingsPath));
        Assert.Equal(headlessBefore, File.ReadAllText(scope.HeadlessSettingsPath));
        Assert.Empty(scope.Harness.OverlayMessages);
    });

    private sealed class ParityScope : IAsyncDisposable
    {
        private readonly TestSettingsWorkspace _guiWorkspace = new(nameof(GuiHeadlessCommandParityTests) + "-gui");
        private readonly TestSettingsWorkspace _headlessWorkspace = new(nameof(GuiHeadlessCommandParityTests) + "-headless");
        public CancellationTokenSource RequestCancellation { get; } = new();
        public AppViewModelHarnessBuilder.AppViewModelInteractionHarness Harness { get; }
        public SettingsService HeadlessSettings { get; }
        public LocalHeadlessCommandRunner Headless { get; }
        public ICliCommandRuntime Gui { get; }
        public string GuiSettingsPath => Path.Combine(_guiWorkspace.PrimaryDir, "settings.json");
        public string HeadlessSettingsPath => Path.Combine(_headlessWorkspace.PrimaryDir, "settings.json");

        public ParityScope()
        {
            var settings = new Settings();
            settings.Hotkeys.App.ToggleAppVisibility = string.Empty;
            settings.DeviceSwitching.Output.HotkeysEnabled = false;
            settings.DeviceSwitching.Input.HotkeysEnabled = false;
            Harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_guiWorkspace, Dispatcher.CurrentDispatcher,
                startupService: InMemoryStartupTaskStore.CreateStartupService(), allowBackgroundWork: true);
            Harness.SettingsService.SaveSettings(settings);
            Harness.SetCachedSettings(settings);
            HeadlessSettings = new SettingsService(_headlessWorkspace.PrimaryDir, _headlessWorkspace.FallbackDir);
            HeadlessSettings.SaveSettings(settings.Clone());
            Headless = new LocalHeadlessCommandRunner(new LocalHeadlessCommandRunner.RuntimeServiceFactories(
                CreateSettingsService: () => HeadlessSettings,
                CreateStartupService: InMemoryStartupTaskStore.CreateStartupService,
                CreateAudioService: () => new AudioDeviceService(new FakeInputListenPropertyWriter())));
            Type? runtime = typeof(App).GetNestedType("AppViewModelCliRuntime", BindingFlags.NonPublic);
            Assert.NotNull(runtime);
            Gui = (ICliCommandRuntime)Activator.CreateInstance(runtime, Harness.ViewModel, new TestAppMainWindowManager(), RequestCancellation.Token)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.ViewModel.CleanupAsync();
            Headless.Dispose();
            Harness.Dispose();
            RequestCancellation.Dispose();
            _guiWorkspace.Dispose();
            _headlessWorkspace.Dispose();
        }
    }
}
