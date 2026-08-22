using AudioPilot.Cli;
using AudioPilot.CliHost;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Tests.Cli;

public sealed partial class LocalHeadlessCommandRunnerTests
{

    [Fact]
    public void ResolveGuiExecutablePath_TargetsAudioPilotBesideTheCliHost()
    {
        string guiExecutablePath = LocalHeadlessCommandRunner.ResolveGuiExecutablePath();

        Assert.Equal("AudioPilot.exe", Path.GetFileName(guiExecutablePath));
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(guiExecutablePath)!)));
    }

    [Fact]
    public void ResolveBluetoothPostAttemptRecheckDelayMs_UsesRuntimeOverride()
    {
        int original = RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs;
        RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs = 275;

        try
        {
            int actualDelayMs = LocalHeadlessCommandRunner.ResolveBluetoothPostAttemptRecheckDelayMs();

            Assert.Equal(275, actualDelayMs);
        }
        finally
        {
            RuntimeTuningConfig.BluetoothReconnectPostAttemptRecheckDelayMs = original;
        }
    }


    [Fact]
    public void DisposeForCleanup_ReturnsTrue_WhenDisposableSucceeds()
    {
        using var logScope = new TestLoggerScope(nameof(DisposeForCleanup_ReturnsTrue_WhenDisposableSucceeds), "cleanup.log");
        var disposable = new TrackingDisposable();

        bool disposed = LocalHeadlessCommandRunner.DisposeForCleanup(
            disposable,
            operation: "test-op",
            disposalTarget: "test-target",
            logger: logScope.Logger);

        Assert.True(disposed);
        Assert.True(disposable.DisposeCalled);
    }


    [Fact]
    public void DisposeForCleanup_ReturnsFalse_AndLogsTrace_WhenDisposableThrows()
    {
        using var logScope = new TestLoggerScope(nameof(DisposeForCleanup_ReturnsFalse_AndLogsTrace_WhenDisposableThrows), "cleanup.log");
        logScope.Logger.MinimumLevel = LogLevel.Trace;
        var disposable = new ThrowingDisposable();

        bool disposed = LocalHeadlessCommandRunner.DisposeForCleanup(
            disposable,
            operation: "test-op",
            disposalTarget: "test-target",
            logger: logScope.Logger);

        Assert.False(disposed);

        string logText = logScope.DisposeAndReadLogText();
        Assert.Contains("headless-cleanup-dispose-ignored", logText, StringComparison.Ordinal);
        Assert.Contains("target=test-target", logText, StringComparison.Ordinal);
        Assert.Contains("exceptionType=InvalidOperationException", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void Dispose_DoesNotCreateUnusedRuntimeServices()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var tracker = new RuntimeServiceCreationTracker(workspace);
        using var runner = tracker.CreateRunner();

        runner.Dispose();

        Assert.Equal(0, tracker.SettingsCreated);
        Assert.Equal(0, tracker.StartupCreated);
        Assert.Equal(0, tracker.AudioCreated);
        Assert.Equal(0, tracker.BluetoothCreated);
    }


    [Fact]
    public void GetConfig_CreatesOnlySettingsService()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var tracker = new RuntimeServiceCreationTracker(workspace);
        using var runner = tracker.CreateRunner();

        var (found, value, error) = runner.GetConfig("theme");

        Assert.True(found, error);
        Assert.Equal("System", value);
        Assert.Equal(1, tracker.SettingsCreated);
        Assert.Equal(0, tracker.StartupCreated);
        Assert.Equal(0, tracker.AudioCreated);
        Assert.Equal(0, tracker.BluetoothCreated);
    }


    [Fact]
    public void GetStartupStatus_CreatesOnlyStartupService()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var tracker = new RuntimeServiceCreationTracker(workspace);
        using var runner = tracker.CreateRunner();

        string output = runner.GetStartupStatus(jsonOutput: false);

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Equal(0, tracker.SettingsCreated);
        Assert.Equal(1, tracker.StartupCreated);
        Assert.Equal(0, tracker.AudioCreated);
        Assert.Equal(0, tracker.BluetoothCreated);
    }


    [Fact]
    public void SetMuteSound_CreatesOnlyAudioService()
    {
        using var workspace = new TestSettingsWorkspace(nameof(LocalHeadlessCommandRunnerTests));
        var tracker = new RuntimeServiceCreationTracker(workspace);
        using var runner = tracker.CreateRunner();

        _ = runner.SetMuteSound(enabled: false);

        Assert.Equal(0, tracker.SettingsCreated);
        Assert.Equal(0, tracker.StartupCreated);
        Assert.Equal(1, tracker.AudioCreated);
        Assert.Equal(0, tracker.BluetoothCreated);
    }


    [Fact]
    public async Task ExecuteAsync_Show_ReturnsSuccess()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand { Action = CliAction.Show });

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_Hide_ReturnsSuccess()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand { Action = CliAction.Hide });

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_Refresh_ReturnsSuccess()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand { Action = CliAction.Refresh });

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_StartupOpen_ReturnsExitCodeThree()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand { Action = CliAction.StartupOpen });

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("Failed to open startup settings.", result.Output);
    }


    [Fact]
    public async Task ExecuteAsync_StartupOpenJson_ReturnsJsonErrorEnvelope()
    {
        using var runner = new LocalHeadlessCommandRunner();

        CliExecutionResult result = await runner.ExecuteAsync(new CliCommand
        {
            Action = CliAction.StartupOpen,
            JsonOutput = true,
        });

        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.Output);

        JObject parsed = JObject.Parse(result.Output!);
        Assert.Equal("1.0.0", parsed["schemaVersion"]?.Value<string>());
        Assert.Equal("startup-open-failed", parsed["data"]?["error"]?["code"]?.Value<string>());
        Assert.Equal(3, parsed["data"]?["error"]?["exitCode"]?.Value<int>());
    }

}
