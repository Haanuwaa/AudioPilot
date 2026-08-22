using AudioPilot.Cli;
using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Cli;

public sealed class CliRoutineExecutionPolicyTests
{

    [Fact]
    public void ManualSystemRoutine_DoesNotRequireItsAutomaticTriggerApplication()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        var routine = new AudioRoutine { TriggerKind = RoutineTriggerKind.Application, TriggerAppPath = @"C:\Apps\Missing.exe", OutputDeviceId = "out" };
        Assert.True(CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, provider, out var pid, out var code, out _));
        Assert.Null(pid);
        Assert.Null(code);
        Assert.Equal(0, provider.CaptureAllCallCount);
    }

    [Theory]
    [InlineData(RoutineTriggerKind.Hotkey)]
    [InlineData(RoutineTriggerKind.Scheduled)]
    [InlineData(RoutineTriggerKind.Network)]
    [InlineData(RoutineTriggerKind.Application)]
    public void AppRouting_ResolvesActionTargetIndependentlyOfTrigger(RoutineTriggerKind kind)
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new(42, @"C:\Apps\Trigger.exe"));
        provider.CaptureAllSnapshots.Add(new(84, @"C:\Apps\Target.exe"));
        var routine = new AudioRoutine { TriggerKind = kind, TriggerAppPath = @"C:\Apps\Trigger.exe", TargetAppPath = @"C:\Apps\Target.exe", SwitchOutputPerApp = true, OutputDeviceId = "out" };
        Assert.True(CliRoutineExecutionPolicy.TryResolveManualRunProcessId(routine, provider, out var pid, out _, out _));
        Assert.Equal(84, pid);
    }

    [Fact]
    public void TryResolveManualRunProcessId_WhenExecutableSnapshotMatches_UsesSharedSnapshotProvider()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new RoutineProcessSnapshot(
            24,
            @"C:\Apps\Spotify\Spotify.exe"));

        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Spotify",
            UsesApplicationTrigger = true,
            SwitchOutputPerApp = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.True(resolved);
        Assert.Equal(24, processId);
        Assert.Null(errorCode);
        Assert.Null(errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(0, provider.TryCaptureCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.None, Assert.Single(provider.CaptureAllOptionsHistory));
    }

    [Fact]
    public void TryResolveManualRunProcessId_WhenPackagedSnapshotMatches_UsesSharedSnapshotProvider()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new RoutineProcessSnapshot(
            12,
            @"C:\Program Files\WindowsApps\Spotify\Spotify.exe",
            AppUserModelId: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));

        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Spotify",
            UsesApplicationTrigger = true,
            SwitchOutputPerApp = true,
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.True(resolved);
        Assert.Equal(12, processId);
        Assert.Null(errorCode);
        Assert.Null(errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId, Assert.Single(provider.CaptureAllOptionsHistory));
    }

    [Fact]
    public void TryResolveManualRunProcessId_WhenPackagedSnapshotHasNoAumid_MatchesWindowsAppsPackageFamily()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        provider.CaptureAllSnapshots.Add(new RoutineProcessSnapshot(
            42,
            @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2512.29.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe"));

        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Notepad",
            UsesApplicationTrigger = true,
            SwitchOutputPerApp = true,
            TriggerAppPath = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.True(resolved);
        Assert.Equal(42, processId);
        Assert.Null(errorCode);
        Assert.Null(errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.IncludeAppUserModelId, Assert.Single(provider.CaptureAllOptionsHistory));
    }

    [Fact]
    public void TryResolveManualRunProcessId_WhenNoSnapshotMatches_ReturnsExpectedFailure()
    {
        var provider = new FakeRoutineProcessSnapshotProvider();
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Name = "Spotify",
            UsesApplicationTrigger = true,
            SwitchOutputPerApp = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            OutputDeviceId = "out-1",
        };

        bool resolved = CliRoutineExecutionPolicy.TryResolveManualRunProcessId(
            routine,
            provider,
            out int? processId,
            out string? errorCode,
            out string? errorMessage);

        Assert.False(resolved);
        Assert.Null(processId);
        Assert.Equal("routine-target-app-not-running", errorCode);
        Assert.Equal("Routine 'Spotify' requires the target application 'Spotify' to be running.", errorMessage);
        Assert.Equal(1, provider.CaptureAllCallCount);
        Assert.Equal(RoutineProcessSnapshotCaptureOptions.None, Assert.Single(provider.CaptureAllOptionsHistory));
    }
}
