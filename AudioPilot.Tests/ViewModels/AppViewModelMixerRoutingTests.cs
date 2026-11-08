using System.Windows.Threading;
using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class AppViewModelMixerRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task CliChildRoutingOverridesItsDirectionAfterWaitingForRoutineWork(bool output, bool leaseAppearsWhileQueued) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMixerRoutingTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher,
            allowBackgroundWork: true, routineProcessSnapshotProvider: new ApplicationProcesses(Environment.ProcessId));
        var model = harness.ViewModel;
        var lifetime = TestPrivateAccess.GetField<RoutineRuntimeCoordinator>(model, "_routineRuntime").Lifetime;
        var routine = new AudioRoutine
        {
            Id = "manual-routing",
            Name = "Manual routing",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Apps\Player.exe",
            RouteToApplication = true,
            OutputDeviceId = "out",
            InputDeviceId = "in",
        };
        void Register() => lifetime.RegisterLease(routine, 42, false, false, false,
            cancellationToken: TestContext.Current.CancellationToken);
        if (!leaseAppearsWhileQueued) Register();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> blocker = lifetime.ApplyRoutingAsync(static () => true,
            async () => { await release.Task; return true; }, TestContext.Current.CancellationToken);
        int writes = 0;
        var readReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routing = new ApplicationRoutingOperations((pid, playback, started, token) =>
        {
            Assert.Equal(checked((uint)Environment.ProcessId), pid);
            Assert.Equal(output, playback);
            Assert.NotNull(started);
            readReady.TrySetResult();
            return Task.FromResult(new ApplicationRoutingInventory(new(true, false, null), [new() { Id = "manual", Name = "Manual device" }], started.Value));
        }, (_, playback, _, device, _) =>
        {
            Assert.Equal(output, playback);
            Assert.Equal("manual", device);
            writes++;
            return Task.FromResult(new ApplicationRoutingChange(ProcessAudioRoutingResult.Applied, new(true, false, device)));
        });
        try
        {
            Assert.True(CliCommand.TryParse(["app", "route", "set", output ? "output" : "input", "--pid",
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--device-id", "manual"], out var command, out _));
            Task<CliExecutionResult> manual = model.ExecuteApplicationRoutingFromCliAsync(command, TestContext.Current.CancellationToken, routing);
            await readReady.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (leaseAppearsWhileQueued) Register();
            release.TrySetResult();
            await blocker;
            Assert.Equal(0, (await manual.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ExitCode);
            Assert.Equal(1, writes);
            var lease = Assert.Single(lifetime.GetLeases());
            Assert.Equal(output, lease.OutputManuallyOverridden);
            Assert.Equal(!output, lease.InputManuallyOverridden);
        }
        finally
        {
            release.TrySetResult();
            foreach (var lease in lifetime.GetLeases()) lifetime.RemoveLease(lease.LeaseKey, lease.Generation);
        }
    });

    private sealed class ApplicationProcesses(int childProcessId) : IRoutineProcessSnapshotProvider
    {
        public RoutineProcessSnapshot? TryCapture(int processId, RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full) => null;
        public List<RoutineProcessSnapshot> CaptureAll(RoutineProcessSnapshotCaptureOptions options = RoutineProcessSnapshotCaptureOptions.Full) =>
            [new(42, @"C:\Apps\Player.exe"), new(childProcessId, @"C:\Apps\Audio.exe", options.HasFlag(RoutineProcessSnapshotCaptureOptions.IncludeParentProcessId) ? 42 : null)];
    }
}
