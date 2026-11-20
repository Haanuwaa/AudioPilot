using System.Text;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Updates;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Tests.Services;

public sealed class UpdateLifecycleTests
{
    [Fact]
    public async Task RestartStartup_HoldsAutomationUntilCommitAndDoesNotReplayStartupRoutines()
    {
        using var logger = new TestLoggerScope(nameof(UpdateLifecycleTests), "startup-gate.log", LogLevel.Info);
        var vm = new FakeStartupViewModel { CurrentSettings = new Settings() };
        var gate = new Gate();
        bool published = false;
        var coordinator = new AppStartupCoordinator(vm, new FakeStartupHotkeyRegistrar(), logger.Logger, updateGate: gate);
        Task startup = coordinator.InitializeAsync(false, () => { published = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(published);
        Assert.Equal(0, vm.EnableRoutineAppStartMonitoringCalls);
        Assert.Equal(0, vm.ExecuteAudioPilotStartupRoutinesCalls);
        gate.Commit.SetResult();
        await startup.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, vm.EnableRoutineAppStartMonitoringCalls);
        Assert.Equal(0, vm.ExecuteAudioPilotStartupRoutinesCalls);
    }

    [Fact]
    public async Task AbortedRestartStartup_NeverEnablesAutomation()
    {
        using var logger = new TestLoggerScope(nameof(UpdateLifecycleTests), "abort-gate.log", LogLevel.Info);
        var vm = new FakeStartupViewModel { CurrentSettings = new Settings() };
        var gate = new Gate();
        var coordinator = new AppStartupCoordinator(vm, new FakeStartupHotkeyRegistrar(), logger.Logger, updateGate: gate);
        Task startup = coordinator.InitializeAsync(false, static () => Task.CompletedTask, TestContext.Current.CancellationToken);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.Commit.SetCanceled(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(0, vm.EnableRoutineAppStartMonitoringCalls);
        Assert.Equal(0, vm.ExecuteAudioPilotStartupRoutinesCalls);
    }

    [Fact]
    public async Task FreshStartup_RetainsShellThenStartupRoutineOrdering()
    {
        using var logger = new TestLoggerScope(nameof(UpdateLifecycleTests), "fresh-order.log", LogLevel.Info);
        bool published = false;
        bool observed = false;
        var vm = new FakeStartupViewModel { CurrentSettings = new Settings() };
        vm.StartupRoutinesAsync = () => { observed = published && vm.EnableRoutineAppStartMonitoringCalls == 1; return Task.CompletedTask; };
        var coordinator = new AppStartupCoordinator(vm, new FakeStartupHotkeyRegistrar(), logger.Logger);
        await coordinator.InitializeAsync(false, () => { published = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.True(observed);
        Assert.Equal(1, vm.ExecuteAudioPilotStartupRoutinesCalls);
    }

    [Fact]
    public async Task Wire_RejectsDuplicateFieldsAndOtherProcessIdentity()
    {
        string nonce = new('A', 64);
        var launch = new UpdateLaunch(1, nonce, "AudioPilot.Update." + nonce, 1, 1, @"C:\helper.exe", Guid.NewGuid().ToString("N"), false, false);
        byte[] json = Encoding.UTF8.GetBytes($$"""{"Schema":1,"Nonce":"{{nonce}}","Kind":"commit","Kind":"abort","Pid":1}""");
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(json.Length));
        stream.Write(json);
        stream.Position = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => UpdateLifecycleWire.ReadAsync(stream, launch, 1, TestContext.Current.CancellationToken));
        stream.SetLength(0);
        await UpdateLifecycleWire.WriteAsync(stream, new(1, nonce, "commit", 99), TestContext.Current.CancellationToken);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateLifecycleWire.ReadAsync(stream, launch, 1, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => UpdateLifecycleWire.ParseLaunch(UpdateLifecycleWire.SerializeLaunch(launch), null));
    }

    private sealed class Gate : IUpdateStartupGate
    {
        public bool Restart => true;
        public bool Visible => false;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Commit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForCommitAsync(CancellationToken token) { Entered.SetResult(); return Commit.Task.WaitAsync(token); }
    }
}
