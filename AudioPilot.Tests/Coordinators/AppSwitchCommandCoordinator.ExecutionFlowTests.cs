using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Coordinators;

[Collection("RuntimeTuningConfigIsolation")]
public sealed partial class AppSwitchCommandCoordinatorTests
{
    private static readonly Func<string, string?, object> StubMmDeviceInterfaceFactory = CreateStubMmDeviceInterfaceFactory();
    private static readonly Func<string, object> StubPropertyStoreInterfaceFactory = CreateStubPropertyStoreInterfaceFactory();

    private class MmDeviceDispatchProxy : DispatchProxy
    {
        public string DeviceId { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "GetId" => SetGetIdResult(args),
                "GetState" => SetGetStateResult(args),
                "Activate" => SetUnsupportedResult(args, 3),
                "OpenPropertyStore" => SetOpenPropertyStoreResult(args),
                _ => throw new NotSupportedException($"Unsupported IMMDevice member: {targetMethod?.Name}"),
            };
        }

        private int SetGetIdResult(object?[]? args)
        {
            args![0] = DeviceId;
            return 0;
        }

        private static int SetGetStateResult(object?[]? args)
        {
            args![0] = DeviceState.Active;
            return 0;
        }

        private int SetOpenPropertyStoreResult(object?[]? args)
        {
            if (string.IsNullOrWhiteSpace(FriendlyName))
            {
                args![1] = null;
                return unchecked((int)0x80004001);
            }

            args![1] = StubPropertyStoreInterfaceFactory(FriendlyName);
            return 0;
        }

        private static int SetUnsupportedResult(object?[]? args, int outIndex)
        {
            args![outIndex] = null;
            return unchecked((int)0x80004001);
        }
    }

    private class PropertyStoreDispatchProxy : DispatchProxy
    {
        public string FriendlyName { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "GetCount" => SetGetCountResult(args),
                "GetAt" => SetGetAtResult(args),
                "GetValue" => SetGetValueResult(args),
                "SetValue" => 0,
                "Commit" => 0,
                _ => throw new NotSupportedException($"Unsupported IPropertyStore member: {targetMethod?.Name}"),
            };
        }

        private static int SetGetCountResult(object?[]? args)
        {
            args![0] = 1;
            return 0;
        }

        private static int SetGetAtResult(object?[]? args)
        {
            args![1] = Activator.CreateInstance(args[1]!.GetType())!;
            return 0;
        }

        private int SetGetValueResult(object?[]? args)
        {
            args![1] = CreateStringPropVariant(FriendlyName);
            return 0;
        }
    }

    private class MmDeviceCollectionDispatchProxy : DispatchProxy
    {
        public object[] DeviceInterfaces { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "GetCount" => SetGetCountResult(args),
                "Item" => SetItemResult(args),
                _ => throw new NotSupportedException($"Unsupported IMMDeviceCollection member: {targetMethod?.Name}"),
            };
        }

        private int SetGetCountResult(object?[]? args)
        {
            args![0] = DeviceInterfaces.Length;
            return 0;
        }

        private int SetItemResult(object?[]? args)
        {
            int index = (int)args![0]!;
            if (index < 0 || index >= DeviceInterfaces.Length)
            {
                args[1] = null;
                return unchecked((int)0x80070057);
            }

            args[1] = DeviceInterfaces[index];
            return 0;
        }
    }

    private sealed class NoopBluetoothReconnectService : IBluetoothReconnectService
    {
        public Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<bool> TryReconnectPairedAudioDeviceAsync(string deviceName, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<bool> TryReconnectUsingAudioEndpointControlAsync(string deviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenFinalOutputSwitchFails_ReportsServiceFailure()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-service-reported-failure.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int directSwitchCalls = 0;
        int finalSwitchCalls = 0;
        int failureCallbackCount = 0;
        int failureWarningCount = 0;
        string? failedDeviceName = null;
        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "device-a", Name = "Configured A" },
            new CycleDevice { Id = "device-b", Name = "Configured B" },
        ];

        SwitchExecutionCallbacks callbacks = new(
            GetCurrentDevice: () => CreateStubMmDevice("fake-current-id"),
            TryResolveActiveCycleEntry: configured => configured,
            GetActiveDevices: static () => [],
            SwitchDirectAsync: (_, targetDevice, _) =>
            {
                directSwitchCalls++;
                Assert.Equal("Configured A", targetDevice.Name);
                return ValueTask.FromResult((Success: false, DeviceName: (string?)null));
            },
            SwitchFinalAsync: (_, targetDevice, _) =>
            {
                finalSwitchCalls++;
                Assert.Equal("Configured A", targetDevice.Name);
                return ValueTask.FromResult((Success: false, DeviceName: (string?)null));
            },
            RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
            RecheckAfterReconnectSuccessAsync: static (_, _, _, _, activeDevices) => Task.FromResult(activeDevices),
            IsSwitchSuccess: static (success, deviceName) => success && !string.IsNullOrWhiteSpace(deviceName),
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during service-failure coverage."),
            ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run during service-failure coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during service-failure coverage."),
            OnFinalServiceFailure: deviceName =>
            {
                failureCallbackCount++;
                failedDeviceName = deviceName;
                overlay.Show(OverlayDeviceKind.Error, "Failed to switch output device", deviceName);
                failureWarningCount++;
            },
            OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during service-failure coverage."),
            OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected-devices callback should not run during service-failure coverage."),
            OnMissingCurrentDevice: static _ => throw new InvalidOperationException("Missing-current-device callback should not run during service-failure coverage."),
            OnConfirmedSingleConnectedSuccess: static _ => throw new InvalidOperationException("Single-connected success callback should not run during service-failure coverage."),
            OnReconnectStarted: static _ => throw new InvalidOperationException("Reconnect-start callback should not run during service-failure coverage."),
            OnReconnectAttemptProgress: static (_, _, _) => throw new InvalidOperationException("Reconnect progress callback should not run during service-failure coverage."),
            ReconnectKind: BluetoothReconnectDeviceKind.Output,
            OverlayDeviceKind: OverlayDeviceKind.Output,
            SuccessEventName: "switch-success",
            FailedEventName: "switch-failed",
            SkipEventName: "switch-skip",
            SkipDisconnectedEventName: "switch-skip-disconnected",
            PhasesEventName: "switch-phases",
            SuccessOverlayTitle: "Switched output device",
            FailureOverlayTitle: "Failed to switch output device",
            DisposeTraceDeviceKind: "output",
            SuppressConnectedOverlayForOutput: true);

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks,
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(1, directSwitchCalls);
        Assert.Equal(1, finalSwitchCalls);
        Assert.Equal(1, failureCallbackCount);
        Assert.Equal(1, failureWarningCount);
        Assert.Equal("Configured A", failedDeviceName);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to switch output device", header);
        Assert.Equal("Configured A", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-service-failure reason=service-reported-failure", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("switch-success | opId=op-service-failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenCurrentOutputIsOutsideCycle_AndFirstConfiguredDeviceIsDisconnected_SwitchesToConnectedCycleDevice()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-current-outside-cycle-connected-output.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int directSwitchCalls = 0;
        int finalSwitchCalls = 0;
        int successCallbackCount = 0;
        string? successDeviceName = null;
        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "disconnected-id", Name = "Disconnected Headset" },
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
        ];

        SwitchExecutionCallbacks callbacks = new(
            GetCurrentDevice: () => CreateStubMmDevice("outside-id"),
            TryResolveActiveCycleEntry: configured => configured.Id.Equals("connected-id", StringComparison.OrdinalIgnoreCase)
                ? configured
                : null,
            GetActiveDevices: static () => [CreateStubMmDevice("connected-id", "Connected Speakers")],
            SwitchDirectAsync: (_, targetDevice, _) =>
            {
                directSwitchCalls++;
                Assert.Equal("disconnected-id", targetDevice.Id);
                return ValueTask.FromResult((Success: false, DeviceName: (string?)null));
            },
            SwitchFinalAsync: (currentId, targetDevice, _) =>
            {
                finalSwitchCalls++;
                Assert.Equal("outside-id", currentId);
                Assert.Equal("connected-id", targetDevice.Id);
                return ValueTask.FromResult((Success: true, DeviceName: (string?)"Connected Speakers"));
            },
            RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
            RecheckAfterReconnectSuccessAsync: static (_, _, _, _, activeDevices) => Task.FromResult(activeDevices),
            IsSwitchSuccess: static (success, deviceName) => success && !string.IsNullOrWhiteSpace(deviceName),
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run when switching from an outside-cycle current device."),
            ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run when a connected cycle target is available."),
            OnSwitchSuccess: (_, deviceName) =>
            {
                successCallbackCount++;
                successDeviceName = deviceName;
                overlay.Show(OverlayDeviceKind.Output, "Switched output device", deviceName);
            },
            OnFinalServiceFailure: static _ => throw new InvalidOperationException("Final failure callback should not run when connected fallback succeeds."),
            OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run when connected fallback succeeds."),
            OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected-devices callback should not run when one connected device exists."),
            OnMissingCurrentDevice: static _ => throw new InvalidOperationException("Missing-current-device callback should not run when current default exists."),
            OnConfirmedSingleConnectedSuccess: static _ => throw new InvalidOperationException("Single-connected success callback should not run for an explicit fallback switch."),
            OnReconnectStarted: static _ => throw new InvalidOperationException("Reconnect should not start when current default is outside the cycle and a connected cycle target exists."),
            OnReconnectAttemptProgress: static (_, _, _) => throw new InvalidOperationException("Reconnect progress should not run when current default is outside the cycle and a connected cycle target exists."),
            ReconnectKind: BluetoothReconnectDeviceKind.Output,
            OverlayDeviceKind: OverlayDeviceKind.Output,
            SuccessEventName: "switch-success",
            FailedEventName: "switch-failed",
            SkipEventName: "switch-skip",
            SkipDisconnectedEventName: "switch-skip-disconnected",
            PhasesEventName: "switch-phases",
            SuccessOverlayTitle: "Switched output device",
            FailureOverlayTitle: "Failed to switch output device",
            DisposeTraceDeviceKind: "output",
            SuppressConnectedOverlayForOutput: true);

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks,
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(1, directSwitchCalls);
        Assert.Equal(1, finalSwitchCalls);
        Assert.Equal(1, successCallbackCount);
        Assert.Equal("Connected Speakers", successDeviceName);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Output, kind);
        Assert.Equal("Switched output device", header);
        Assert.Equal("Connected Speakers", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-skip-disconnected | opId=op-service-failure count=1 reason=current-outside-cycle", logText, StringComparison.Ordinal);
        Assert.Contains("switch-success | opId=op-service-failure reason=current-outside-cycle", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=no-alternate-connected", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenCurrentOutputIsInConfiguredCycle_DoesNotUseOutsideCycleFallback()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-current-in-cycle-single-connected-output.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int directSwitchCalls = 0;
        int finalSwitchCalls = 0;
        int reconnectStartedCalls = 0;
        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            new CycleDevice { Id = "disconnected-id", Name = "Disconnected Headset" },
        ];

        SwitchExecutionCallbacks callbacks = new(
            GetCurrentDevice: () => CreateStubMmDevice("connected-id", "Connected Speakers"),
            TryResolveActiveCycleEntry: configured => configured.Id.Equals("connected-id", StringComparison.OrdinalIgnoreCase)
                ? configured
                : null,
            GetActiveDevices: static () => [CreateStubMmDevice("connected-id", "Connected Speakers")],
            SwitchDirectAsync: (_, targetDevice, _) =>
            {
                directSwitchCalls++;
                Assert.Equal("disconnected-id", targetDevice.Id);
                return ValueTask.FromResult((Success: false, DeviceName: (string?)null));
            },
            SwitchFinalAsync: (_, _, _) =>
            {
                finalSwitchCalls++;
                return ValueTask.FromResult((Success: false, DeviceName: (string?)null));
            },
            RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
            RecheckAfterReconnectSuccessAsync: static (_, _, _, _, activeDevices) => Task.FromResult(activeDevices),
            IsSwitchSuccess: static (success, deviceName) => success && !string.IsNullOrWhiteSpace(deviceName),
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run when reconnect is disabled."),
            ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run when reconnect is disabled."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch success callback should not run for single-connected in-cycle fallback."),
            OnFinalServiceFailure: static _ => throw new InvalidOperationException("Final service failure should not run when single-connected handling reports no alternate."),
            OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure should not run when reconnect is disabled."),
            OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected-devices callback should not run when one cycle device is connected."),
            OnMissingCurrentDevice: static _ => throw new InvalidOperationException("Missing-current callback should not run when current default exists."),
            OnConfirmedSingleConnectedSuccess: static _ => throw new InvalidOperationException("Confirmed single-connected success should not run when reconnect is disabled."),
            OnReconnectStarted: _ => reconnectStartedCalls++,
            OnReconnectAttemptProgress: static (_, _, _) => throw new InvalidOperationException("Reconnect progress should not run when reconnect is disabled."),
            ReconnectKind: BluetoothReconnectDeviceKind.Output,
            OverlayDeviceKind: OverlayDeviceKind.Output,
            SuccessEventName: "switch-success",
            FailedEventName: "switch-failed",
            SkipEventName: "switch-skip",
            SkipDisconnectedEventName: "switch-skip-disconnected",
            PhasesEventName: "switch-phases",
            SuccessOverlayTitle: "Switched output device",
            FailureOverlayTitle: "Failed to switch output device",
            DisposeTraceDeviceKind: "output",
            SuppressConnectedOverlayForOutput: true);

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks,
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(1, directSwitchCalls);
        Assert.Equal(0, finalSwitchCalls);
        Assert.Equal(1, reconnectStartedCalls);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Failed to switch output device", header);
        Assert.Equal("Disconnected Headset", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-service-failure reason=no-alternate-connected", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=current-outside-cycle", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("switch-success | opId=op-service-failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenOutputCurrentDeviceIsMissing_LogsNoDefaultOutput()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-no-default-output.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int missingCurrentCalls = 0;
        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "device-a", Name = "Configured A" },
            new CycleDevice { Id = "device-b", Name = "Configured B" },
        ];

        SwitchExecutionCallbacks callbacks = new(
            GetCurrentDevice: static () => null,
            TryResolveActiveCycleEntry: configured => configured,
            GetActiveDevices: static () => throw new InvalidOperationException("Device enumeration should not run during no-default-output coverage."),
            SwitchDirectAsync: static (_, _, _) => throw new InvalidOperationException("Direct switch should not run during no-default-output coverage."),
            SwitchFinalAsync: static (_, _, _) => throw new InvalidOperationException("Final switch should not run during no-default-output coverage."),
            RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
            RecheckAfterReconnectSuccessAsync: static (_, _, _, _, activeDevices) => Task.FromResult(activeDevices),
            IsSwitchSuccess: static (_, _) => false,
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during no-default-output coverage."),
            ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run during no-default-output coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during no-default-output coverage."),
            OnFinalServiceFailure: static _ => throw new InvalidOperationException("Service-failure callback should not run during no-default-output coverage."),
            OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during no-default-output coverage."),
            OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected-devices callback should not run during no-default-output coverage."),
            OnMissingCurrentDevice: _ =>
            {
                missingCurrentCalls++;
                loggerScope.Logger.Warning("AppViewModel", () => "switch-failed | opId=op-service-failure reason=no-default-output");
            },
            OnConfirmedSingleConnectedSuccess: static _ => throw new InvalidOperationException("Single-connected success callback should not run during no-default-output coverage."),
            OnReconnectStarted: static _ => throw new InvalidOperationException("Reconnect-start callback should not run during no-default-output coverage."),
            OnReconnectAttemptProgress: static (_, _, _) => throw new InvalidOperationException("Reconnect progress callback should not run during no-default-output coverage."),
            ReconnectKind: BluetoothReconnectDeviceKind.Output,
            OverlayDeviceKind: OverlayDeviceKind.Output,
            SuccessEventName: "switch-success",
            FailedEventName: "switch-failed",
            SkipEventName: "switch-skip",
            SkipDisconnectedEventName: "switch-skip-disconnected",
            PhasesEventName: "switch-phases",
            SuccessOverlayTitle: "Switched output device",
            FailureOverlayTitle: "Failed to switch output device",
            DisposeTraceDeviceKind: "output",
            SuppressConnectedOverlayForOutput: true);

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks,
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(1, missingCurrentCalls);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-service-failure reason=no-default-output", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("service-reported-failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenInputCurrentDeviceIsMissing_LogsNoDefaultInput()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-no-default-input.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int missingCurrentCalls = 0;
        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "device-a", Name = "Configured A" },
            new CycleDevice { Id = "device-b", Name = "Configured B" },
        ];

        SwitchExecutionCallbacks callbacks = new(
            GetCurrentDevice: static () => null,
            TryResolveActiveCycleEntry: configured => configured,
            GetActiveDevices: static () => throw new InvalidOperationException("Device enumeration should not run during no-default-input coverage."),
            SwitchDirectAsync: static (_, _, _) => throw new InvalidOperationException("Direct switch should not run during no-default-input coverage."),
            SwitchFinalAsync: static (_, _, _) => throw new InvalidOperationException("Final switch should not run during no-default-input coverage."),
            RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
            RecheckAfterReconnectSuccessAsync: static (_, _, _, _, activeDevices) => Task.FromResult(activeDevices),
            IsSwitchSuccess: static (_, _) => false,
            ConfirmCurrentDefaultTarget: static (_, _) => throw new InvalidOperationException("Default confirmation should not run during no-default-input coverage."),
            ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run during no-default-input coverage."),
            OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during no-default-input coverage."),
            OnFinalServiceFailure: static _ => throw new InvalidOperationException("Service-failure callback should not run during no-default-input coverage."),
            OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during no-default-input coverage."),
            OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected-devices callback should not run during no-default-input coverage."),
            OnMissingCurrentDevice: _ =>
            {
                missingCurrentCalls++;
                loggerScope.Logger.Warning("AppViewModel", () => "switch-failed | opId=op-service-failure reason=no-default-input");
            },
            OnConfirmedSingleConnectedSuccess: static _ => throw new InvalidOperationException("Single-connected success callback should not run during no-default-input coverage."),
            OnReconnectStarted: static _ => throw new InvalidOperationException("Reconnect-start callback should not run during no-default-input coverage."),
            OnReconnectAttemptProgress: static (_, _, _) => throw new InvalidOperationException("Reconnect progress callback should not run during no-default-input coverage."),
            ReconnectKind: BluetoothReconnectDeviceKind.Input,
            OverlayDeviceKind: OverlayDeviceKind.Input,
            SuccessEventName: "switch-success",
            FailedEventName: "switch-failed",
            SkipEventName: "switch-skip",
            SkipDisconnectedEventName: "switch-skip-disconnected",
            PhasesEventName: "switch-phases",
            SuccessOverlayTitle: "Switched input device",
            FailureOverlayTitle: "Failed to switch input device",
            DisposeTraceDeviceKind: "input",
            SuppressConnectedOverlayForOutput: false);

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks,
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(1, missingCurrentCalls);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-failed | opId=op-service-failure reason=no-default-input", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("service-reported-failure", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchOutputAsync_WhenConfiguredCycleIsEmpty_LogsEmptyCycle_AndShowsMissingCycleWarning()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-output-empty-cycle.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        int cycleMissingWarningCalls = 0;
        int switchFailureWarningCalls = 0;

        bool result = await coordinator.SwitchOutputAsync(
            configuredCycle: [],
            muteMic: false,
            muteSound: false,
            deafen: false,
            preserveAudioLevels: false,
            reverse: false,
            reconnectOptions: new BluetoothReconnectOptions(
                Enabled: false,
                MaxAttempts: 0,
                AttemptTimeoutMs: 0,
                CooldownMs: 0,
                OnlyLikelyBluetoothEndpoints: false),
            schedulePostSwitchRefresh: static _ => throw new InvalidOperationException("Refresh should not be scheduled during empty-cycle coverage."),
            showOutputCycleMissingWarning: () => cycleMissingWarningCalls++,
            showOutputSwitchFailureWarning: () => switchFailureWarningCalls++);

        Assert.False(result);
        Assert.Equal(1, cycleMissingWarningCalls);
        Assert.Equal(0, switchFailureWarningCalls);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("reason=empty-cycle", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("switch-start", logText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchInputAsync_WhenConfiguredCycleIsEmpty_LogsEmptyCycle()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-input-empty-cycle.log");
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        using var audio = new AudioDeviceService();
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        bool result = await coordinator.SwitchInputAsync(
            configuredCycle: [],
            reverse: false,
            preserveAudioLevels: false,
            reconnectOptions: new BluetoothReconnectOptions(
                Enabled: false,
                MaxAttempts: 0,
                AttemptTimeoutMs: 0,
                CooldownMs: 0,
                OnlyLikelyBluetoothEndpoints: false));

        Assert.False(result);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("reason=empty-cycle", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("switch-start", logText, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppViewModelAssertSingleConnected(List<CycleDevice> connectedCycle, string expectedName)
    {
        CycleDevice connected = Assert.Single(connectedCycle);
        Assert.Equal(expectedName, connected.Name);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenOperationIsCanceled_LogsSuperseded()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-superseded.log", LogLevel.Debug);
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle: [new CycleDevice { Id = "id-a", Name = "A" }],
            reverse: false,
            callbacks: new SwitchExecutionCallbacks(
                GetCurrentDevice: static () => null,
                TryResolveActiveCycleEntry: static _ => null,
                GetActiveDevices: static () => [],
                SwitchDirectAsync: static (_, _, _) => throw new InvalidOperationException("Direct switch should not run when canceled."),
                SwitchFinalAsync: static (_, _, _) => throw new InvalidOperationException("Final switch should not run when canceled."),
                RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
                RecheckAfterReconnectSuccessAsync: static (_, _, _, _, activeDevices) => Task.FromResult(activeDevices),
                IsSwitchSuccess: static (_, _) => false,
                ConfirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
                ScheduleDeferredAutoSwitch: static (_, _, _, _) => { },
                OnSwitchSuccess: static (_, _) => { },
                OnFinalServiceFailure: static _ => { },
                OnReconnectPendingFailure: static _ => { },
                OnNoConnectedDevices: static _ => { },
                OnMissingCurrentDevice: static _ => { },
                OnConfirmedSingleConnectedSuccess: static _ => { },
                OnReconnectStarted: static _ => { },
                OnReconnectAttemptProgress: static (_, _, _) => { },
                ReconnectKind: BluetoothReconnectDeviceKind.Output,
                OverlayDeviceKind: OverlayDeviceKind.Output,
                SuccessEventName: "switch-success",
                FailedEventName: "switch-failed",
                SkipEventName: "switch-skip",
                SkipDisconnectedEventName: "switch-skip-disconnected",
                PhasesEventName: "switch-phases",
                SuccessOverlayTitle: "Switched output device",
                FailureOverlayTitle: "Failed to switch output device",
                DisposeTraceDeviceKind: "output",
                SuppressConnectedOverlayForOutput: true),
            operationCancellationToken: cts.Token,
            isOperationCurrent: static () => true);

        Assert.False(result);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-skip | opId=op-service-failure reason=superseded-by-newer-request", logText, StringComparison.Ordinal);
        Assert.Contains("switch-phases | opId=op-service-failure", logText, StringComparison.Ordinal);
        Assert.Contains("result=superseded", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenReconnectObservationDefers_LogsDeferredPhaseResult()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-deferred-phase-result.log", LogLevel.Debug);
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectService = new FakeBluetoothReconnectService { NextResult = true };
        var reconnectCoordinator = new BluetoothReconnectCoordinator(reconnectService, loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            new CycleDevice { Id = "pending-id", Name = "Pending Headset" },
        ];

        static List<MMDevice> CreateConnectedDevices() =>
        [
            CreateStubMmDevice("connected-id"),
        ];

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks: new SwitchExecutionCallbacks(
                GetCurrentDevice: static () => null,
                TryResolveActiveCycleEntry: configured => configured.Id.Equals("connected-id", StringComparison.OrdinalIgnoreCase)
                    ? new CycleDevice { Id = configured.Id, Name = configured.Name }
                    : null,
                GetActiveDevices: CreateConnectedDevices,
                SwitchDirectAsync: static (_, _, _) => throw new InvalidOperationException("Direct switch should not run during deferred-phase coverage."),
                SwitchFinalAsync: static (_, _, _) => throw new InvalidOperationException("Final switch should not run during deferred-phase coverage."),
                RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
                RecheckAfterReconnectSuccessAsync: (_, _, _, _, _) => Task.FromResult(CreateConnectedDevices()),
                IsSwitchSuccess: static (_, _) => false,
                ConfirmCurrentDefaultTarget: static (_, _) => (false, string.Empty),
                ScheduleDeferredAutoSwitch: static (_, _, _, _) => { },
                OnSwitchSuccess: static (_, _) => { },
                OnFinalServiceFailure: static _ => throw new InvalidOperationException("Final failure callback should not run during deferred-phase coverage."),
                OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during deferred-phase coverage."),
                OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected callback should not run during deferred-phase coverage."),
                OnMissingCurrentDevice: static _ => throw new InvalidOperationException("Missing-current callback should not run during deferred-phase coverage."),
                OnConfirmedSingleConnectedSuccess: static _ => throw new InvalidOperationException("Confirmed-success callback should not run during deferred-phase coverage."),
                OnReconnectStarted: static _ => { },
                OnReconnectAttemptProgress: static (_, _, _) => { },
                ReconnectKind: BluetoothReconnectDeviceKind.Output,
                OverlayDeviceKind: OverlayDeviceKind.Output,
                SuccessEventName: "switch-success",
                FailedEventName: "switch-failed",
                SkipEventName: "switch-skip",
                SkipDisconnectedEventName: "switch-skip-disconnected",
                PhasesEventName: "switch-phases",
                SuccessOverlayTitle: "Switched output device",
                FailureOverlayTitle: "Failed to switch output device",
                DisposeTraceDeviceKind: "output",
                SuppressConnectedOverlayForOutput: true),
            reconnectOptions: new BluetoothReconnectOptions(
                Enabled: true,
                MaxAttempts: 1,
                AttemptTimeoutMs: 100,
                CooldownMs: 0,
                OnlyLikelyBluetoothEndpoints: false),
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(0, presenter.ShowCount);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-skip | opId=op-service-failure reason=reconnect-success-awaiting-observation", logText, StringComparison.Ordinal);
        Assert.Contains("action=deferred-auto-switch", logText, StringComparison.Ordinal);
        Assert.Contains("switch-phases | opId=op-service-failure", logText, StringComparison.Ordinal);
        Assert.Contains("result=deferred", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenReconnectSuccessLeavesStaleActiveSnapshotButDefaultIsConfirmed_ReturnsConfirmedOutputSuccess()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-output-reconnect-stale-snapshot-confirmed.log", LogLevel.Debug);
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectService = new FakeBluetoothReconnectService { NextResult = true };
        var reconnectCoordinator = new BluetoothReconnectCoordinator(reconnectService, loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
            new CycleDevice { Id = "pending-id", Name = "Pending Headset" },
        ];

        static List<MMDevice> CreateConnectedDevices() =>
        [
            CreateStubMmDevice("connected-id"),
        ];

        bool confirmedSuccess = false;

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks: new SwitchExecutionCallbacks(
                GetCurrentDevice: static () => null,
                TryResolveActiveCycleEntry: configured => configured.Id.Equals("connected-id", StringComparison.OrdinalIgnoreCase)
                    ? new CycleDevice { Id = configured.Id, Name = configured.Name }
                    : null,
                GetActiveDevices: CreateConnectedDevices,
                SwitchDirectAsync: static (_, _, _) => throw new InvalidOperationException("Direct switch should not run during reconnect-confirmed coverage."),
                SwitchFinalAsync: static (_, _, _) => throw new InvalidOperationException("Final switch should not run during reconnect-confirmed coverage."),
                RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
                RecheckAfterReconnectSuccessAsync: (_, _, _, _, _) => Task.FromResult(CreateConnectedDevices()),
                IsSwitchSuccess: static (_, _) => false,
                ConfirmCurrentDefaultTarget: static (_, _) => (true, "Pending Headset"),
                ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run during reconnect-confirmed coverage."),
                OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during reconnect-confirmed coverage."),
                OnFinalServiceFailure: static _ => throw new InvalidOperationException("Final failure callback should not run during reconnect-confirmed coverage."),
                OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during reconnect-confirmed coverage."),
                OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected callback should not run during reconnect-confirmed coverage."),
                OnMissingCurrentDevice: static _ => throw new InvalidOperationException("Missing-current callback should not run during reconnect-confirmed coverage."),
                OnConfirmedSingleConnectedSuccess: _ => confirmedSuccess = true,
                OnReconnectStarted: static _ => { },
                OnReconnectAttemptProgress: static (_, _, _) => { },
                ReconnectKind: BluetoothReconnectDeviceKind.Output,
                OverlayDeviceKind: OverlayDeviceKind.Output,
                SuccessEventName: "switch-success",
                FailedEventName: "switch-failed",
                SkipEventName: "switch-skip",
                SkipDisconnectedEventName: "switch-skip-disconnected",
                PhasesEventName: "switch-phases",
                SuccessOverlayTitle: "Switched output device",
                FailureOverlayTitle: "Failed to switch output device",
                DisposeTraceDeviceKind: "output",
                SuppressConnectedOverlayForOutput: true),
            reconnectOptions: new BluetoothReconnectOptions(
                Enabled: true,
                MaxAttempts: 1,
                AttemptTimeoutMs: 100,
                CooldownMs: 0,
                OnlyLikelyBluetoothEndpoints: false),
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(confirmedSuccess);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Output, kind);
        Assert.Equal("Switched output device", header);
        Assert.Equal("Pending Headset", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-success | opId=op-service-failure reason=reconnect-success-default-confirmed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnect-success-awaiting-observation", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteSwitchCoreAsync_WhenReconnectSuccessLeavesStaleActiveSnapshotButDefaultIsConfirmed_ReturnsConfirmedInputSuccess()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppSwitchCommandCoordinatorTests), "appswitch-input-reconnect-stale-snapshot-confirmed.log", LogLevel.Debug);
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectService = new FakeBluetoothReconnectService { NextResult = true };
        var reconnectCoordinator = new BluetoothReconnectCoordinator(reconnectService, loggerScope.Logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, loggerScope.Logger, reconnectCoordinator);

        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Mic" },
            new CycleDevice { Id = "pending-id", Name = "Pending Headset Mic" },
        ];

        static List<MMDevice> CreateConnectedDevices() =>
        [
            CreateStubMmDevice("connected-id"),
        ];

        bool confirmedSuccess = false;

        bool result = await InvokeExecuteSwitchCoreAsync(
            coordinator,
            configuredCycle,
            reverse: false,
            callbacks: new SwitchExecutionCallbacks(
                GetCurrentDevice: static () => null,
                TryResolveActiveCycleEntry: configured => configured.Id.Equals("connected-id", StringComparison.OrdinalIgnoreCase)
                    ? new CycleDevice { Id = configured.Id, Name = configured.Name }
                    : null,
                GetActiveDevices: CreateConnectedDevices,
                SwitchDirectAsync: static (_, _, _) => throw new InvalidOperationException("Direct switch should not run during reconnect-confirmed coverage."),
                SwitchFinalAsync: static (_, _, _) => throw new InvalidOperationException("Final switch should not run during reconnect-confirmed coverage."),
                RecheckAfterReconnectAttemptAsync: static (_, _, activeDevices) => Task.FromResult(activeDevices),
                RecheckAfterReconnectSuccessAsync: (_, _, _, _, _) => Task.FromResult(CreateConnectedDevices()),
                IsSwitchSuccess: static (_, _) => false,
                ConfirmCurrentDefaultTarget: static (_, _) => (true, "Pending Headset Mic"),
                ScheduleDeferredAutoSwitch: static (_, _, _, _) => throw new InvalidOperationException("Deferred scheduling should not run during reconnect-confirmed coverage."),
                OnSwitchSuccess: static (_, _) => throw new InvalidOperationException("Switch-success callback should not run during reconnect-confirmed coverage."),
                OnFinalServiceFailure: static _ => throw new InvalidOperationException("Final failure callback should not run during reconnect-confirmed coverage."),
                OnReconnectPendingFailure: static _ => throw new InvalidOperationException("Reconnect-pending failure callback should not run during reconnect-confirmed coverage."),
                OnNoConnectedDevices: static _ => throw new InvalidOperationException("No-connected callback should not run during reconnect-confirmed coverage."),
                OnMissingCurrentDevice: static _ => throw new InvalidOperationException("Missing-current callback should not run during reconnect-confirmed coverage."),
                OnConfirmedSingleConnectedSuccess: _ => confirmedSuccess = true,
                OnReconnectStarted: static _ => { },
                OnReconnectAttemptProgress: static (_, _, _) => { },
                ReconnectKind: BluetoothReconnectDeviceKind.Input,
                OverlayDeviceKind: OverlayDeviceKind.Input,
                SuccessEventName: "switch-success",
                FailedEventName: "switch-failed",
                SkipEventName: "switch-skip",
                SkipDisconnectedEventName: "switch-skip-disconnected",
                PhasesEventName: "switch-phases",
                SuccessOverlayTitle: "Switched input device",
                FailureOverlayTitle: "Failed to switch input device",
                DisposeTraceDeviceKind: "input",
                SuppressConnectedOverlayForOutput: false),
            reconnectOptions: new BluetoothReconnectOptions(
                Enabled: true,
                MaxAttempts: 1,
                AttemptTimeoutMs: 100,
                CooldownMs: 0,
                OnlyLikelyBluetoothEndpoints: false),
            operationCancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.True(confirmedSuccess);
        Assert.Equal(1, presenter.ShowCount);
        var (kind, header, deviceName) = Assert.Single(presenter.Messages);
        Assert.Equal(OverlayDeviceKind.Input, kind);
        Assert.Equal("Switched input device", header);
        Assert.Equal("Pending Headset Mic", deviceName);

        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("switch-success | opId=op-service-failure reason=reconnect-success-default-confirmed", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("reconnect-success-awaiting-observation", logText, StringComparison.Ordinal);
    }

    private static async Task InvokeDeferredAutoSwitchAsync(
        AppSwitchCommandCoordinator coordinator,
        string pendingDeviceId,
        string pendingDeviceName,
        string successEventName,
        string failedEventName,
        DeferredAutoSwitchCallbacks callbacks)
    {
        int originalWindowMs = RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs;
        int originalRecheckMs = RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs;

        try
        {
            RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs = 1000;
            RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs = 100;

            object deferredCoordinator = GetDeferredAutoSwitchCoordinator(coordinator);
            var runDeferredMethod = deferredCoordinator.GetType().GetMethod(
                "RunDeferredAutoSwitchAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(runDeferredMethod);

            IReadOnlyList<CycleDevice> configuredCycle =
            [
                new CycleDevice { Id = "configured-a", Name = "Configured A" },
                new CycleDevice { Id = "configured-b", Name = "Configured B" },
            ];

            var task = (Task?)runDeferredMethod!.Invoke(
                deferredCoordinator,
                [
                    "op-timeout",
                    configuredCycle,
                    pendingDeviceId,
                    pendingDeviceName,
                    successEventName,
                    failedEventName,
                    callbacks,
                    CreateLifetimeCancellationProbe(coordinator),
                    CreateLinkedCancellationSourceFactory(coordinator),
                    CancellationToken.None,
                ]);

            Assert.NotNull(task);
            await task!;
        }
        finally
        {
            RuntimeTuningConfig.BluetoothReconnectDeferredAutoSwitchWindowMs = originalWindowMs;
            RuntimeTuningConfig.BluetoothReconnectSuccessRecheckIntervalMs = originalRecheckMs;
        }
    }

    private static async Task InvokeDeferredResolvedBranchAsync(
        AppSwitchCommandCoordinator coordinator,
        string pendingDeviceId,
        string pendingDeviceName,
        string successEventName,
        string failedEventName,
        DeferredAutoSwitchCallbacks callbacks)
    {
        object deferredCoordinator = GetDeferredAutoSwitchCoordinator(coordinator);
        var runDeferredMethod = deferredCoordinator.GetType().GetMethod(
            "RunDeferredAutoSwitchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(runDeferredMethod);

        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = pendingDeviceId, Name = pendingDeviceName },
        ];

        var task = (Task?)runDeferredMethod!.Invoke(
            deferredCoordinator,
            [
                "op-resolved",
                configuredCycle,
                pendingDeviceId,
                pendingDeviceName,
                successEventName,
                failedEventName,
                callbacks,
                CreateLifetimeCancellationProbe(coordinator),
                CreateLinkedCancellationSourceFactory(coordinator),
                CancellationToken.None,
            ]);

        Assert.NotNull(task);
        await task!;
    }

    private static bool InvokeTryHandleSingleConnectedCycle(
        AppSwitchCommandCoordinator coordinator,
        bool reconnectAttempted,
        bool reconnectSucceeded,
        Func<string, string, (bool Confirmed, string ResolvedDeviceName)> tryConfirmCurrentDefaultTarget,
        Action<string> onReconnectPendingFailure,
        Action<string, string> scheduleDeferredAutoSwitch,
        Action onConfirmedSuccess,
        out bool success,
        List<MMDevice>? activeDevices = null,
        List<CycleDevice>? connectedCycle = null,
        List<CycleDevice>? skippedDevices = null)
    {
        var method = typeof(AppSwitchCommandCoordinator).GetMethod(
            "TryHandleSingleConnectedCycle",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        connectedCycle ??=
        [
            new CycleDevice { Id = "connected-id", Name = "Connected Speakers" },
        ];

        skippedDevices ??=
        [
            new CycleDevice { Id = "pending-id", Name = "Pending Headset" },
        ];

        activeDevices ??= [];

        object?[] args =
        [
            "op-single-connected",
            "switch-skip",
            "switch-failed",
            "switch-success",
            OverlayDeviceKind.Output,
            "Switched output device",
            "Failed to switch output device",
            activeDevices,
            connectedCycle,
            skippedDevices,
            reconnectAttempted,
            reconnectSucceeded,
            onReconnectPendingFailure,
            scheduleDeferredAutoSwitch,
            tryConfirmCurrentDefaultTarget,
            onConfirmedSuccess,
            false,
        ];

        bool handled = (bool)method!.Invoke(coordinator, args)!;
        success = (bool)args[16]!;
        return handled;
    }

    private static async Task<(int OverlayShowCount, int RefreshCalls)> ExecuteDisposedDeferredOutputAutoSwitchAsync(Logger logger)
    {
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, logger, reconnectCoordinator);

        coordinator.Dispose();

        object deferredCoordinator = GetDeferredAutoSwitchCoordinator(coordinator);
        var createCallbacksMethod = deferredCoordinator.GetType().GetMethod(
            "CreateDeferredOutputAutoSwitchCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var runDeferredMethod = deferredCoordinator.GetType().GetMethod(
            "RunDeferredAutoSwitchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(createCallbacksMethod);
        Assert.NotNull(runDeferredMethod);

        int refreshCalls = 0;
        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        var callbacks = (DeferredAutoSwitchCallbacks)createCallbacksMethod!.Invoke(
            deferredCoordinator,
            [
                false,
                false,
                false,
                false,
                (Action<string>)(_ => refreshCalls++),
                (Action)(() => throw new InvalidOperationException("Output failure warning should not run after disposal.")),
                (Func<string, string, (bool Confirmed, string ResolvedDeviceName)>)((_, _) => (false, string.Empty)),
            ])!;

        var task = (Task?)runDeferredMethod!.Invoke(
            deferredCoordinator,
            [
                "op-dispose",
                configuredCycle,
                "id-b",
                "B",
                AppConstants.Audio.LogEvents.OutputSwitch.Success,
                AppConstants.Audio.LogEvents.OutputSwitch.Failed,
                callbacks,
                CreateLifetimeCancellationProbe(coordinator),
                CreateLinkedCancellationSourceFactory(coordinator),
                CancellationToken.None,
            ]);

        Assert.NotNull(task);
        await task!;
        return (presenter.ShowCount, refreshCalls);
    }

    private static async Task<bool> InvokeExecuteSwitchCoreAsync(
        AppSwitchCommandCoordinator coordinator,
        IReadOnlyList<CycleDevice> configuredCycle,
        bool reverse,
        SwitchExecutionCallbacks callbacks,
        Func<bool>? isOperationCurrent = null,
        BluetoothReconnectOptions? reconnectOptions = null,
        CancellationToken operationCancellationToken = default)
    {
        var method = typeof(AppSwitchCommandCoordinator).GetMethod(
            "ExecuteSwitchCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var task = (ValueTask<bool>)method!.Invoke(
            coordinator,
            [
                "op-service-failure",
                configuredCycle,
                reverse,
                reconnectOptions ?? new BluetoothReconnectOptions(
                    Enabled: false,
                    MaxAttempts: 0,
                    AttemptTimeoutMs: 0,
                    CooldownMs: 0,
                    OnlyLikelyBluetoothEndpoints: false),
                isOperationCurrent ?? (() => true),
                callbacks,
                operationCancellationToken,
            ])!;

        return await task;
    }

    private static void InvokeTryQueueCoalescedRetry(
        AppSwitchCommandCoordinator coordinator,
        Func<bool> tryBeginRetry,
        string skipEventName,
        string opId,
        AppSwitchRequestRejectionReason rejectionReason,
        IReadOnlyList<CycleDevice> configuredCycle,
        long lastRequestTicks,
        int debounceMs,
        Func<int, IReadOnlyList<CycleDevice>, Task> runBackgroundAsync)
    {
        var method = typeof(AppSwitchCommandCoordinator).GetMethod(
            "TryQueueCoalescedRetry",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        method!.Invoke(
            coordinator,
            [
                tryBeginRetry,
                skipEventName,
                opId,
                rejectionReason,
                configuredCycle,
                lastRequestTicks,
                debounceMs,
                runBackgroundAsync,
            ]);
    }

    private static async Task<int> ExecuteDisposedDeferredInputAutoSwitchAsync(Logger logger)
    {
        using var audio = new AudioDeviceService();
        var presenter = new RecordingOverlayPresenter();
        var overlay = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);
        var reconnectCoordinator = new BluetoothReconnectCoordinator(new NoopBluetoothReconnectService(), logger);
        var coordinator = new AppSwitchCommandCoordinator(audio, overlay, logger, reconnectCoordinator);

        coordinator.Dispose();

        object deferredCoordinator = GetDeferredAutoSwitchCoordinator(coordinator);
        var createCallbacksMethod = deferredCoordinator.GetType().GetMethod(
            "CreateDeferredInputAutoSwitchCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var runDeferredMethod = deferredCoordinator.GetType().GetMethod(
            "RunDeferredAutoSwitchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(createCallbacksMethod);
        Assert.NotNull(runDeferredMethod);

        IReadOnlyList<CycleDevice> configuredCycle =
        [
            new CycleDevice { Id = "id-a", Name = "A" },
            new CycleDevice { Id = "id-b", Name = "B" },
        ];

        var callbacks = (DeferredAutoSwitchCallbacks)createCallbacksMethod!.Invoke(
            deferredCoordinator,
            [
                (Func<string, string, (bool Confirmed, string ResolvedDeviceName)>)((_, _) => (false, string.Empty)),
            ])!;

        var task = (Task?)runDeferredMethod!.Invoke(
            deferredCoordinator,
            [
                "op-input-dispose",
                configuredCycle,
                "id-b",
                "B",
                AppConstants.Audio.LogEvents.InputSwitch.Success,
                AppConstants.Audio.LogEvents.InputSwitch.Failed,
                callbacks,
                CreateLifetimeCancellationProbe(coordinator),
                CreateLinkedCancellationSourceFactory(coordinator),
                CancellationToken.None,
            ]);

        Assert.NotNull(task);
        await task!;
        return presenter.ShowCount;
    }

    private static MMDevice CreateStubMmDevice(string id, string? friendlyName = null)
    {
        MMDevice device = (MMDevice)RuntimeHelpers.GetUninitializedObject(typeof(MMDevice));
        TestPrivateAccess.SetField(device, "deviceInterface", StubMmDeviceInterfaceFactory(id, friendlyName));
        return device;
    }

    private static CycleDevice CreateCycleDeviceSnapshot(string id, string? friendlyName = null)
    {
        return new CycleDevice
        {
            Id = id,
            Name = friendlyName ?? string.Empty,
        };
    }

    private static MMDeviceCollection CreateStubMmDeviceCollection(params string[] deviceIds)
    {
        Type interfaceType = typeof(MMDeviceCollection).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IMMDeviceCollection", throwOnError: true)!;
        var proxy = (MmDeviceCollectionDispatchProxy)DispatchProxy.Create(interfaceType, typeof(MmDeviceCollectionDispatchProxy));
        proxy.DeviceInterfaces = [.. deviceIds.Select(id => StubMmDeviceInterfaceFactory(id, null))];

        return (MMDeviceCollection)Activator.CreateInstance(
            typeof(MMDeviceCollection),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [proxy],
            culture: null)!;
    }

    private static Func<string, string?, object> CreateStubMmDeviceInterfaceFactory()
    {
        Type interfaceType = typeof(MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IMMDevice", throwOnError: true)!;
        return (id, friendlyName) =>
        {
            var proxy = (MmDeviceDispatchProxy)DispatchProxy.Create(interfaceType, typeof(MmDeviceDispatchProxy));
            proxy.DeviceId = id;
            proxy.FriendlyName = friendlyName ?? string.Empty;
            return proxy;
        };
    }

    private static Func<string, object> CreateStubPropertyStoreInterfaceFactory()
    {
        Type interfaceType = typeof(MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IPropertyStore", throwOnError: true)!;
        return friendlyName =>
        {
            var proxy = (PropertyStoreDispatchProxy)DispatchProxy.Create(interfaceType, typeof(PropertyStoreDispatchProxy));
            proxy.FriendlyName = friendlyName;
            return proxy;
        };
    }

    private static object CreateStringPropVariant(string value)
    {
        Type propVariantType = typeof(MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.PropVariant", throwOnError: true)!;
        object propVariant = Activator.CreateInstance(propVariantType)!;
        propVariantType.GetField("vt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(propVariant, (short)31);
        propVariantType.GetField("pointerValue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(propVariant, Marshal.StringToCoTaskMemUni(value));
        return propVariant;
    }

    private static AppSwitchDeferredAutoSwitchCoordinator GetDeferredAutoSwitchCoordinator(AppSwitchCommandCoordinator coordinator)
    {
        object? deferredCoordinator = typeof(AppSwitchCommandCoordinator)
            .GetField("_deferredAutoSwitchCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(coordinator);
        return Assert.IsType<AppSwitchDeferredAutoSwitchCoordinator>(deferredCoordinator);
    }

    private static Func<bool> CreateLifetimeCancellationProbe(AppSwitchCommandCoordinator coordinator)
    {
        var method = typeof(AppSwitchCommandCoordinator).GetMethod(
            "IsLifetimeCancellationRequested",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return () => (bool)method!.Invoke(coordinator, [])!;
    }

    private static Func<CancellationToken, CancellationTokenSource> CreateLinkedCancellationSourceFactory(AppSwitchCommandCoordinator coordinator)
    {
        var method = typeof(AppSwitchCommandCoordinator).GetMethod(
            "CreateLinkedCancellationSource",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return token => (CancellationTokenSource)method!.Invoke(coordinator, [token])!;
    }
}
