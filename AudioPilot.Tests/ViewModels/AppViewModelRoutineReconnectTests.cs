using System.Reflection;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineReconnectTests : IDisposable
{
    private readonly AudioDeviceService _audio = new();

    [Fact]
    public void BuildRoutineActiveDeviceIds_IgnoresBlankIds_AndDeduplicatesCaseInsensitively()
    {
        HashSet<string> activeIds = AppViewModel.BuildRoutineActiveDeviceIds(
        [
            new CycleDevice { Id = "device-1", Name = "Headset" },
            new CycleDevice { Id = "DEVICE-1", Name = "Headset Duplicate" },
            new CycleDevice { Id = " ", Name = "Blank" },
            new CycleDevice { Id = "device-2", Name = "Microphone" },
        ]);

        Assert.Equal(2, activeIds.Count);
        Assert.Contains("device-1", activeIds);
        Assert.Contains("device-2", activeIds);
    }

    [Fact]
    public void BuildRoutineActiveDeviceIds_ReturnsEmptySet_WhenSourceIsNull()
    {
        HashSet<string> activeIds = AppViewModel.BuildRoutineActiveDeviceIds(null);

        Assert.Empty(activeIds);
    }

    [Fact]
    public async Task ExecuteRoutineOutputSwitchAsync_AttemptsBluetoothReconnect_WhenTargetMissingAndReconnectSucceeds()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = true };
        using TempLogScope logScope = TempLogScope.Create("routine-output-switch");
        var overlayPresenter = new RecordingOverlayPresenter();
        using var harness = CreateHarness(fakeReconnectService, logScope.Logger, overlayPresenter);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Id = "routine-output",
            Name = "Routine Output",
            OutputDeviceId = "missing-output-device",
            OutputDeviceName = "Bluetooth Headset",
        };

        AppViewModel.RoutineDeviceSwitchExecutionResult result = await viewModel.ExecuteRoutineOutputSwitchAsync(routine, applicationProcessId: null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
        Assert.Equal(1, fakeReconnectService.Calls);

        string logText = logScope.ReadLogText(
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed");
        AssertRoutineOutputSwitchLogOrder(logText);
        Assert.Contains($"routineId={LogPrivacy.Id("routine-output")}", logText, StringComparison.Ordinal);
        Assert.Contains($"routineName={LogPrivacy.Label("Routine Output")}", logText, StringComparison.Ordinal);
        Assert.Contains("opId=routine-output-reconnect:", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("opId=routine-output-reconnect:routine-output", logText, StringComparison.Ordinal);
        if (logText.Contains("routine-output-switch-started", StringComparison.Ordinal))
        {
            Assert.Contains("opId=routine-output:", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("opId=routine-output:routine-output", logText, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Routine Output", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bluetooth Headset", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRoutineOutputSwitchAsync_UsesFallbackReconnect_WhenPairDoesNotReconnectTarget()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService();
        fakeReconnectService.EnqueueResult(false);
        fakeReconnectService.NextFallbackResult = true;
        using TempLogScope logScope = TempLogScope.Create("routine-output-fallback");
        using var harness = CreateHarness(fakeReconnectService, logScope.Logger);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Id = "routine-output-fallback",
            Name = "Routine Output Fallback",
            OutputDeviceId = "missing-output-device",
            OutputDeviceName = "Bluetooth Headset",
        };

        AppViewModel.RoutineDeviceSwitchExecutionResult result = await viewModel.ExecuteRoutineOutputSwitchAsync(routine, applicationProcessId: null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
        Assert.Equal(1, fakeReconnectService.Calls);
        Assert.Equal(1, fakeReconnectService.FallbackCalls);
        Assert.Equal(["output"], fakeReconnectService.Kinds);

        string logText = logScope.ReadLogText(
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed");
        AssertRoutineOutputSwitchLogOrder(logText);
    }

    [Fact]
    public void ReconcileRoutineTargetForExecution_RemapsOutputTargetFromKnownDeviceList()
    {
        AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell();
        viewModel.SetKnownActiveDeviceInfosForTests(
            [new CycleDevice { Id = "fresh-output", Name = "Bluetooth Headset Stereo" }],
            []);
        AudioRoutine routine = new()
        {
            Id = "routine-output-remap",
            Name = "Routine Output Remap",
            OutputDeviceId = "missing-output-device",
            OutputDeviceName = "Bluetooth Headset",
        };

        MethodInfo? method = typeof(AppViewModel).GetMethod("ReconcileRoutineTargetForExecution", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        AudioRoutine resolved = (AudioRoutine)method!.Invoke(viewModel, [routine, true])!;

        Assert.Equal("fresh-output", resolved.OutputDeviceId);
        Assert.Equal("Bluetooth Headset Stereo", resolved.OutputDeviceName);
    }

    [Fact]
    public async Task ExecuteRoutineInputSwitchAsync_AttemptsBluetoothReconnect_WhenTargetMissingAndReconnectFails()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = false };
        using TempLogScope logScope = TempLogScope.Create("routine-input-switch");
        using var harness = CreateHarness(fakeReconnectService, logScope.Logger);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Id = "routine-input",
            Name = "Routine Input",
            InputDeviceId = "missing-input-device",
            InputDeviceName = "Bluetooth Microphone",
        };

        AppViewModel.RoutineDeviceSwitchExecutionResult result = await viewModel.ExecuteRoutineInputSwitchAsync(routine, applicationProcessId: null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
        Assert.Equal(1, fakeReconnectService.Calls);

        string logText = logScope.ReadLogText(
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed",
            "routine-input-switch-started",
            "routine-input-switch-completed");
        AssertLogOrder(
            logText,
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed",
            "routine-input-switch-started",
            "routine-input-switch-completed");
        Assert.Contains($"routineId={LogPrivacy.Id("routine-input")}", logText, StringComparison.Ordinal);
        Assert.Contains($"routineName={LogPrivacy.Label("Routine Input")}", logText, StringComparison.Ordinal);
        Assert.Contains("opId=routine-input-reconnect:", logText, StringComparison.Ordinal);
        Assert.Contains("opId=routine-input:", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("opId=routine-input-reconnect:routine-input", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("opId=routine-input:routine-input", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Routine Input", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bluetooth Microphone", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRoutineAsync_AttemptsInputReconnect_AfterOutputFailure()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = false };
        var overlayPresenter = new RecordingOverlayPresenter();
        using TempLogScope logScope = TempLogScope.Create("routine-execution");
        using var harness = CreateHarness(fakeReconnectService, logScope.Logger, overlayPresenter);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Id = "routine-dual",
            Name = "Desk",
            OutputDeviceId = "missing-output-device",
            OutputDeviceName = "Bluetooth Headset",
            InputDeviceId = "missing-input-device",
            InputDeviceName = "Bluetooth Microphone",
        };

        AppViewModel.RoutineExecutionResult result = await viewModel.ExecuteRoutineAsync(routine, showOverlay: true, executionSource: "test", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.OutputFailureDetail));
        Assert.False(string.IsNullOrWhiteSpace(result.InputFailureDetail));
        Assert.Equal(2, fakeReconnectService.Calls);
        Assert.Equal(["output", "input"], fakeReconnectService.Kinds);

        var (kind, header, deviceName) = Assert.Single(overlayPresenter.Messages);
        Assert.Equal(OverlayDeviceKind.Error, kind);
        Assert.Equal("Routine output/input failed", header);
        Assert.Equal("Desk", deviceName);

        string logText = logScope.ReadLogText(
            "routine-execution-started",
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed",
            "routine-input-switch-started",
            "routine-input-switch-completed",
            "routine-execution-failed");
        AssertRoutineExecutionOutputThenInputLogOrder(logText);
        Assert.Contains($"routineId={LogPrivacy.Id("routine-dual")}", logText, StringComparison.Ordinal);
        Assert.Contains($"routineName={LogPrivacy.Label("Desk")}", logText, StringComparison.Ordinal);
        Assert.Contains("source=test", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bluetooth Headset", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bluetooth Microphone", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRoutineOutputSwitchAsync_WhenCanceledDuringPostReconnectDelay_PropagatesCancellation()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = false };
        using var harness = CreateHarness(fakeReconnectService, Logger.Instance);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Id = "routine-output-cancel",
            Name = "Routine Output Cancel",
            OutputDeviceId = "missing-output-device",
            OutputDeviceName = "Bluetooth Headset",
        };

        using var cancellationTokenSource = new CancellationTokenSource();
        Func<int, CancellationToken, Task> originalDelay = AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests;
        AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests = (_, cancellationToken) =>
        {
            cancellationTokenSource.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        try
        {
            Exception? exception = await Record.ExceptionAsync(() =>
                viewModel.ExecuteRoutineOutputSwitchAsync(routine, applicationProcessId: null, cancellationTokenSource.Token));
            Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        }
        finally
        {
            AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests = originalDelay;
        }

        Assert.Equal(1, fakeReconnectService.Calls);
    }

    [Fact]
    public async Task ExecuteDeviceChangeTriggeredRoutinesAsync_WhenCanceledDuringReconnectDelay_PropagatesCancellation()
    {
        var fakeReconnectService = new FakeBluetoothReconnectService { NextResult = false };
        using var harness = CreateHarness(fakeReconnectService, Logger.Instance);
        AppViewModel viewModel = harness.ViewModel;
        AudioRoutine routine = new()
        {
            Id = "routine-device-change-cancel",
            Name = "Routine Device Change Cancel",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.DeviceChange,
            OutputDeviceId = "missing-output-device",
            OutputDeviceName = "Bluetooth Headset",
        };

        Settings cachedSettings = new()
        {
            Miscellaneous = new MiscellaneousSettings
            {
                BluetoothReconnectEnabled = true
            },
            Routines = new RoutinesSettings
            {
                Items = [routine.Clone()]
            }
        };
        TestPrivateAccess.SetField(viewModel, "_cachedSettings", cachedSettings);

        using var cancellationTokenSource = new CancellationTokenSource();
        Func<int, CancellationToken, Task> originalDelay = AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests;
        AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests = (_, cancellationToken) =>
        {
            cancellationTokenSource.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        try
        {
            Exception? exception = await Record.ExceptionAsync(() =>
                viewModel.ExecuteDeviceChangeTriggeredRoutinesAsync(cancellationTokenSource.Token));
            Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        }
        finally
        {
            AppViewModel.RoutineReconnectPostAttemptDelayAsyncForTests = originalDelay;
        }

        Assert.Equal(1, fakeReconnectService.Calls);
    }

    [Fact]
    public void ResolveRoutinePostSwitchRestoreOptions_PreservesProcessAudioButSkipsExplicitEndpointTargets()
    {
        AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell();
        TestPrivateAccess.SetField(viewModel, "_preserveAudioLevelsBackingField", true);
        AudioRoutine routine = new()
        {
            OutputDeviceId = "out-1",
            OutputDeviceName = "Headset",
            MasterVolumePercent = 35,
        };

        (bool preserveAudioLevels, bool restoreMasterVolume, bool restoreMicVolume) = InvokeResolveRoutinePostSwitchRestoreOptions(viewModel, routine);

        Assert.True(preserveAudioLevels);
        Assert.False(restoreMasterVolume);
        Assert.True(restoreMicVolume);
    }

    [Fact]
    public void ResolveRoutinePostSwitchRestoreOptions_RestoresAllVolumes_WhenNoExplicitVolumeTargetsAreConfigured()
    {
        AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell();
        TestPrivateAccess.SetField(viewModel, "_preserveAudioLevelsBackingField", true);
        AudioRoutine routine = new()
        {
            OutputDeviceId = "out-1",
            OutputDeviceName = "Headset",
        };

        (bool preserveAudioLevels, bool restoreMasterVolume, bool restoreMicVolume) = InvokeResolveRoutinePostSwitchRestoreOptions(viewModel, routine);

        Assert.True(preserveAudioLevels);
        Assert.True(restoreMasterVolume);
        Assert.True(restoreMicVolume);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldPreserveAudioLevelsForRoutineDeviceSwitch_ReturnsCurrentSetting(bool expected)
    {
        AppViewModel viewModel = AppViewModelHarnessBuilder.CreateUninitializedViewModelShell();
        TestPrivateAccess.SetField(viewModel, "_preserveAudioLevelsBackingField", expected);

        bool result = InvokeShouldPreserveAudioLevelsForRoutineDeviceSwitch(viewModel);

        Assert.Equal(expected, result);
    }

    public void Dispose()
    {
        _audio.Dispose();
    }

    private AppViewModelHarnessBuilder.RoutineReconnectHarness CreateHarness(
        FakeBluetoothReconnectService fakeReconnectService,
        Logger logger,
        RecordingOverlayPresenter? overlayPresenter = null)
    {
        return AppViewModelHarnessBuilder.CreateRoutineReconnectHarness(_audio, fakeReconnectService, logger, overlayPresenter);
    }

    private static void AssertLogOrder(string logText, params string[] markers)
    {
        int searchStart = 0;
        foreach (string marker in markers)
        {
            int markerIndex = logText.IndexOf(marker, searchStart, StringComparison.Ordinal);
            Assert.True(markerIndex >= 0, $"Expected log marker '{marker}' was not found.\nLog text:\n{logText}");
            searchStart = markerIndex + marker.Length;
        }
    }

    private static void AssertRoutineOutputSwitchLogOrder(string logText)
    {
        if (logText.Contains("routine-output-switch-failed", StringComparison.Ordinal))
        {
            AssertLogOrder(
                logText,
                "routine-target-reconnect-started",
                "routine-target-reconnect-completed",
                "routine-output-switch-failed");
            return;
        }

        AssertLogOrder(
            logText,
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed",
            "routine-output-switch-started",
            "routine-output-switch-completed");
    }

    private static void AssertRoutineExecutionOutputThenInputLogOrder(string logText)
    {
        if (logText.Contains("routine-output-switch-failed", StringComparison.Ordinal))
        {
            AssertLogOrder(
                logText,
                "routine-execution-started",
                "routine-target-reconnect-started",
                "routine-target-reconnect-completed",
                "routine-output-switch-failed",
                "routine-input-switch-started",
                "routine-input-switch-completed",
                "routine-execution-failed");
            return;
        }

        AssertLogOrder(
            logText,
            "routine-execution-started",
            "routine-target-reconnect-started",
            "routine-target-reconnect-completed",
            "routine-output-switch-started",
            "routine-output-switch-completed",
            "routine-input-switch-started",
            "routine-input-switch-completed",
            "routine-execution-failed");
    }

    private static (bool PreserveAudioLevels, bool RestoreMasterVolume, bool RestoreMicVolume) InvokeResolveRoutinePostSwitchRestoreOptions(AppViewModel viewModel, AudioRoutine routine)
    {
        MethodInfo? method = typeof(AppViewModel).GetMethod("ResolveRoutinePostSwitchRestoreOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object result = method!.Invoke(viewModel, [routine])!;
        Type resultType = result.GetType();
        FieldInfo? preserveField = resultType.GetField("Item1");
        FieldInfo? restoreMasterField = resultType.GetField("Item2");
        FieldInfo? restoreMicField = resultType.GetField("Item3");
        Assert.NotNull(preserveField);
        Assert.NotNull(restoreMasterField);
        Assert.NotNull(restoreMicField);
        return (
            (bool)preserveField!.GetValue(result)!,
            (bool)restoreMasterField!.GetValue(result)!,
            (bool)restoreMicField!.GetValue(result)!);
    }

    private static bool InvokeShouldPreserveAudioLevelsForRoutineDeviceSwitch(AppViewModel viewModel)
    {
        MethodInfo? method = typeof(AppViewModel).GetMethod("ShouldPreserveAudioLevelsForRoutineDeviceSwitch", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(viewModel, null)!;
    }

    private sealed class TempLogScope : IDisposable
    {
        private readonly string _root;
        private readonly string _logPath;
        private bool _disposed;

        private TempLogScope(string root, string logPath, Logger logger)
        {
            _root = root;
            _logPath = logPath;
            Logger = logger;
        }

        public Logger Logger { get; }

        public static TempLogScope Create(string prefix)
        {
            string root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            const string logFileName = "app.log";
            var logger = new Logger(root, logFileName)
            {
                MinimumLevel = LogLevel.Trace,
            };

            return new TempLogScope(root, Path.Combine(root, logFileName), logger);
        }

        public string ReadLogText(params string[] requiredFragments)
        {
            Dispose();
            return TestLogFileAssert.WaitForLogText(_logPath, requiredFragments: requiredFragments);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Logger.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
