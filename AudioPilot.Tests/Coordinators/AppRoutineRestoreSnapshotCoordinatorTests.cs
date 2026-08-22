using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Coordinators;

public sealed class AppRoutineRestoreSnapshotCoordinatorTests
{
    [Fact]
    public void CaptureSnapshot_ReturnsNull_WhenRoutineIsNotStatefulOrNotRestorable()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-not-restorable.log", LogLevel.Info);
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Desk",
            TriggerKind = RoutineTriggerKind.Hotkey,
            RestorePreviousAudioOnDeactivate = false,
        };

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            routine,
            static () => new RoutineRestoreDeviceInfo("out-1", "Speakers"),
            static () => new RoutineRestoreDeviceInfo("in-1", "Mic"),
            loggerScope.Logger);

        Assert.Null(snapshot);
    }

    [Fact]
    public void CaptureSnapshot_ReturnsNull_WhenNoDefaultDevicesExist()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-no-defaults.log", LogLevel.Info);

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(outputDeviceId: "out-target"),
            static () => new RoutineRestoreDeviceInfo(string.Empty, string.Empty),
            static () => new RoutineRestoreDeviceInfo(string.Empty, string.Empty),
            loggerScope.Logger);

        Assert.Null(snapshot);
    }

    [Fact]
    public void CaptureSnapshot_ReturnsSnapshot_WhenDefaultDevicesExist()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-defaults.log", LogLevel.Info);

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(outputDeviceId: "out-target", inputDeviceId: "in-target"),
            static () => new RoutineRestoreDeviceInfo("out-1", "Speakers", 42f, false),
            static () => new RoutineRestoreDeviceInfo("in-1", "Mic", 11f, true),
            loggerScope.Logger);

        Assert.True(snapshot.HasValue);
        Assert.Equal("out-1", snapshot.Value.PreviousOutputDeviceId);
        Assert.Equal("Speakers", snapshot.Value.PreviousOutputDeviceName);
        Assert.Equal("in-1", snapshot.Value.PreviousInputDeviceId);
        Assert.Equal("Mic", snapshot.Value.PreviousInputDeviceName);
        Assert.Equal(42f, snapshot.Value.PreviousOutputVolumePercent);
        Assert.False(snapshot.Value.PreviousOutputMuted);
        Assert.Equal(11f, snapshot.Value.PreviousInputVolumePercent);
        Assert.True(snapshot.Value.PreviousInputMuted);
    }

    [Fact]
    public void CaptureSnapshot_CapturesOnlyOutput_ForOutputOnlyRoutine()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-output-only.log", LogLevel.Info);
        int inputReads = 0;

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(outputDeviceId: "out-target"),
            static () => new RoutineRestoreDeviceInfo("out-1", "Speakers", 42f, false),
            () =>
            {
                inputReads++;
                return new RoutineRestoreDeviceInfo("in-1", "Mic", 11f, true);
            },
            loggerScope.Logger);

        Assert.True(snapshot.HasValue);
        Assert.Equal("out-1", snapshot.Value.PreviousOutputDeviceId);
        Assert.Equal("Speakers", snapshot.Value.PreviousOutputDeviceName);
        Assert.Equal(42f, snapshot.Value.PreviousOutputVolumePercent);
        Assert.Equal(string.Empty, snapshot.Value.PreviousInputDeviceId);
        Assert.Null(snapshot.Value.PreviousInputVolumePercent);
        Assert.Equal(0, inputReads);
    }

    [Fact]
    public void CaptureSnapshot_CapturesOnlyInput_ForInputOnlyRoutine()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-input-only.log", LogLevel.Info);
        int outputReads = 0;

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(inputDeviceId: "in-target"),
            () =>
            {
                outputReads++;
                return new RoutineRestoreDeviceInfo("out-1", "Speakers", 42f, false);
            },
            static () => new RoutineRestoreDeviceInfo("in-1", "Mic", 11f, true),
            loggerScope.Logger);

        Assert.True(snapshot.HasValue);
        Assert.Equal(string.Empty, snapshot.Value.PreviousOutputDeviceId);
        Assert.Null(snapshot.Value.PreviousOutputVolumePercent);
        Assert.Equal("in-1", snapshot.Value.PreviousInputDeviceId);
        Assert.Equal("Mic", snapshot.Value.PreviousInputDeviceName);
        Assert.Equal(11f, snapshot.Value.PreviousInputVolumePercent);
        Assert.Equal(0, outputReads);
    }

    [Fact]
    public void CaptureSnapshot_CapturesVolumeOnlyState_WithoutDeviceRestoreTarget()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-volume-only.log", LogLevel.Info);

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(masterVolumePercent: 25),
            static () => new RoutineRestoreDeviceInfo("out-1", "Speakers", 42f, false),
            static () => new RoutineRestoreDeviceInfo("in-1", "Mic", 11f, true),
            loggerScope.Logger);

        Assert.True(snapshot.HasValue);
        Assert.Equal(string.Empty, snapshot.Value.PreviousOutputDeviceId);
        Assert.Equal(42f, snapshot.Value.PreviousOutputVolumePercent);
        Assert.False(snapshot.Value.PreviousOutputMuted);
        Assert.Equal(string.Empty, snapshot.Value.PreviousInputDeviceId);
        Assert.Null(snapshot.Value.PreviousInputVolumePercent);
    }

    [Fact]
    public void CaptureSnapshot_SkipsDeviceRestoreTargets_ForAppOnlyRouting()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-app-only.log", LogLevel.Info);

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(outputDeviceId: "out-target", inputDeviceId: "in-target", switchOutputPerApp: true),
            static () => new RoutineRestoreDeviceInfo("out-1", "Speakers", 42f, false),
            static () => new RoutineRestoreDeviceInfo("in-1", "Mic", 11f, true),
            loggerScope.Logger);

        Assert.Null(snapshot);
    }

    [Fact]
    public void CaptureSnapshot_PreservesCapturedOutput_WhenRequestedRecordingReadThrows()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppRoutineRestoreSnapshotCoordinatorTests), "routine-snapshot-recording-throws.log", LogLevel.Info);

        var snapshot = AppRoutineRestoreSnapshotCoordinator.CaptureSnapshot(
            BuildRoutine(outputDeviceId: "out-target", inputDeviceId: "in-target"),
            static () => new RoutineRestoreDeviceInfo("out-1", "Speakers"),
            static () => throw new InvalidOperationException("recording failed"),
            loggerScope.Logger);

        Assert.True(snapshot.HasValue);
        Assert.Equal("out-1", snapshot.Value.PreviousOutputDeviceId);
        Assert.Equal("Speakers", snapshot.Value.PreviousOutputDeviceName);
        Assert.Equal(string.Empty, snapshot.Value.PreviousInputDeviceId);
    }

    private static AudioRoutine BuildRoutine(
        string? outputDeviceId = null,
        string? inputDeviceId = null,
        int? masterVolumePercent = null,
        int? micVolumePercent = null,
        bool switchOutputPerApp = false)
    {
        return new AudioRoutine
        {
            Id = "routine-1",
            Name = "Desk",
            TriggerKind = RoutineTriggerKind.Application,
            UsesApplicationTrigger = true,
            RestorePreviousAudioOnDeactivate = true,
            OutputDeviceId = outputDeviceId ?? string.Empty,
            InputDeviceId = inputDeviceId ?? string.Empty,
            MasterVolumePercent = masterVolumePercent,
            MicVolumePercent = micVolumePercent,
            SwitchOutputPerApp = switchOutputPerApp,
        };
    }
}
