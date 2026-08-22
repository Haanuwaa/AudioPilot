using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("DeviceCacheHelperIsolation")]
public sealed class AppViewModelRuntimeHealthTests
{
    [Fact]
    public Task ShutdownBeforeQueuedExportRuns_PreventsCollectionAndArchiveCreation() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRuntimeHealthTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        var vm = harness.ViewModel;
        int captures = 0;
        using var overlay = new OverlayService(action => action(), _ => new RecordingOverlayPresenter());
        var engine = new MediaOverlayEngine(currentSnapshotOverride: (_, _, _) =>
        {
            Interlocked.Increment(ref captures);
            return Task.FromResult(MediaOverlaySessionSnapshot.Empty);
        }, snapshotsBySourceOverride: (_, _) => Task.FromResult(new Dictionary<string, MediaOverlaySessionSnapshot>()),
            sessionSnapshotsOverride: (_, _) => Task.FromResult(new List<MediaOverlaySessionSnapshot>()));
        var coordinator = new AppCliOverlayCoordinator(harness.Audio, overlay, new MediaOverlayCommandService(engine), Logger.Instance, static () => null);
        await TestPrivateAccess.GetField<AppCliOverlayCoordinator>(vm, "_cliOverlayCoordinator").ShutdownAsync();
        TestPrivateAccess.SetField(vm, "_cliOverlayCoordinator", coordinator);
        var shutdown = TestPrivateAccess.GetField<CancellationTokenSource>(vm, "_backgroundWorkCts");
        using var posted = new ManualResetEventSlim();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        int uiThread = Environment.CurrentManagedThreadId;
        string path = Path.Combine(workspace.Root, "canceled-export.zip");
        void OnPosted(object? sender, DispatcherHookEventArgs e)
        {
            if (Environment.CurrentManagedThreadId != uiThread && e.Operation is DispatcherOperation<Task<DiagnosticBundleSaveResult>>) posted.Set();
        }
        dispatcher.Hooks.OperationPosted += OnPosted;
        try
        {
            var save = TestPrivateAccess.InvokeNonPublicTask<DiagnosticBundleSaveResult>(vm, "SaveDiagnosticsBundleAsync", path);
            Assert.True(posted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(save.IsCompleted);
            shutdown.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            await vm.WaitForQueuedBackgroundTasksForTestsAsync();
            Assert.Equal(0, Volatile.Read(ref captures));
            Assert.False(File.Exists(path));
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= OnPosted;
            shutdown.Cancel();
            await vm.CleanupAsync();
            await coordinator.ShutdownAsync();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task DeferredRoutineOutcomeRemainsDistinctFromCompletedOrFailedExecution(bool accepted) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRuntimeHealthTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        var routine = new AudioRoutine { Id = "fixture-routine", Name = "Private fixture routine", OutputDeviceId = "fixture-output" };
        var result = new RoutineExecutionResult(accepted, "Private output", null, AwaitingAppCompletion: true);
        typeof(AppViewModel).GetMethod("RecordRoutineExecutionHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(harness.ViewModel, [routine, "application-launch", result, null]);
        var entry = Assert.Single(TestPrivateAccess.GetField<Lazy<ExecutionHistoryService>>(harness.ViewModel, "_executionHistory").Value.GetEntries());
        var display = DiagnosticsOperation.FromEntry(entry);
        var json = harness.ViewModel.GetDiagnosticsHistoryFromCli(true, 30, "routine", redactOutput: true);
        var item = Assert.Single(JsonNode.Parse(json)!["data"]!["entries"]!.AsArray())!;
        Assert.Equal(accepted ? "awaiting-app-audio" : "failed", item["outcome"]!.GetValue<string>());
        Assert.Equal(accepted, item["success"]!.GetValue<bool>());
        Assert.Equal(!accepted, display.Issue);
        Assert.Contains(accepted ? "Waiting for app audio" : "Failed", display.Heading, StringComparison.Ordinal);
        if (accepted) Assert.Contains("waiting for app audio", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Private fixture routine", json, StringComparison.Ordinal);
        await harness.ViewModel.CleanupAsync();
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public Task DirectSwitchBoundaryCorrelatesCompletedAndFaultedWritesWithoutDuplicateHistory(bool output, bool fault) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRuntimeHealthTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        string? writeTrace = null;
        Func<ValueTask<bool>> write = () =>
        {
            writeTrace = OperationTrace.CurrentId;
            if (fault) throw new InvalidOperationException("Private write detail");
            return ValueTask.FromResult(true);
        };
        var track = typeof(AppViewModel).GetMethod("TrackDeviceSwitchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (OperationTrace.Attach("fixture-cli-command"))
        {
            var pending = (ValueTask<bool>)track.Invoke(harness.ViewModel, [write, output, false, "cli", "Private fixture device", true])!;
            if (fault) await Assert.ThrowsAsync<InvalidOperationException>(() => pending.AsTask());
            else Assert.True(await pending);
            Assert.Equal("fixture-cli-command", OperationTrace.CurrentId);
        }
        Assert.Null(OperationTrace.CurrentId);
        string json = harness.ViewModel.GetDiagnosticsHistoryFromCli(true, 30, "switch", redactOutput: true);
        var entry = Assert.Single(JsonNode.Parse(json)!["data"]!["entries"]!.AsArray())!;
        Assert.Equal(output ? "switch-output-device" : "switch-input-device", entry["action"]!.GetValue<string>());
        Assert.Equal(writeTrace, entry["traceId"]!.GetValue<string>());
        Assert.Equal(!fault, entry["success"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(entry[output ? "outputDeviceName" : "inputDeviceName"]!.GetValue<string>()));
        Assert.DoesNotContain("Private fixture device", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Private write detail", json, StringComparison.Ordinal);
        await harness.ViewModel.CleanupAsync();
    });

    [Fact]
    public Task SwitchHistoryCoversSharedPickerHotkeyAndCliBoundariesWithoutDuplicates() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRuntimeHealthTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
        MethodInfo track = typeof(AppViewModel).GetMethod("TrackDeviceSwitchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string? observedTrace = null;
        var successful = (ValueTask<bool>)track.Invoke(harness.ViewModel,
            [new Func<ValueTask<bool>>(() => { observedTrace = OperationTrace.CurrentId; return ValueTask.FromResult(true); }), true, false, "picker", "Private device label", false])!;
        Assert.True(await successful);
        Assert.Null(OperationTrace.CurrentId);
        var faulted = (ValueTask<bool>)track.Invoke(harness.ViewModel,
            [new Func<ValueTask<bool>>(() => throw new InvalidOperationException("private failure detail")), false, false, "hotkey", null, false])!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulted.AsTask());
        Assert.False(await harness.ViewModel.SwitchOutputFromCliAsync(false, false, false, false));
        string json = harness.ViewModel.GetDiagnosticsHistoryFromCli(true, 30, "switch", redactOutput: true);
        var entries = JsonNode.Parse(json)!["data"]!["entries"]!.AsArray();
        Assert.Equal(3, entries.Count);
        Assert.Single(entries, entry => entry!["source"]!.GetValue<string>() == "picker" && entry["success"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(observedTrace));
        Assert.Equal(observedTrace, entries.Single(entry => entry!["source"]!.GetValue<string>() == "picker")!["traceId"]!.GetValue<string>());
        Assert.Single(entries, entry => entry!["source"]!.GetValue<string>() == "hotkey" && !entry["success"]!.GetValue<bool>());
        Assert.Single(entries, entry => entry!["source"]!.GetValue<string>() == "cli" && entry["skipped"]!.GetValue<bool>());
        Assert.DoesNotContain("Private device label", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private failure detail", json, StringComparison.Ordinal);
        await harness.ViewModel.CleanupAsync();
    });

    [Fact]
    public Task DiagnosticFeedbackDoesNotReleaseTrackedNativeCleanup() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRuntimeHealthTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher,
            startupService: InMemoryStartupTaskStore.CreateStartupService(), allowBackgroundWork: true);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MethodInfo run = typeof(AppViewModel).GetMethod("RunDiagnosticsWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(bool));
        var result = (Task<bool>)run.Invoke(harness.ViewModel,
            [new Func<CancellationToken, Task<bool>>(_ => Task.FromResult(true)), "diagnostics-test-cleanup",
             new Func<Task>(() => { cleanupEntered.TrySetResult(); return cleanupFinished.Task; }), TestContext.Current.CancellationToken])!;
        try
        {
            Assert.True(await result.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var tracked = (System.Collections.Concurrent.ConcurrentDictionary<int, Task>)typeof(AppViewModel)
                .GetField("_backgroundTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.ViewModel)!;
            Assert.Contains(tracked.Values, task => !task.IsCompleted);
        }
        finally
        {
            cleanupFinished.TrySetResult();
            await harness.ViewModel.CleanupAsync();
        }
    });

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
        harness.ViewModel.PauseAutomaticRoutines(null);
        Assert.Contains("automatic routines: paused", harness.ViewModel.GetDiagnosticsStatusFromCli(false, false, true), StringComparison.Ordinal);
        JsonNode paused = JsonNode.Parse(harness.ViewModel.GetDiagnosticsStatusFromCli(true, false, true))!["data"]!["runtimeHealth"]!;
        Assert.True(paused["routineWork"]!["automaticPaused"]!.GetValue<bool>());
        var report = Assert.IsType<DiagnosticsReport>(typeof(AppViewModel).GetMethod("CaptureDiagnosticsReport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(harness.ViewModel, null));
        Assert.True(report.Health.RoutineWork!.AutomaticPaused);
        Assert.Equal(Environment.ProcessId, report.Process!.ProcessId);
        harness.ViewModel.ResumeAutomaticRoutines();
        Assert.Contains("automatic routines: none configured", harness.ViewModel.GetDiagnosticsStatusFromCli(false, false, true), StringComparison.Ordinal);
        Assert.NotNull(health["audio"]);
        Assert.NotNull(health["settings"]);
        Assert.NotNull(health["logging"]);
        Assert.False(report.Health.RoutineWork.HasAutomaticRoutines);
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
