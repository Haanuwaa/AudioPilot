using System.Reflection;
using AudioPilot.Coordinators;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class AppSwitchCommandCoordinatorTests
{


    [Fact]
    public void TryResolveDeferredPendingTarget_ReturnsFalse_WhenPendingMissing()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredPendingTarget(
            pendingDeviceId: "id-z",
            pendingDeviceName: "Z",
            connectedCycle,
            out CycleDevice targetDevice);

        Assert.False(resolved);
        Assert.Equal(string.Empty, targetDevice.Id);
    }


    [Fact]
    public void TryResolveDeferredPendingTarget_ReturnsExactIdMatch_WhenPresent()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredPendingTarget(
            pendingDeviceId: "id-b",
            pendingDeviceName: "B",
            connectedCycle,
            out CycleDevice targetDevice);

        Assert.True(resolved);
        Assert.Equal("id-b", targetDevice.Id);
        Assert.Equal("B", targetDevice.Name);
    }


    [Fact]
    public void TryResolveDeferredPendingTarget_RemapsByName_WhenUniqueMatchExists()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "Laptop Speakers" },
            new CycleDevice { Id = "id-new", Name = "WH-1000XM4 Stereo" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredPendingTarget(
            pendingDeviceId: "id-old",
            pendingDeviceName: "WH-1000XM4 Hands-Free AG Audio",
            connectedCycle,
            out CycleDevice targetDevice);

        Assert.True(resolved);
        Assert.Equal("id-new", targetDevice.Id);
        Assert.Equal("WH-1000XM4 Stereo", targetDevice.Name);
    }


    [Fact]
    public void TryResolveDeferredPendingTarget_ReturnsFalse_WhenNameMatchIsAmbiguous()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "id-a", Name = "Galaxy Buds2 Stereo" },
            new CycleDevice { Id = "id-b", Name = "Galaxy Buds2 Hands-Free AG Audio" },
        ];

        bool resolved = AppSwitchCommandCoordinator.TryResolveDeferredPendingTarget(
            pendingDeviceId: "id-old",
            pendingDeviceName: "Galaxy Buds2",
            connectedCycle,
            out CycleDevice targetDevice);

        Assert.False(resolved);
        Assert.Equal(string.Empty, targetDevice.Id);
    }


    [Fact]
    public void IsReconnectSuccessPendingTargetSatisfied_ReturnsTrue_WhenCurrentDefaultAlreadyMatchesPendingTarget()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
        ];

        bool satisfied = AppSwitchCommandCoordinator.IsReconnectSuccessPendingTargetSatisfied(
            connectedCycle,
            pendingIdSpecified: true,
            pendingDeviceId: "pending-id-old",
            pendingDeviceName: "WH-1000XM5 Stereo",
            confirmCurrentDefaultTarget: static (_, _) => (true, "WH-1000XM5 Stereo"),
            out bool defaultConfirmed);

        Assert.True(satisfied);
        Assert.True(defaultConfirmed);
    }


    [Fact]
    public void IsReconnectSuccessPendingTargetSatisfied_ReturnsFalse_WhenPendingTargetIsStillUnresolved()
    {
        List<CycleDevice> connectedCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
        ];

        bool satisfied = AppSwitchCommandCoordinator.IsReconnectSuccessPendingTargetSatisfied(
            connectedCycle,
            pendingIdSpecified: true,
            pendingDeviceId: "pending-id-old",
            pendingDeviceName: "WH-1000XM5 Stereo",
            confirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
            out bool defaultConfirmed);

        Assert.False(satisfied);
        Assert.False(defaultConfirmed);
    }


    [Theory]
    [InlineData(2, 0, false, false, 0)]
    [InlineData(1, 1, true, true, 1)]
    [InlineData(1, 1, true, false, 2)]
    [InlineData(1, 1, false, false, 3)]
    [InlineData(1, 0, false, false, 3)]
    public void ResolveSingleConnectedCycleDecision_ReturnsExpectedAction(
        int connectedCount,
        int skippedCount,
        bool reconnectAttempted,
        bool reconnectSucceeded,
        int expected)
    {
        SingleConnectedCycleDecision actual = AppSwitchCommandCoordinator.ResolveSingleConnectedCycleDecision(
            connectedCount,
            skippedCount,
            reconnectAttempted,
            reconnectSucceeded);

        Assert.Equal((SingleConnectedCycleDecision)expected, actual);
    }


    [Fact]
    public void TryHandleSingleConnectedCycle_WhenReconnectSucceededAndDefaultIsConfirmed_ReportsConfirmedSuccess()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-reconnect-observed-confirmed.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        bool scheduledDeferred = false;
        bool confirmedSuccess = false;

        bool handled = InvokeTryHandleSingleConnectedCycle(
            coordinator,
            reconnectAttempted: true,
            reconnectSucceeded: true,
            tryConfirmCurrentDefaultTarget: static (_, _) => (true, "Resolved Speakers"),
            onReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during reconnect-confirmed coverage."),
            scheduleDeferredAutoSwitch: (_, _) => scheduledDeferred = true,
            onConfirmedSuccess: () => confirmedSuccess = true,
            out bool success);

        Assert.True(handled);
        Assert.True(success);
        Assert.True(confirmedSuccess);
        Assert.False(scheduledDeferred);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Output, kind);
        Assert.Equal("Switched output device", header);
        Assert.Equal("Resolved Speakers", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-success | opId=op-single-connected reason=reconnect-success-default-confirmed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnect-success-awaiting-observation", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void TryHandleSingleConnectedCycle_WhenReconnectSucceededButDefaultIsNotConfirmed_SchedulesDeferredObservation()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-reconnect-awaiting-observation.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        bool scheduledDeferred = false;
        bool confirmedSuccess = false;

        bool handled = InvokeTryHandleSingleConnectedCycle(
            coordinator,
            reconnectAttempted: true,
            reconnectSucceeded: true,
            tryConfirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
            onReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during awaiting-observation coverage."),
            scheduleDeferredAutoSwitch: (_, _) => scheduledDeferred = true,
            onConfirmedSuccess: () => confirmedSuccess = true,
            out bool success);

        Assert.True(handled);
        Assert.False(success);
        Assert.False(confirmedSuccess);
        Assert.True(scheduledDeferred);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-skip | opId=op-single-connected reason=reconnect-success-awaiting-observation", logText, StringComparison.Ordinal);
        Assert.Contains("action=deferred-auto-switch", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnect-success-default-confirmed", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void TryHandleSingleConnectedCycle_WhenNoAlternateConnected_ShowsFailureOverlay_AndLogsFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-no-alternate-connected.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        bool scheduledDeferred = false;

        bool handled = InvokeTryHandleSingleConnectedCycle(
            coordinator,
            reconnectAttempted: false,
            reconnectSucceeded: false,
            tryConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during no-alternate-connected coverage."),
            onReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during no-alternate coverage."),
            scheduleDeferredAutoSwitch: (_, _) => scheduledDeferred = true,
            onConfirmedSuccess: static () => throw new InvalidOperationException("Confirmed-success callback should not run during no-alternate-connected coverage."),
            out bool success,
            connectedCycle:
            [
                new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            ],
            skippedDevices: []);

        Assert.True(handled);
        Assert.False(success);
        Assert.False(scheduledDeferred);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to switch output device", header);
        Assert.Equal("Connected Speakers", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-single-connected reason=no-alternate-connected", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnect-pending", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-disconnected-target-inactive", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void TryHandleSingleConnectedCycle_WhenReconnectIsStillPending_DoesNotScheduleDeferredSwitch()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-reconnect-pending.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        bool scheduledDeferred = false;
        string? reconnectPendingFailureDevice = null;

        bool handled = InvokeTryHandleSingleConnectedCycle(
            coordinator,
            reconnectAttempted: true,
            reconnectSucceeded: false,
            tryConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during reconnect-pending coverage."),
            onReconnectPendingFailure: deviceName => reconnectPendingFailureDevice = deviceName,
            scheduleDeferredAutoSwitch: (_, _) => scheduledDeferred = true,
            onConfirmedSuccess: static () => throw new InvalidOperationException("Confirmed-success callback should not run during reconnect-pending coverage."),
            out bool success,
            connectedCycle:
            [
                new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            ],
            skippedDevices:
            [
                new CycleDevice { Id = "pending-id", Name = "Pending Headset" },
            ]);

        Assert.True(handled);
        Assert.False(success);
        Assert.False(scheduledDeferred);
        Assert.Equal("Pending Headset", reconnectPendingFailureDevice);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-single-connected reason=reconnect-pending", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("no-alternate-connected", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void CreateOutputSwitchExecutionCallbacks_WhenReconnectPending_ShowsReconnectFailureOverlay()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-reconnect-pending-overlay.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        var method = typeof(AppSwitchCommandCoordinator).GetMethod(
            "CreateOutputSwitchExecutionCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var callbacks = (SwitchExecutionCallbacks)method!.Invoke(
            coordinator,
            [
                false,
                false,
                false,
                false,
                (Action<string>)(_ => { }),
                (Action)(() => { }),
            ])!;

        callbacks.OnReconnectPendingFailure("Pending Headset");

        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to reconnect output device", header);
        Assert.Equal("Pending Headset", deviceName);
    }


    [Fact]
    public void TryHandleSingleConnectedCycle_WhenReconnectWasNotAttempted_ShowsSwitchFailureOverlay()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-no-reconnect-attempt.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        bool handled = InvokeTryHandleSingleConnectedCycle(
            coordinator,
            reconnectAttempted: false,
            reconnectSucceeded: false,
            tryConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run when reconnect was not attempted."),
            onReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run when reconnect was not attempted."),
            scheduleDeferredAutoSwitch: static (_, _) => throw new InvalidOperationException("Deferred scheduling should not run when reconnect was not attempted."),
            onConfirmedSuccess: static () => throw new InvalidOperationException("Confirmed-success callback should not run when reconnect was not attempted."),
            out bool success,
            connectedCycle:
            [
                new CycleDevice { Id = "connected-id", Name = "USB Speakers" },
            ],
            skippedDevices:
            [
                new CycleDevice { Id = "pending-id", Name = "Line Out" },
            ]);

        Assert.True(handled);
        Assert.False(success);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to switch output device", header);
        Assert.Equal("Line Out", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-single-connected reason=no-alternate-connected", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnect-pending", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void BuildCycleSnapshotState_WhenConfiguredDeviceRemapsByName_AddsRemappedDevice()
    {
        string remappedDeviceId = "remapped-output-id";
        string remappedDeviceName = "WH-1000XM4 Stereo";
        List<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            new CycleDevice { Id = "stale-id", Name = remappedDeviceName },
        ];
        List<CycleDevice> activeDevices =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            new CycleDevice { Id = remappedDeviceId, Name = remappedDeviceName },
        ];

        SwitchCycleSnapshotState state = AppSwitchCycleStateResolver.BuildCycleSnapshotState(configuredCycle, activeDevices);

        Assert.Empty(state.SkippedDevices);
        Assert.Equal(2, state.ConnectedCycle.Count);
        Assert.Equal(remappedDeviceId, state.ConnectedCycle[1].Id);
        Assert.Equal(remappedDeviceName, state.ConnectedCycle[1].Name);
    }


    [Theory]
    [InlineData("id-a", "WH-1000XM4 Stereo", "id-a", "WH-1000XM4 Hands-Free AG Audio", true)]
    [InlineData("id-new", "WH-1000XM4 Stereo", "id-old", "WH-1000XM4 Hands-Free AG Audio", true)]
    [InlineData("id-a", "Speakers (Realtek(R) Audio)", "id-b", "WH-1000XM4 Hands-Free AG Audio", false)]
    public void DoesCurrentDefaultMatchPendingTarget_ReturnsExpectedValue(
        string currentDeviceId,
        string currentDeviceName,
        string pendingDeviceId,
        string pendingDeviceName,
        bool expected)
    {
        bool matches = AppSwitchCommandCoordinator.DoesCurrentDefaultMatchPendingTarget(
            currentDeviceId,
            currentDeviceName,
            pendingDeviceId,
            pendingDeviceName);

        Assert.Equal(expected, matches);
    }


    [Fact]
    public void ResolveReconnectConnectedOverlaySuppressMs_ReturnsAtLeastReconnectWindow()
    {
        int suppressMs = AppSwitchCommandCoordinator.ResolveReconnectConnectedOverlaySuppressMs(
            baseSuppressMs: 2200,
            stabilizeWindowMs: 12000,
            timeoutGraceMs: 2000,
            recheckIntervalMs: 500);

        Assert.Equal(14500, suppressMs);
    }


    [Fact]
    public void ResolveReconnectConnectedOverlaySuppressMs_PreservesLongerBaseSuppression()
    {
        int suppressMs = AppSwitchCommandCoordinator.ResolveReconnectConnectedOverlaySuppressMs(
            baseSuppressMs: 20000,
            stabilizeWindowMs: 12000,
            timeoutGraceMs: 2000,
            recheckIntervalMs: 500);

        Assert.Equal(20000, suppressMs);
    }


    [Theory]
    [InlineData(0, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessRecheckInitialIntervalMs)]
    [InlineData(1199, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessRecheckInitialIntervalMs)]
    [InlineData(1200, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessRecheckMidIntervalMs)]
    [InlineData(3999, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessRecheckMidIntervalMs)]
    [InlineData(4000, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessRecheckIntervalMs)]
    public void ResolveAdaptiveSuccessRecheckIntervalMs_ReturnsExpectedIntervals(int elapsedMs, int expectedDelayMs)
    {
        int actualDelayMs = AppSwitchCommandCoordinator.ResolveAdaptiveSuccessRecheckIntervalMs(elapsedMs);

        Assert.Equal(expectedDelayMs, actualDelayMs);
    }


    [Fact]
    public void ResolveSuccessObservedRecheckIntervalMs_ReturnsAdaptiveInterval_WhenPendingNotObserved()
    {
        int actualDelayMs = AppSwitchCommandCoordinator.ResolveSuccessObservedRecheckIntervalMs(
            elapsedMs: 5000,
            pendingObserved: false);

        Assert.Equal(AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessRecheckIntervalMs, actualDelayMs);
    }


    [Theory]
    [InlineData(0, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessObservedRecheckIntervalMs)]
    [InlineData(1500, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessObservedRecheckIntervalMs)]
    [InlineData(5000, AudioPilot.Constants.AppConstants.Timing.BluetoothReconnectSuccessObservedRecheckIntervalMs)]
    public void ResolveSuccessObservedRecheckIntervalMs_ClampsInterval_WhenPendingObserved(int elapsedMs, int expectedDelayMs)
    {
        int actualDelayMs = AppSwitchCommandCoordinator.ResolveSuccessObservedRecheckIntervalMs(
            elapsedMs,
            pendingObserved: true);

        Assert.Equal(expectedDelayMs, actualDelayMs);
    }


    [Fact]
    public void ResolveSuccessObservedRecheckIntervalMs_UsesRuntimeOverride_WhenPendingObserved()
    {
        int original = RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs;
        RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs = 75;

        try
        {
            int actualDelayMs = AppSwitchCommandCoordinator.ResolveSuccessObservedRecheckIntervalMs(
                elapsedMs: 5000,
                pendingObserved: true);

            Assert.Equal(75, actualDelayMs);
        }
        finally
        {
            RuntimeTuningConfig.BluetoothReconnectSuccessObservedRecheckIntervalMs = original;
        }
    }


    [Theory]
    [InlineData(1000, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void ShouldApplyReconnectSuccessTimeoutGrace_RequiresGraceToBeEnabled(int graceMs, bool expected)
    {
        bool applyGrace = AppSwitchCommandCoordinator.ShouldApplyReconnectSuccessTimeoutGrace(graceMs);

        Assert.Equal(expected, applyGrace);
    }

}
