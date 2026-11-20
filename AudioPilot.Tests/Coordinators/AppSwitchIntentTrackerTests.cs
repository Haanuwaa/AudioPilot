using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppSwitchIntentTrackerTests
{
    [Theory]
    [InlineData(BluetoothReconnectDeviceKind.Output)]
    [InlineData(BluetoothReconnectDeviceKind.Input)]
    public void Begin_ReentrantCancellationKeepsVersionAndTokenTogether(BluetoothReconnectDeviceKind kind)
    {
        using var tracker = new AppSwitchIntentTracker(kind);
        (int firstVersion, CancellationToken firstToken) = tracker.Begin();
        (int Version, CancellationToken Token) reentrant = default;
        using CancellationTokenRegistration registration = firstToken.Register(() => reentrant = tracker.Begin());

        (int secondVersion, CancellationToken secondToken) = tracker.Begin();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(secondToken.IsCancellationRequested);
        Assert.False(tracker.IsCurrent(firstVersion));
        Assert.False(tracker.IsCurrent(secondVersion));
        Assert.True(tracker.IsCurrent(reentrant.Version));
        Assert.False(reentrant.Token.IsCancellationRequested);
        Assert.Equal(reentrant.Token, tracker.GetActiveToken());
    }

    [Fact]
    public async Task Begin_ConcurrentRequestsLeaveOnlyNewestTokenActive()
    {
        using var tracker = new AppSwitchIntentTracker(BluetoothReconnectDeviceKind.Output);
        var requests = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(tracker.Begin)));
        int latestVersion = requests.Max(static request => request.Version);

        Assert.Equal(32, requests.Select(static request => request.Version).Distinct().Count());
        foreach (var request in requests)
        {
            Assert.Equal(request.Version == latestVersion, tracker.IsCurrent(request.Version));
            Assert.Equal(request.Version != latestVersion, request.Token.IsCancellationRequested);
        }
        Assert.Equal(requests.Single(request => request.Version == latestVersion).Token, tracker.GetActiveToken());
    }

    [Fact]
    public void Dispose_ClosesAdmissionBeforeCancellationCallbacks()
    {
        using var tracker = new AppSwitchIntentTracker(BluetoothReconnectDeviceKind.Output);
        (int version, CancellationToken token) = tracker.Begin();
        tracker.SetActiveTarget(BluetoothReconnectDeviceKind.Output, "b", "Headphones");
        tracker.SetReconnectOverlayDeviceName(BluetoothReconnectDeviceKind.Output, "Headphones");
        (int Version, CancellationToken Token) reentrant = default;
        using CancellationTokenRegistration registration = token.Register(() => reentrant = tracker.Begin());

        tracker.Dispose();
        tracker.Dispose();

        Assert.False(tracker.IsCurrent(version));
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, reentrant.Version);
        Assert.True(reentrant.Token.IsCancellationRequested);
        Assert.True(tracker.GetActiveToken().IsCancellationRequested);
        Assert.Null(tracker.GetReconnectOverlayDeviceName());
    }

    [Theory]
    [InlineData(BluetoothReconnectDeviceKind.Output, BluetoothReconnectDeviceKind.Input)]
    [InlineData(BluetoothReconnectDeviceKind.Input, BluetoothReconnectDeviceKind.Output)]
    public void ReconnectState_IsScopedToItsFlow(BluetoothReconnectDeviceKind kind, BluetoothReconnectDeviceKind otherKind)
    {
        using var tracker = new AppSwitchIntentTracker(kind);
        CycleDevice[] cycle = [new() { Id = "a", Name = "First" }, new() { Id = "b", Name = "Second" }];
        tracker.SetActiveTarget(kind, "b", "Second");
        tracker.SetReconnectOverlayDeviceName(kind, "Second");
        tracker.ClearActiveTarget(otherKind);
        tracker.ClearReconnectOverlayDeviceName(otherKind);

        Assert.True(tracker.DoesRequestedTargetMatchActiveTarget(cycle, "a", reverse: false));
        Assert.False(tracker.DoesRequestedTargetMatchActiveTarget(cycle, "b", reverse: false));
        Assert.Equal("Second", tracker.GetReconnectOverlayDeviceName());

        tracker.ClearActiveTarget(kind);
        tracker.ClearReconnectOverlayDeviceName(kind);
        Assert.False(tracker.DoesRequestedTargetMatchActiveTarget(cycle, "a", reverse: false));
        Assert.Null(tracker.GetReconnectOverlayDeviceName());
    }
}
