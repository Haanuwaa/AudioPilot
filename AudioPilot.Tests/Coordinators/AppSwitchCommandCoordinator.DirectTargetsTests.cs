using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchCommandCoordinatorDirectTargetsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledReconnectSkipsReadinessWaitAndSupersedesOlderCycleIntent(bool output)
    {
        using var audio = new AudioDeviceService(new FakeInputListenPropertyWriter());
        var presenter = new RecordingOverlayPresenter();
        using var overlay = new OverlayService(action => action(), _ => presenter);
        var bluetooth = new FakeBluetoothReconnectService();
        using var coordinator = new AppSwitchCommandCoordinator(audio, overlay, Logger.Instance, new(bluetooth, Logger.Instance));
        var tracker = TestPrivateAccess.GetField<AppSwitchIntentTracker>(coordinator, output ? "_outputIntentTracker" : "_inputIntentTracker");
        (int version, CancellationToken token) = tracker.Begin();
        var target = new CycleDevice { Id = "missing-direct-target-fixture", Name = "Fixture Bluetooth endpoint" };

        Task<bool> request = output
            ? coordinator.SwitchOutputToDeviceAsync(target, false, false, false, false, new(), _ => { }).AsTask()
            : coordinator.SwitchInputToDeviceAsync(target, false, new()).AsTask();
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(token.IsCancellationRequested);
        Assert.False(tracker.IsCurrent(version));
        Assert.Equal(0, bluetooth.Calls);
        Assert.Equal(OverlayDeviceKind.Error, Assert.Single(presenter.Messages).kind);
    }
}
