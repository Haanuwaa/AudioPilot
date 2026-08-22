using System.Reflection;
using AudioPilot.Coordinators;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Coordinators;

public sealed partial class AppSwitchCommandCoordinatorTests
{


    [Fact]
    public void ShouldContinueDeferredLoop_ReturnsFalse_WhenCancelled()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime deadlineUtc = nowUtc.AddSeconds(10);

        bool shouldContinue = AppSwitchCommandCoordinator.ShouldContinueDeferredLoop(
            nowUtc,
            deadlineUtc,
            isLifetimeCancellationRequested: true);

        Assert.False(shouldContinue);
    }


    [Fact]
    public void ShouldEmitDeferredTimeoutNotification_ReturnsFalse_WhenCancelled()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime deadlineUtc = nowUtc.AddSeconds(-1);

        bool shouldNotify = AppSwitchCommandCoordinator.ShouldEmitDeferredTimeoutNotification(
            nowUtc,
            deadlineUtc,
            isLifetimeCancellationRequested: true);

        Assert.False(shouldNotify);
    }


    [Fact]
    public void ShouldEmitDeferredTimeoutNotification_ReturnsTrue_WhenDeadlineElapsedAndNotCancelled()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime deadlineUtc = nowUtc.AddSeconds(-1);

        bool shouldNotify = AppSwitchCommandCoordinator.ShouldEmitDeferredTimeoutNotification(
            nowUtc,
            deadlineUtc,
            isLifetimeCancellationRequested: false);

        Assert.True(shouldNotify);
    }


    [Fact]
    public async Task RunDeferredOutputAutoSwitchAsync_DoesNotNotifyTimeout_WhenCoordinatorDisposed()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AppSwitchCommandCoordinatorTests), "appswitch-dispose.log");

        (int overlayShowCount, int refreshCalls) = await ExecuteDisposedDeferredOutputAutoSwitchAsync(loggerScope.Logger);

        Assert.Equal(0, overlayShowCount);
        Assert.Equal(0, refreshCalls);

        string logText = TestLogFileAssert.ReadAvailableLogText(loggerScope.LogPath);

        Assert.DoesNotContain("deferred-auto-switch-timeout", logText, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task RunDeferredOutputAutoSwitchAsync_EmitsTimeoutFailureOverlay_WhenDefaultCannotBeConfirmed()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-output-timeout-failed.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int refreshCalls = 0;
        int failureWarningCalls = 0;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: () => [CreateCycleDeviceSnapshot("connected-output-id")],
            GetCurrentDevice: static () => null,
            SwitchTargetAsync: static (_, _, _) => throw new InvalidOperationException("SwitchTargetAsync should not run during output-timeout coverage."),
            ConfirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
            IsSwitchSuccess: static (_, _) => false,
            OnAlreadyActive: static (_, _) => throw new InvalidOperationException("Already-active callback should not run during output-timeout coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during output-timeout coverage."),
            OnDefaultConfirmed: static (_, _) => throw new InvalidOperationException("Default-confirmed callback should not run during output-timeout coverage."),
            OnServiceFailure: static (_, _) => throw new InvalidOperationException("Service-failure callback should not run during output-timeout coverage."),
            OnTimeoutConfirmed: static (_, _) => throw new InvalidOperationException("Timeout-confirmed callback should not run during output-timeout coverage."),
            OnTimeoutFailure: deviceName =>
            {
                overlay.Show(OverlayDeviceKind.Error, "Failed to reconnect output device", deviceName);
                failureWarningCalls++;
            });

        await InvokeDeferredAutoSwitchAsync(
            coordinator,
            pendingDeviceId: "missing-id",
            pendingDeviceName: "Missing Output",
            successEventName: "switch-success",
            failedEventName: "switch-failed",
            callbacks);

        Assert.Equal(0, refreshCalls);
        Assert.Equal(1, failureWarningCalls);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to reconnect output device", header);
        Assert.Equal("Missing Output", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-timeout reason=deferred-auto-switch-timeout", logText, StringComparison.Ordinal);
    }


    [Fact]
    public void CreateDeferredOutputAutoSwitchCallbacks_WhenServiceSwitchFails_ShowsSwitchFailureOverlay()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-output-deferred-service-failure-overlay.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        object deferredCoordinator = typeof(AppSwitchCommandCoordinator)
            .GetField("_deferredAutoSwitchCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;

        var method = deferredCoordinator.GetType().GetMethod(
            "CreateDeferredOutputAutoSwitchCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        int refreshCalls = 0;
        int failureWarningCalls = 0;
        var callbacks = (DeferredAutoSwitchCallbacks)method!.Invoke(
            deferredCoordinator,
            [
                false,
                false,
                false,
                false,
                (Action<string>)(_ => refreshCalls++),
                (Action)(() => failureWarningCalls++),
                (Func<string, string, (bool Confirmed, string ResolvedDeviceName)>)((_, _) => (false, string.Empty)),
            ])!;

        callbacks.OnServiceFailure("op-output-defer", "Bluetooth Headset");

        Assert.Equal(0, refreshCalls);
        Assert.Equal(1, failureWarningCalls);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to switch output device", header);
        Assert.Equal("Bluetooth Headset", deviceName);
    }


    [Fact]
    public async Task RunDeferredInputAutoSwitchAsync_DoesNotNotifyTimeout_WhenCoordinatorDisposed()
    {
        using var loggerScope = TestLoggerScope.CreateFileBacked(nameof(AppSwitchCommandCoordinatorTests), "appswitch-input-dispose.log");

        int overlayShowCount = await ExecuteDisposedDeferredInputAutoSwitchAsync(loggerScope.Logger);

        Assert.Equal(0, overlayShowCount);

        string logText = TestLogFileAssert.ReadAvailableLogText(loggerScope.LogPath);

        Assert.DoesNotContain("deferred-auto-switch-timeout", logText, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task RunDeferredOutputAutoSwitchAsync_WhenPendingTargetAlreadyActive_ShowsSuccessOverlay_AndSchedulesRefresh()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-output-already-active.log");
        using var audio = new AudioDeviceService();
        string currentDeviceId = "active-output-id";
        string currentDeviceName = "Desk Speakers";
        Assert.False(string.IsNullOrWhiteSpace(currentDeviceId));
        Assert.False(string.IsNullOrWhiteSpace(currentDeviceName));

        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        int? suppressMs = null;
        bool? suppressOutput = null;
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(
            audio,
            overlay,
            loggerScope.Logger,
            reconnectCoordinator,
            (output, suppressDurationMs) =>
            {
                suppressOutput = output;
                suppressMs = suppressDurationMs;
            });

        int refreshCalls = 0;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: () => [CreateCycleDeviceSnapshot(currentDeviceId, currentDeviceName)],
            GetCurrentDevice: () => CreateCycleDeviceSnapshot(currentDeviceId, currentDeviceName),
            SwitchTargetAsync: static (_, _, _) => throw new InvalidOperationException("SwitchTargetAsync should not run during already-active coverage."),
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during already-active coverage."),
            IsSwitchSuccess: static (_, _) => false,
            OnAlreadyActive: (_, deviceName) =>
            {
                suppressOutput = true;
                suppressMs = RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs;
                overlay.Show(OverlayDeviceKind.Output, "Switched output device", deviceName);
                refreshCalls++;
            },
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during already-active coverage."),
            OnDefaultConfirmed: static (_, _) => throw new InvalidOperationException("Default-confirmed callback should not run during already-active coverage."),
            OnServiceFailure: static (_, _) => throw new InvalidOperationException("Service-failure callback should not run during already-active coverage."),
            OnTimeoutConfirmed: static (_, _) => throw new InvalidOperationException("Timeout-confirmed callback should not run during already-active coverage."),
            OnTimeoutFailure: static _ => throw new InvalidOperationException("Timeout-failure callback should not run during already-active coverage."));

        await InvokeDeferredResolvedBranchAsync(
            coordinator,
            pendingDeviceId: currentDeviceId,
            pendingDeviceName: currentDeviceName,
            successEventName: "switch-output-success",
            failedEventName: "switch-output-failed",
            callbacks);

        Assert.Equal(1, refreshCalls);
        Assert.True(suppressOutput);
        Assert.Equal(RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs, suppressMs);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Output, kind);
        Assert.Equal("Switched output device", header);
        Assert.Equal(currentDeviceName, deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("reason=deferred-auto-switch-already-active", logText, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunDeferredAutoSwitchAsync_WhenSwitchFailsButDefaultIsConfirmed_UsesDeferredDefaultConfirmedBranch()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-default-confirmed.log");
        using var audio = new AudioDeviceService();
        string targetDeviceId = "pending-output-id";
        string targetDeviceName = "Conference Headset";
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int defaultConfirmedCalls = 0;
        string? confirmedDeviceName = null;
        bool serviceFailureCalled = false;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: () => [CreateCycleDeviceSnapshot(targetDeviceId, targetDeviceName)],
            GetCurrentDevice: () => CreateCycleDeviceSnapshot("fake-current-id"),
            SwitchTargetAsync: static (_, _, _) => ValueTask.FromResult((false, (string?)null)),
            ConfirmCurrentDefaultTarget: static (_, _) => (true, "Resolved Input Device"),
            IsSwitchSuccess: static (_, _) => false,
            OnAlreadyActive: static (_, _) => throw new InvalidOperationException("Already-active callback should not run during default-confirmed coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during default-confirmed coverage."),
            OnDefaultConfirmed: (_, deviceName) =>
            {
                defaultConfirmedCalls++;
                confirmedDeviceName = deviceName;
                overlay.Show(OverlayDeviceKind.Input, "Switched input device", deviceName);
            },
            OnServiceFailure: static (_, _) => throw new InvalidOperationException("Service-failure callback should not run during default-confirmed coverage."),
            OnTimeoutConfirmed: static (_, _) => throw new InvalidOperationException("Timeout-confirmed callback should not run during default-confirmed coverage."),
            OnTimeoutFailure: _ => serviceFailureCalled = true);

        await InvokeDeferredResolvedBranchAsync(
            coordinator,
            pendingDeviceId: targetDeviceId,
            pendingDeviceName: targetDeviceName,
            successEventName: "switch-success",
            failedEventName: "switch-failed",
            callbacks);

        Assert.Equal(1, defaultConfirmedCalls);
        Assert.Equal("Resolved Input Device", confirmedDeviceName);
        Assert.False(serviceFailureCalled);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Input, kind);
        Assert.Equal("Switched input device", header);
        Assert.Equal("Resolved Input Device", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-success |", logText, StringComparison.Ordinal);
        Assert.Contains("reason=deferred-default-confirmed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred-service-failure", logText, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunDeferredAutoSwitchAsync_WhenSwitchSucceeds_UsesDeferredAutoSwitchSuccessBranch()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-switch-success.log");
        using var audio = new AudioDeviceService();
        string targetDeviceId = "pending-output-id";
        string targetDeviceName = "Conference Headset";
        var presenter = new RecordingOverlayPresenter();
        bool suppressOutput = false;
        int suppressMs = 0;
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(
            audio,
            overlay,
            loggerScope.Logger,
            reconnectCoordinator,
            suppressConnectedHotplugOverlay: (output, durationMs) =>
            {
                suppressOutput = output;
                suppressMs = durationMs;
            });

        int switchSuccessCalls = 0;
        int refreshCalls = 0;
        string? switchedDeviceName = null;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: () => [CreateCycleDeviceSnapshot(targetDeviceId, targetDeviceName)],
            GetCurrentDevice: () => CreateCycleDeviceSnapshot("fake-current-id"),
            SwitchTargetAsync: static (_, _, _) => ValueTask.FromResult((Success: true, DeviceName: (string?)"Resolved Speakers")),
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during switch-success coverage."),
            IsSwitchSuccess: static (success, deviceName) => success && !string.IsNullOrWhiteSpace(deviceName),
            OnAlreadyActive: static (_, _) => throw new InvalidOperationException("Already-active callback should not run during switch-success coverage."),
            OnSwitchSuccess: (deferredOpId, deviceName) =>
            {
                switchSuccessCalls++;
                switchedDeviceName = deviceName;
                suppressOutput = true;
                suppressMs = RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs;
                overlay.Show(OverlayDeviceKind.Output, "Switched output device", deviceName);
                refreshCalls++;
            },
            OnDefaultConfirmed: static (_, _) => throw new InvalidOperationException("Default-confirmed callback should not run during switch-success coverage."),
            OnServiceFailure: static (_, _) => throw new InvalidOperationException("Service-failure callback should not run during switch-success coverage."),
            OnTimeoutConfirmed: static (_, _) => throw new InvalidOperationException("Timeout-confirmed callback should not run during switch-success coverage."),
            OnTimeoutFailure: static _ => throw new InvalidOperationException("Timeout-failure callback should not run during switch-success coverage."));

        await InvokeDeferredResolvedBranchAsync(
            coordinator,
            pendingDeviceId: targetDeviceId,
            pendingDeviceName: targetDeviceName,
            successEventName: "switch-output-success",
            failedEventName: "switch-output-failed",
            callbacks);

        Assert.Equal(1, switchSuccessCalls);
        Assert.Equal("Resolved Speakers", switchedDeviceName);
        Assert.Equal(1, refreshCalls);
        Assert.True(suppressOutput);
        Assert.Equal(RuntimeTuningConfig.HotplugConnectedOverlaySuppressAfterSwitchMs, suppressMs);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Output, kind);
        Assert.Equal("Switched output device", header);
        Assert.Equal("Resolved Speakers", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-output-success |", logText, StringComparison.Ordinal);
        Assert.Contains("reason=deferred-auto-switch", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred-default-confirmed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred-service-failure", logText, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunDeferredAutoSwitchAsync_WhenSwitchFailsAndDefaultIsNotConfirmed_UsesDeferredServiceFailureBranch()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-service-failure.log");
        using var audio = new AudioDeviceService();
        string targetDeviceId = "pending-output-id";
        string targetDeviceName = "Conference Headset";
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int serviceFailureCalls = 0;
        string? failedDeviceName = null;
        bool defaultConfirmedCalled = false;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: () => [CreateCycleDeviceSnapshot(targetDeviceId, targetDeviceName)],
            GetCurrentDevice: () => CreateCycleDeviceSnapshot("fake-current-id"),
            SwitchTargetAsync: static (_, _, _) => ValueTask.FromResult((false, (string?)null)),
            ConfirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
            IsSwitchSuccess: static (_, _) => false,
            OnAlreadyActive: static (_, _) => throw new InvalidOperationException("Already-active callback should not run during service-failure coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during service-failure coverage."),
            OnDefaultConfirmed: static (_, _) => throw new InvalidOperationException("Default-confirmed callback should not run during service-failure coverage."),
            OnServiceFailure: (_, deviceName) =>
            {
                serviceFailureCalls++;
                failedDeviceName = deviceName;
                overlay.Show(OverlayDeviceKind.Error, "Failed to switch input device", deviceName);
            },
            OnTimeoutConfirmed: (_, _) => defaultConfirmedCalled = true,
            OnTimeoutFailure: static _ => throw new InvalidOperationException("Timeout-failure callback should not run during service-failure coverage."));

        await InvokeDeferredResolvedBranchAsync(
            coordinator,
            pendingDeviceId: targetDeviceId,
            pendingDeviceName: targetDeviceName,
            successEventName: "switch-success",
            failedEventName: "switch-failed",
            callbacks);

        Assert.Equal(1, serviceFailureCalls);
        Assert.Equal(targetDeviceName, failedDeviceName);
        Assert.False(defaultConfirmedCalled);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to switch input device", header);
        Assert.Equal(targetDeviceName, deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed |", logText, StringComparison.Ordinal);
        Assert.Contains("reason=deferred-service-failure", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred-default-confirmed", logText, StringComparison.Ordinal);
    }


    [Fact]
    public async Task RunDeferredAutoSwitchAsync_ConfirmsDefaultOnTimeout_AndLogsSuccess()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-timeout-confirmed.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int timeoutConfirmedCalls = 0;
        string? confirmedDeviceName = null;
        bool timeoutFailureCalled = false;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: audio.GetActivePlaybackCycleEntries,
            GetCurrentDevice: static () => null,
            SwitchTargetAsync: static (_, _, _) => throw new InvalidOperationException("SwitchTargetAsync should not run during timeout confirmation coverage."),
            ConfirmCurrentDefaultTarget: static (_, _) => (true, "Resolved Speakers"),
            IsSwitchSuccess: static (_, _) => false,
            OnAlreadyActive: static (_, _) => throw new InvalidOperationException("Already-active callback should not run during timeout confirmation coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during timeout confirmation coverage."),
            OnDefaultConfirmed: static (_, _) => throw new InvalidOperationException("Default-confirmed callback should not run before timeout during timeout confirmation coverage."),
            OnServiceFailure: static (_, _) => throw new InvalidOperationException("Service-failure callback should not run during timeout confirmation coverage."),
            OnTimeoutConfirmed: (_, deviceName) =>
            {
                timeoutConfirmedCalls++;
                confirmedDeviceName = deviceName;
            },
            OnTimeoutFailure: _ => timeoutFailureCalled = true);

        await InvokeDeferredAutoSwitchAsync(
            coordinator,
            pendingDeviceId: "missing-output-id",
            pendingDeviceName: "Missing Output",
            successEventName: "switch-success",
            failedEventName: "switch-failed",
            callbacks);

        Assert.Equal(1, timeoutConfirmedCalls);
        Assert.Equal("Resolved Speakers", confirmedDeviceName);
        Assert.False(timeoutFailureCalled);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-success | opId=op-timeout reason=deferred-timeout-default-confirmed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred-auto-switch-timeout", logText, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task RunDeferredAutoSwitchAsync_EmitsTimeoutFailure_WhenDefaultCannotBeConfirmed()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-timeout-failed.log");
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int timeoutFailureCalls = 0;
        string? failedDeviceName = null;
        bool timeoutConfirmedCalled = false;

        DeferredAutoSwitchCallbacks callbacks = new(
            GetActiveDevices: audio.GetActivePlaybackCycleEntries,
            GetCurrentDevice: static () => null,
            SwitchTargetAsync: static (_, _, _) => throw new InvalidOperationException("SwitchTargetAsync should not run during timeout failure coverage."),
            ConfirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
            IsSwitchSuccess: static (_, _) => false,
            OnAlreadyActive: static (_, _) => throw new InvalidOperationException("Already-active callback should not run during timeout failure coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during timeout failure coverage."),
            OnDefaultConfirmed: static (_, _) => throw new InvalidOperationException("Default-confirmed callback should not run during timeout failure coverage."),
            OnServiceFailure: static (_, _) => throw new InvalidOperationException("Service-failure callback should not run during timeout failure coverage."),
            OnTimeoutConfirmed: (_, _) => timeoutConfirmedCalled = true,
            OnTimeoutFailure: deviceName =>
            {
                timeoutFailureCalls++;
                failedDeviceName = deviceName;
            });

        await InvokeDeferredAutoSwitchAsync(
            coordinator,
            pendingDeviceId: "missing-input-id",
            pendingDeviceName: "Missing Input",
            successEventName: "switch-success",
            failedEventName: "switch-failed",
            callbacks);

        Assert.Equal(1, timeoutFailureCalls);
        Assert.Equal("Missing Input", failedDeviceName);
        Assert.False(timeoutConfirmedCalled);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-timeout reason=deferred-auto-switch-timeout", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("deferred-timeout-default-confirmed", logText, StringComparison.Ordinal);
    }

}
