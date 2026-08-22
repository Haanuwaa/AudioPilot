using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.ViewModels;

[Collection("DeviceCacheHelperIsolation")]
public sealed class AppViewModelRuntimeHealthTests
{
    [Fact]
    public Task DiagnosticsIncludesRetainedFallbackReasonAndLiveRoutineState() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRuntimeHealthTests));
        var primary = new Monitor(succeed: false);
        var fallback = new Monitor(succeed: true);
        using var monitor = new FallbackProcessLifecycleMonitor(Logger.Instance, () => primary, () => fallback);
        Assert.True(monitor.Start().Success);
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher,
            startupService: InMemoryStartupTaskStore.CreateStartupService(), processLifecycleMonitor: monitor);
        string output = harness.ViewModel.GetDiagnosticsStatusFromCli(true, false, true);
        JsonNode health = JsonNode.Parse(output)!["data"]!["runtimeHealth"]!;
        Assert.Equal("gui", health["hostMode"]!.GetValue<string>());
        Assert.Equal("polling", health["processMonitorMode"]!.GetValue<string>());
        Assert.Equal("wmi-AccessDenied", health["processMonitorFallbackReason"]!.GetValue<string>());
        Assert.NotNull(health["hotkeys"]);
        Assert.NotNull(health["routineWork"]);
        Assert.Equal(1, primary.Starts);
        await harness.ViewModel.CleanupAsync();
    });

    private sealed class Monitor(bool succeed) : IProcessLifecycleMonitor
    {
        public event Action<int>? ProcessStarted { add { } remove { } }
        public event Action<int>? ProcessStopped { add { } remove { } }
        public bool IsRunning { get; private set; }
        internal int Starts { get; private set; }
        public ProcessLifecycleMonitorStartResult Start()
        {
            Starts++;
            IsRunning = succeed;
            return new(succeed, succeed ? "active" : "failed", succeed ? null : "wmi-AccessDenied");
        }
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }
}
