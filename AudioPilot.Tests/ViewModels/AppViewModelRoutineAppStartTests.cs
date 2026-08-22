using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineAppStartTests
{
    [Fact]
    public void GetAudioPilotStartupTriggeredRoutinesForExecution_ReturnsOnlyEnabledMatchingRoutines()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-1",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-2",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            },
            new()
            {
                Id = "routine-3",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Hotkey,
                OutputDeviceId = "out-3",
                OutputDeviceName = "Desk",
            },
            new()
            {
                Id = "routine-4",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.AudioPilotStartup,
                MasterVolumePercent = 35,
            }
        ];

        List<AudioRoutine> result = AppViewModel.GetAudioPilotStartupTriggeredRoutinesForExecution(routines);

        Assert.Equal(["routine-1", "routine-4"], [.. result.Select(static routine => routine.Id)]);
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_MatchesExactPathProcess()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Spotify",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(21, @"C:\Apps\Spotify\Spotify.exe");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        AppViewModel.RoutineAppStartMatch matched = Assert.Single(result);
        Assert.Equal("routine-1", matched.Routine.Id);
        Assert.Equal(21, matched.ProcessId);
        Assert.Same(watchedRoutines[0], matched.Routine);
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_IgnoresNonMatchingProcess()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Spotify",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(21, @"C:\Apps\Discord\Discord.exe");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        Assert.Empty(result);
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_MatchesMultipleRoutinesForSameExecutable()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            },
            new AudioRoutine
            {
                Id = "routine-2",
                Name = "Mic",
                Enabled = true,
                InputDeviceId = "in-1",
                InputDeviceName = "Studio Mic",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(30, @"C:\Apps\Spotify\Spotify.exe");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        Assert.Equal(["routine-1", "routine-2"], [.. result.Select(static match => match.Routine.Id)]);
        Assert.All(result, static match => Assert.Equal(30, match.ProcessId));
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_MatchesPackagedAppByAumid()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Spotify",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(
            21,
            @"C:\Windows\System32\ApplicationFrameHost.exe",
            1,
            "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        AppViewModel.RoutineAppStartMatch matched = Assert.Single(result);
        Assert.Equal("routine-1", matched.Routine.Id);
        Assert.Equal(21, matched.ProcessId);
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_MatchesPackagedAppByWindowsAppsExecutablePath_WhenAumidIsUnavailable()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Notepad",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(
            21,
            @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2512.29.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        AppViewModel.RoutineAppStartMatch matched = Assert.Single(result);
        Assert.Equal("routine-1", matched.Routine.Id);
        Assert.Equal(21, matched.ProcessId);
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_MatchesSteamWebHelper_WhenTargetIsSteamExe()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Steam",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Program Files (x86)\Steam\steam.exe",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(
            6672,
            @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        AppViewModel.RoutineAppStartMatch matched = Assert.Single(result);
        Assert.Equal("routine-1", matched.Routine.Id);
        Assert.Equal(6672, matched.ProcessId);
    }

    [Fact]
    public void EvaluateRoutineAppStartMatchesForProcess_MatchesSquirrelAppExe_WhenTargetIsUpdateExe()
    {
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Discord",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Users\ExampleUser\AppData\Local\Discord\Update.exe",
            }
        ];
        var snapshot = new RoutineProcessSnapshot(
            3528,
            @"C:\Users\ExampleUser\AppData\Local\Discord\app-1.0.9236\Discord.exe");

        IReadOnlyList<AppViewModel.RoutineAppStartMatch> result = AppViewModel.EvaluateRoutineAppStartMatchesForProcess(watchedRoutines, snapshot);

        AppViewModel.RoutineAppStartMatch matched = Assert.Single(result);
        Assert.Equal("routine-1", matched.Routine.Id);
        Assert.Equal(3528, matched.ProcessId);
    }

    [Fact]
    public void ShouldCaptureProcessSnapshotsForStartedMatches_ReturnsFalse_WhenNoPerAppRoutingMatches()
    {
        List<AppViewModel.RoutineAppStartMatch> matches =
        [
            new(
                new AudioRoutine
                {
                    Id = "routine-1",
                    Enabled = true,
                    UsesApplicationTrigger = true,
                    TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                    OutputDeviceId = "out-1",
                    SwitchOutputPerApp = false,
                },
                100)
        ];

        bool result = AppViewModel.ShouldCaptureProcessSnapshotsForStartedMatches(matches, activeLeaseCount: 1);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCaptureProcessSnapshotsForStartedMatches_ReturnsFalse_WhenPerAppRoutingMatchesExistButNoActiveLeases()
    {
        List<AppViewModel.RoutineAppStartMatch> matches =
        [
            new(
                new AudioRoutine
                {
                    Id = "routine-1",
                    Enabled = true,
                    UsesApplicationTrigger = true,
                    TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                    OutputDeviceId = "out-1",
                    SwitchOutputPerApp = true,
                },
                100)
        ];

        bool result = AppViewModel.ShouldCaptureProcessSnapshotsForStartedMatches(matches, activeLeaseCount: 0);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCaptureProcessSnapshotsForStartedMatches_ReturnsTrue_WhenPerAppRoutingMatchesExistAndActiveLeasesRemain()
    {
        List<AppViewModel.RoutineAppStartMatch> matches =
        [
            new(
                new AudioRoutine
                {
                    Id = "routine-1",
                    Enabled = true,
                    UsesApplicationTrigger = true,
                    TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                    OutputDeviceId = "out-1",
                    SwitchOutputPerApp = true,
                },
                100)
        ];

        bool result = AppViewModel.ShouldCaptureProcessSnapshotsForStartedMatches(matches, activeLeaseCount: 1);

        Assert.True(result);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(2, 3, true)]
    public void ShouldCaptureProcessSnapshotsForStoppedProcess_ReturnsExpectedValue(
        int activeLeaseCount,
        int activeAppStartStatefulSessionCount,
        bool expected)
    {
        bool result = AppViewModel.ShouldCaptureProcessSnapshotsForStoppedProcess(activeLeaseCount, activeAppStartStatefulSessionCount);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CollectRoutineAppOutputCandidateProcessIds_IncludesExactPathDescendantsAndSessionProcesses()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(101, @"C:\Apps\Spotify\Spotify.exe", 100),
            new(102, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100),
            new(200, @"C:\Apps\Discord\Discord.exe", 1),
        ];
        List<AudioSessionSnapshot> sessionSnapshots =
        [
            new("Spotify", 80f, "Speakers", "Spotify", null, 102),
            new("Discord", 65f, "Headset", "Discord", null, 200),
        ];

        IReadOnlyList<uint> result = AppViewModel.CollectRoutineAppOutputCandidateProcessIds(
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            snapshots,
            sessionSnapshots);

        Assert.Equal([100u, 101u, 102u], result);
    }

    [Fact]
    public void CollectRoutineAppOutputCandidateProcessIds_IncludesPackagedMatchesDescendantsAndSessionProcesses()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new(101, @"C:\Windows\System32\WWAHost.exe", 100),
            new(102, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new(200, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        ];
        List<AudioSessionSnapshot> sessionSnapshots =
        [
            new("Spotify", 80f, "Speakers", "Spotify", null, 101),
            new("Spotify", 75f, "Speakers", "Spotify", null, 102),
            new("Calculator", 65f, "Speakers", "Calculator", null, 200),
        ];

        IReadOnlyList<uint> result = AppViewModel.CollectRoutineAppOutputCandidateProcessIds(
            100,
            "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            snapshots,
            sessionSnapshots);

        Assert.Equal([100u, 101u], result);
    }

    [Fact]
    public void FindRunningRoutineTriggerProcessId_ReturnsLowestMatchingProcessId()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
        };
        List<RoutineProcessSnapshot> snapshots =
        [
            new(205, @"C:\Apps\Discord\Discord.exe", 1),
            new(120, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(140, @"C:\Apps\Spotify\Spotify.exe", 1),
        ];

        int? result = AppViewModel.FindRunningRoutineTriggerProcessId(routine, snapshots);

        Assert.Equal(120, result);
    }

    [Fact]
    public void FindRunningRoutineTriggerProcessId_ReturnsMatchingPackagedAppProcessId()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        };
        List<RoutineProcessSnapshot> snapshots =
        [
            new(205, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            new(120, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new(140, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
        ];

        int? result = AppViewModel.FindRunningRoutineTriggerProcessId(routine, snapshots);

        Assert.Equal(120, result);
    }

    [Fact]
    public void FindRunningRoutineTriggerProcessId_NormalizesFileUriExecutableTarget()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = "file:///C:/Apps/Spotify/Spotify.exe",
        };
        List<RoutineProcessSnapshot> snapshots =
        [
            new(205, @"C:\Apps\Discord\Discord.exe", 1),
            new(120, @"C:\Apps\Spotify\Spotify.exe", 1),
        ];

        int? result = AppViewModel.FindRunningRoutineTriggerProcessId(routine, snapshots);

        Assert.Equal(120, result);
    }

    [Fact]
    public void FindRunningProcessIdsForTriggerTarget_ReturnsMatchingPackagedAppProcessIdsInOrder()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(205, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            new(140, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new(120, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new(300, @"C:\Windows\System32\WWAHost.exe", 120),
        ];

        IReadOnlyList<int> result = AppViewModel.FindRunningProcessIdsForTriggerTarget(
            "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            snapshots);

        Assert.Equal([120, 140], result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsTrue_WhenSteamWindowTitleAndClassMatchBigPicture()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
            new(140, @"C:\Program Files (x86)\Steam\steamwebhelper.exe", 120),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [140] = [new AudioDeviceHelper.VisibleWindowMetadata("Steam Big Picture Mode", "SDL_app")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.True(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsFalse_WhenBigPictureTitleUsesUnexpectedClass()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
            new(140, @"C:\Program Files (x86)\Steam\steamwebhelper.exe", 120),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [140] = [new AudioDeviceHelper.VisibleWindowMetadata("A Big Picture announcement", "Chrome_WidgetWin_1")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.False(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsTrue_WhenSteamWindowClassAndTitleMatchSoundSwitchHeuristic()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
            new(140, @"C:\Program Files (x86)\Steam\steamwebhelper.exe", 120),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [140] = [new AudioDeviceHelper.VisibleWindowMetadata("Steam Big Picture Mode", "SDL_app")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.True(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsTrue_WhenSteamHelperWindowMatchesSteamFallbackSignature()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steamui.exe", 1),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [120] = [new AudioDeviceHelper.VisibleWindowMetadata("Steam", "CUIEngineWin32")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.True(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsFalse_WhenRootSteamWindowOnlyMatchesLegacyFallbackSignature()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [120] = [new AudioDeviceHelper.VisibleWindowMetadata("Steam", "CUIEngineWin32")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.False(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsTrue_WhenSteamWindowMatchesSpFallbackSignature()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steamui.exe", 1),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [120] = [new AudioDeviceHelper.VisibleWindowMetadata("SP", "SDL_app")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.True(result);
    }

    [Fact]
    public void GetSteamBigPictureWindowHandles_ReturnsMatchingHandles()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
            new(140, @"C:\Program Files (x86)\Steam\steamwebhelper.exe", 120),
        ];

        IReadOnlyList<AudioDeviceHelper.VisibleWindowHandleMetadata> visibleWindows =
        [
            new((nint)10, 140, "Steam Big Picture Mode", "SDL_app"),
            new((nint)11, 120, "Steam", "Chrome_WidgetWin_1"),
        ];

        IReadOnlyList<nint> result = AppViewModel.GetSteamBigPictureWindowHandles(snapshots, visibleWindows, trace: null);

        Assert.Equal([(nint)10], result);
    }

    [Fact]
    public void GetSteamBigPictureWindowHandles_ExcludesRootSteamLegacyFallbackWindow()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
        ];

        IReadOnlyList<AudioDeviceHelper.VisibleWindowHandleMetadata> visibleWindows =
        [
            new((nint)10, 120, "Steam", "CUIEngineWin32"),
        ];

        IReadOnlyList<nint> result = AppViewModel.GetSteamBigPictureWindowHandles(snapshots, visibleWindows, trace: null);

        Assert.Empty(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_ReturnsFalse_WhenMatchingWindowBelongsToNonSteamProcess()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files\Discord\Discord.exe", 1),
        ];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [120] = [new AudioDeviceHelper.VisibleWindowMetadata("Steam Big Picture Mode", "SDL_app")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, trace: null);

        Assert.False(result);
    }

    [Fact]
    public void IsSteamBigPictureActive_EmitsTraceDiagnostics_WhenSteamWindowIsRejected()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(120, @"C:\Program Files (x86)\Steam\steam.exe", 1),
        ];
        List<string> traces = [];

        IReadOnlyDictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>> visibleWindowsByProcessId = new Dictionary<int, IReadOnlyList<AudioDeviceHelper.VisibleWindowMetadata>>
        {
            [120] = [new AudioDeviceHelper.VisibleWindowMetadata("Steam", "Chrome_WidgetWin_1")],
        };

        bool result = AppViewModel.IsSteamBigPictureActive(snapshots, visibleWindowsByProcessId, traces.Add);

        Assert.False(result);
        Assert.Contains(traces, static trace => trace.Contains("steam-big-picture-window-rejected", StringComparison.Ordinal));
        Assert.Contains(traces, static trace => trace.Contains("processId=id[len=3 hash=", StringComparison.Ordinal));
        Assert.DoesNotContain(traces, static trace => trace.Contains("processId=120", StringComparison.Ordinal));
        Assert.Contains(traces, static trace => trace.Contains("result=inactive", StringComparison.Ordinal));
    }

    [Fact]
    public void GetDeviceChangeTriggeredRoutinesForExecution_OrdersByDisplayOrder_WhenMultipleRoutinesTargetSameFlow()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-2",
                Name = "Speakers Later",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 2,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            },
            new()
            {
                Id = "routine-1",
                Name = "Speakers First",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-3",
                Name = "Disabled",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 3,
                OutputDeviceId = "out-3",
                OutputDeviceName = "Monitor",
            }
        ];

        List<AudioRoutine> result = AppViewModel.GetDeviceChangeTriggeredRoutinesForExecution(routines);

        Assert.Equal(["routine-1", "routine-2"], [.. result.Select(static routine => routine.Id)]);
    }

    [Fact]
    public void GetDeviceChangeTriggeredRoutinesForExecution_FiltersOutDisabledWrongTriggerAndTargetlessRoutines()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "eligible-output",
                Name = "Eligible Output",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 1,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "eligible-input",
                Name = "Eligible Input",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 2,
                InputDeviceId = "in-1",
                InputDeviceName = "Mic",
            },
            new()
            {
                Id = "eligible-volume",
                Name = "Eligible Volume",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 3,
                MasterVolumePercent = 40,
            },
            new()
            {
                Id = "disabled",
                Name = "Disabled",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 4,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            },
            new()
            {
                Id = "wrong-trigger",
                Name = "Wrong Trigger",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Hotkey,
                DisplayOrder = 5,
                OutputDeviceId = "out-3",
                OutputDeviceName = "Monitor",
            },
            new()
            {
                Id = "no-targets",
                Name = "No Targets",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 6,
            }
        ];

        List<AudioRoutine> result = AppViewModel.GetDeviceChangeTriggeredRoutinesForExecution(routines);

        Assert.Equal(["eligible-output", "eligible-input", "eligible-volume"], [.. result.Select(static routine => routine.Id)]);
        Assert.All(result, routine => Assert.NotSame(routine, routines.First(candidate => candidate.Id == routine.Id)));
    }

    [Fact]
    public void GetDeviceChangeTriggeredRoutinesForExecution_OrdersByNameWhenDisplayOrderMatches()
    {
        List<AudioRoutine> routines =
        [
            new()
            {
                Id = "routine-z",
                Name = "Zulu",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 5,
                OutputDeviceId = "out-z",
                OutputDeviceName = "Headset",
            },
            new()
            {
                Id = "routine-a",
                Name = "Alpha",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 5,
                OutputDeviceId = "out-a",
                OutputDeviceName = "Speakers",
            },
            new()
            {
                Id = "routine-m",
                Name = "Mike",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.DeviceChange,
                DisplayOrder = 5,
                InputDeviceId = "in-m",
                InputDeviceName = "Mic",
            }
        ];

        List<AudioRoutine> result = AppViewModel.GetDeviceChangeTriggeredRoutinesForExecution(routines);

        Assert.Equal(["routine-a", "routine-m", "routine-z"], [.. result.Select(static routine => routine.Id)]);
    }

    [Fact]
    public async Task DeactivateRoutineStatefulSessionAsync_RemovesOlderSessionWithoutAffectingLatest()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        var activeSessions = GetActiveRoutineStatefulSessions(viewModel);
        activeSessions["application-launch:routine-old:100"] = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-old:100",
            "routine-old",
            "Old",
            RoutineTriggerKind.Application,
            activationSequence: 1,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: 100);
        activeSessions["application-launch:routine-new:200"] = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-new:200",
            "routine-new",
            "New",
            RoutineTriggerKind.Application,
            activationSequence: 2,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: 200);

        await InvokeDeactivateRoutineStatefulSessionAsync(viewModel, "application-launch:routine-old:100");

        Assert.DoesNotContain("application-launch:routine-old:100", activeSessions.Keys);
        Assert.Contains("application-launch:routine-new:200", activeSessions.Keys);
    }

    [Fact]
    public async Task DeactivateRoutineStatefulSessionAsync_RemovesLatestSession_WhenRestoreSnapshotIsMissing()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        var activeSessions = GetActiveRoutineStatefulSessions(viewModel);
        activeSessions["steam-big-picture:routine-steam"] = new AppViewModel.RoutineStatefulSession(
            "steam-big-picture:routine-steam",
            "routine-steam",
            "Steam",
            RoutineTriggerKind.SteamBigPicture,
            activationSequence: 4,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: null);

        await InvokeDeactivateRoutineStatefulSessionAsync(viewModel, "steam-big-picture:routine-steam");

        Assert.Empty(activeSessions);
    }

    [Fact]
    public async Task DeactivateRoutineStatefulSessionAsync_ClearsWaitingForAppAudio_WhenAppExitsBeforeAudioAppears()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        viewModel.ApplyRoutinesFromSettings(
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Notepad",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ]);
        viewModel.SetRoutineLastRunStateForTests("routine-1", RoutineLastRunState.WaitingForApp);
        var activeSessions = GetActiveRoutineStatefulSessions(viewModel);
        activeSessions["application-launch:routine-1:100"] = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-1:100",
            "routine-1",
            "Notepad",
            RoutineTriggerKind.Application,
            activationSequence: 1,
            restorePreviousAudioOnDeactivate: false,
            restoreSnapshot: null,
            rootProcessId: 100);

        await InvokeDeactivateRoutineStatefulSessionAsync(viewModel, "application-launch:routine-1:100");

        AudioRoutine routine = Assert.Single(viewModel.Routines);
        Assert.Equal(RoutineLastRunState.Skipped, routine.LastRunState);
        Assert.Equal("App closed before audio appeared", routine.LastRunDetail);
        Assert.Contains("App closed before audio appeared", routine.LastRunStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeactivateRoutineStatefulSessionAsync_KeepsWaitingForAppAudio_WhenAnotherSameRoutineSessionRemains()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        viewModel.ApplyRoutinesFromSettings(
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Notepad",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ]);
        viewModel.SetRoutineLastRunStateForTests("routine-1", RoutineLastRunState.WaitingForApp);
        var activeSessions = GetActiveRoutineStatefulSessions(viewModel);
        activeSessions["application-launch:routine-1:100"] = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-1:100",
            "routine-1",
            "Notepad",
            RoutineTriggerKind.Application,
            activationSequence: 1,
            restorePreviousAudioOnDeactivate: false,
            restoreSnapshot: null,
            rootProcessId: 100);
        activeSessions["application-launch:routine-1:200"] = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-1:200",
            "routine-1",
            "Notepad",
            RoutineTriggerKind.Application,
            activationSequence: 2,
            restorePreviousAudioOnDeactivate: false,
            restoreSnapshot: null,
            rootProcessId: 200);

        await InvokeDeactivateRoutineStatefulSessionAsync(viewModel, "application-launch:routine-1:100");

        AudioRoutine routine = Assert.Single(viewModel.Routines);
        Assert.Equal(RoutineLastRunState.WaitingForApp, routine.LastRunState);
        Assert.Contains("application-launch:routine-1:200", activeSessions.Keys);
    }

    [Fact]
    public void CompletePendingAppAudioWaitForRemovedLeases_ClearsWaitingForAppAudio_WhenLaunchProcessExitsBeforeAudioAppears()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        viewModel.ApplyRoutinesFromSettings(
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Discord",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ]);
        viewModel.SetRoutineLastRunStateForTests("routine-1", RoutineLastRunState.WaitingForApp);

        viewModel.CompletePendingAppAudioWaitForRemovedLeasesForTests(
        [
            new AppViewModel.RoutineAppOutputLease(
                "routine-1:100",
                "routine-1",
                "Discord",
                100,
                @"C:\Apps\Discord\Discord.exe",
                "out-1",
                "Speakers")
        ]);

        AudioRoutine routine = Assert.Single(viewModel.Routines);
        Assert.Equal(RoutineLastRunState.Skipped, routine.LastRunState);
        Assert.Equal("App closed before audio appeared", routine.LastRunDetail);
    }

    [Fact]
    public void CompletePendingAppAudioWaitForRemovedLeases_KeepsWaiting_WhenAnotherLeaseForRoutineRemains()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        viewModel.ApplyRoutinesFromSettings(
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Discord",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ]);
        viewModel.SetRoutineLastRunStateForTests("routine-1", RoutineLastRunState.WaitingForApp);
        viewModel.GetActiveRoutineAppOutputLeasesForTests()["routine-1:200"] = new AppViewModel.RoutineAppOutputLease(
            "routine-1:200",
            "routine-1",
            "Discord",
            200,
            @"C:\Apps\Discord\Discord.exe",
            "out-1",
            "Speakers");

        viewModel.CompletePendingAppAudioWaitForRemovedLeasesForTests(
        [
            new AppViewModel.RoutineAppOutputLease(
                "routine-1:100",
                "routine-1",
                "Discord",
                100,
                @"C:\Apps\Discord\Discord.exe",
                "out-1",
                "Speakers")
        ]);

        AudioRoutine routine = Assert.Single(viewModel.Routines);
        Assert.Equal(RoutineLastRunState.WaitingForApp, routine.LastRunState);
    }

    [Fact]
    public void RegisterRoutineAppOutputLease_WhenSameProcessRerunsDeferred_ClearsCompletedOutputState()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Discord",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            SwitchOutputPerApp = true,
        };
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:28776",
            "routine-1",
            "Discord",
            28776,
            @"C:\Apps\Discord\Discord.exe",
            "out-1",
            "Speakers")
        {
            CompletionOverlayShown = true,
        };
        lease.AppliedOutputProcessIds.UnionWith([28776u, 28800u, 28812u]);
        activeLeases[lease.LeaseKey] = lease;
        viewModel.RecalculateRoutineAppOutputLeasePendingCountsForTests();

        viewModel.RegisterRoutineAppOutputLeaseForTests(
            routine,
            rootProcessId: 28776,
            outputApplied: false,
            inputApplied: false,
            completionOverlayShown: false);

        AppViewModel.RoutineAppOutputLease updatedLease = Assert.Single(activeLeases.Values);
        Assert.False(updatedLease.CompletionOverlayShown);
        Assert.Empty(updatedLease.AppliedOutputProcessIds);
    }

    [Fact]
    public void RegisterRoutineAppOutputLease_WhenSameProcessRerunsApplied_ReplacesCompletionState()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Discord",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            SwitchOutputPerApp = true,
        };
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:28776",
            "routine-1",
            "Discord",
            28776,
            @"C:\Apps\Discord\Discord.exe",
            "out-1",
            "Speakers");
        activeLeases[lease.LeaseKey] = lease;
        viewModel.RecalculateRoutineAppOutputLeasePendingCountsForTests();

        viewModel.RegisterRoutineAppOutputLeaseForTests(
            routine,
            rootProcessId: 28776,
            outputApplied: true,
            inputApplied: false,
            completionOverlayShown: true);

        AppViewModel.RoutineAppOutputLease updatedLease = Assert.Single(activeLeases.Values);
        Assert.True(updatedLease.CompletionOverlayShown);
        Assert.Equal([28776u], [.. updatedLease.AppliedOutputProcessIds]);
    }

    [Fact]
    public void UpdateRoutineAppStartMonitorState_AcquiresSessionMonitoring_ForPendingLeaseTargets()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();
        activeLeases["routine-1:100"] = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Spotify",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers");
        viewModel.RecalculateRoutineAppOutputLeasePendingCountsForTests();

        viewModel.UpdateRoutineAppStartMonitorStateForTests();

        Assert.Equal(1, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
        Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Input));
    }

    [Fact]
    public void UpdateRoutineAppStartMonitorState_ReleasesSessionMonitoring_WhenPendingLeaseWorkCompletes()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Spotify",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers");
        activeLeases["routine-1:100"] = lease;
        viewModel.RecalculateRoutineAppOutputLeasePendingCountsForTests();

        viewModel.UpdateRoutineAppStartMonitorStateForTests();
        Assert.Equal(1, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));

        lease.AppliedOutputProcessIds.Add(100u);
        viewModel.RecalculateRoutineAppOutputLeasePendingCountsForTests();
        viewModel.UpdateRoutineAppStartMonitorStateForTests();

        Assert.Equal(0, harness.Audio.GetSessionMonitoringConsumerCountForTests(AudioMixerMode.Output));
    }

    [Fact]
    public void MarkRoutineAppOutputLeaseCompleted_PromotesWaitingRoutineToSucceeded()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        viewModel.ApplyRoutinesFromSettings(
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Spotify",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                SwitchOutputPerApp = true,
            }
        ]);
        viewModel.SetRoutineLastRunStateForTests("routine-1", RoutineLastRunState.WaitingForApp);

        viewModel.MarkRoutineAppOutputLeaseCompletedForTests(
            new AppViewModel.RoutineAppOutputLease(
                "routine-1:100",
                "routine-1",
                "Spotify",
                100,
                @"C:\Apps\Spotify\Spotify.exe",
                "out-1",
                "Speakers"));

        AudioRoutine routine = Assert.Single(viewModel.Routines);
        Assert.Equal(RoutineLastRunState.Succeeded, routine.LastRunState);
    }

    [Fact]
    public void ReconcileRoutineAppOutputLeases_RemovesLease_WhenRoutineNoLongerWatched()
    {
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["routine-1:100"] = new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        };
        List<AudioRoutine> watchedRoutines = [];
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1)
        ];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.ReconcileRoutineAppOutputLeases(currentLeases, watchedRoutines, snapshots);

        Assert.Empty(result);
    }

    [Fact]
    public void ReconcileRoutineAppOutputLeases_UpdatesLeaseAndRetainsAppliedProcesses_WhenStillAlive()
    {
        var lease = new AppViewModel.RoutineAppOutputLease("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers");
        lease.AppliedOutputProcessIds.Add(100u);

        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            [lease.LeaseKey] = lease
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(102, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100)
        ];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.ReconcileRoutineAppOutputLeases(currentLeases, watchedRoutines, snapshots);

        AppViewModel.RoutineAppOutputLease updatedLease = Assert.Single(result.Values);
        Assert.Equal("out-2", updatedLease.OutputDeviceId);
        Assert.Equal("Headset", updatedLease.OutputDeviceName);
        Assert.Empty(updatedLease.AppliedOutputProcessIds);
    }

    [Fact]
    public void ReconcileRoutineAppOutputLeases_RetainsNewPendingLease_WhenRootSnapshotIsTemporarilyMissing()
    {
        DateTime nowUtc = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
        var lease = new AppViewModel.RoutineAppOutputLease("routine-1:100", "routine-1", "Discord", 100, @"C:\Apps\Discord\Discord.exe", "out-1", "Speakers")
        {
            CreatedUtc = nowUtc,
        };
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            [lease.LeaseKey] = lease
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        ];
        List<RoutineProcessSnapshot> snapshots = [];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.ReconcileRoutineAppOutputLeases(currentLeases, watchedRoutines, snapshots, nowUtc);

        AppViewModel.RoutineAppOutputLease retainedLease = Assert.Single(result.Values);
        Assert.Equal(lease.LeaseKey, retainedLease.LeaseKey);
        Assert.Equal(lease.CreatedUtc, retainedLease.CreatedUtc);
    }

    [Fact]
    public void ReconcileRoutineAppOutputLeases_RemovesOldPendingLease_WhenRootSnapshotIsMissing()
    {
        DateTime nowUtc = new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);
        var lease = new AppViewModel.RoutineAppOutputLease("routine-1:100", "routine-1", "Discord", 100, @"C:\Apps\Discord\Discord.exe", "out-1", "Speakers")
        {
            CreatedUtc = nowUtc - TimeSpan.FromSeconds(10),
        };
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            [lease.LeaseKey] = lease
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        ];
        List<RoutineProcessSnapshot> snapshots = [];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.ReconcileRoutineAppOutputLeases(currentLeases, watchedRoutines, snapshots, nowUtc);

        Assert.Empty(result);
    }

    [Fact]
    public void ReconcileRoutineAppOutputLeases_RemovesCompletedLease_WhenRootSnapshotIsMissing()
    {
        var lease = new AppViewModel.RoutineAppOutputLease("routine-1:100", "routine-1", "Discord", 100, @"C:\Apps\Discord\Discord.exe", "out-1", "Speakers")
        {
            CreatedUtc = DateTime.UtcNow,
        };
        lease.AppliedOutputProcessIds.Add(100u);
        lease.CompletionOverlayShown = true;
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            [lease.LeaseKey] = lease
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        ];
        List<RoutineProcessSnapshot> snapshots = [];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.ReconcileRoutineAppOutputLeases(currentLeases, watchedRoutines, snapshots);

        Assert.Empty(result);
    }

    [Fact]
    public void ReconcileRoutineAppOutputLeases_RetainsMultipleRoots_ForSameRoutine()
    {
        var firstLease = new AppViewModel.RoutineAppOutputLease("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers");
        var secondLease = new AppViewModel.RoutineAppOutputLease("routine-1:200", "routine-1", "Spotify", 200, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers");

        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            [firstLease.LeaseKey] = firstLease,
            [secondLease.LeaseKey] = secondLease,
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-2",
                OutputDeviceName = "Headset",
            }
        ];
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(101, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100),
            new(200, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(201, @"C:\Apps\Spotify\SpotifyPlayer.exe", 200),
        ];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.ReconcileRoutineAppOutputLeases(currentLeases, watchedRoutines, snapshots);

        Assert.Equal(2, result.Count);
        Assert.Contains("routine-1:100", result.Keys);
        Assert.Contains("routine-1:200", result.Keys);
        Assert.All(result.Values, lease => Assert.Equal("out-2", lease.OutputDeviceId));
    }

    [Fact]
    public void SynchronizeRoutineAppOutputLeasesWithWatchedRoutines_KeepsInputOnlyLease()
    {
        Dictionary<string, AppViewModel.RoutineAppOutputLease> currentLeases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["routine-1:100"] = new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "", "", "in-1", "Microphone")
        };
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Spotify Mic",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                SwitchOutputPerApp = true,
                InputDeviceId = "in-2",
                InputDeviceName = "Studio Mic",
            }
        ];

        Dictionary<string, AppViewModel.RoutineAppOutputLease> result = AppViewModel.SynchronizeRoutineAppOutputLeasesWithWatchedRoutines(currentLeases, watchedRoutines);

        AppViewModel.RoutineAppOutputLease updatedLease = Assert.Single(result.Values);
        Assert.Equal("in-2", updatedLease.InputDeviceId);
        Assert.Equal("Studio Mic", updatedLease.InputDeviceName);
    }

    [Fact]
    public void IsRoutineAppOutputLeaseAlive_ReturnsFalse_WhenOnlyUnrelatedSamePathProcessRemains()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(300, @"C:\Apps\Spotify\Spotify.exe", 1)
        ];

        bool alive = AppViewModel.IsRoutineAppOutputLeaseAlive(100, @"C:\Apps\Spotify\Spotify.exe", snapshots);

        Assert.False(alive);
    }

    [Fact]
    public void IsRoutineAppOutputLeaseAlive_ReturnsFalse_WhenRootPidWasReusedByDifferentExecutable()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Discord\Discord.exe", 1),
            new(101, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100),
        ];

        bool alive = AppViewModel.IsRoutineAppOutputLeaseAlive(100, @"C:\Apps\Spotify\Spotify.exe", snapshots);

        Assert.False(alive);
    }

    [Fact]
    public void IsRoutineAppOutputLeaseAlive_ReturnsTrue_WhenPackagedRootAndDescendantRemain()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
            new(101, @"C:\Windows\System32\WWAHost.exe", 100),
            new(200, @"C:\Windows\System32\ApplicationFrameHost.exe", 1, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        ];

        bool alive = AppViewModel.IsRoutineAppOutputLeaseAlive(100, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", snapshots);

        Assert.True(alive);
    }

    [Fact]
    public void CollectRoutineAppOutputCandidateProcessIds_DoesNotIncludeUnrelatedSiblingRoot()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(101, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100),
            new(200, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(201, @"C:\Apps\Spotify\SpotifyPlayer.exe", 200),
        ];
        List<AudioSessionSnapshot> sessionSnapshots =
        [
            new("Spotify Root One", 80f, "Speakers", "Spotify", null, 101),
            new("Spotify Root Two", 75f, "Speakers", "Spotify", null, 201),
        ];

        IReadOnlyList<uint> result = AppViewModel.CollectRoutineAppOutputCandidateProcessIds(
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            snapshots,
            sessionSnapshots);

        Assert.Equal([100u, 101u], result);
    }

    [Fact]
    public void CollectRoutineAppOutputCandidateProcessIds_DoesNotIncludeReusedRootProcessTree()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Discord\Discord.exe", 1),
            new(101, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100),
        ];
        List<AudioSessionSnapshot> sessionSnapshots =
        [
            new("Spotify", 80f, "Speakers", "Spotify", null, 101),
        ];

        IReadOnlyList<uint> result = AppViewModel.CollectRoutineAppOutputCandidateProcessIds(
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            snapshots,
            sessionSnapshots);

        Assert.Empty(result);
    }

    [Fact]
    public void CreateRoutineAppStartSnapshotSet_UsesLastSnapshotForDuplicatePid()
    {
        List<RoutineProcessSnapshot> snapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(100, @"C:\Apps\Spotify\SpotifyPlayer.exe", 50),
        ];

        AppViewModel.RoutineAppStartSnapshotSet snapshotSet = AppViewModel.CreateRoutineAppStartSnapshotSet(snapshots);

        Assert.Equal(2, snapshotSet.Snapshots.Count);
        Assert.True(snapshotSet.SnapshotsByPid.TryGetValue(100, out RoutineProcessSnapshot snapshot));
        Assert.Equal(@"C:\Apps\Spotify\SpotifyPlayer.exe", snapshot.ExecutablePath);
        Assert.Equal(50, snapshot.ParentProcessId);
    }

    [Fact]
    public void CalculateTransition_ReturnsStartedAndStoppedProcessIds()
    {
        HashSet<int> previousProcessIds = [10, 20, 30];
        HashSet<int> currentProcessIds = [20, 30, 40, 50];

        ProcessLifecycleMonitorTransition result = PollingProcessLifecycleMonitor.CalculateTransition(previousProcessIds, currentProcessIds);

        Assert.Equal([40, 50], result.StartedProcessIds);
        Assert.Equal([10], result.StoppedProcessIds);
    }

    [Fact]
    public void ShouldSkipRoutineAppStartMatchForExistingLease_ReturnsTrue_ForDescendantOfExistingLease()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };
        AppViewModel.RoutineAppStartMatch match = new(routine, 101);
        RoutineProcessSnapshot processSnapshot = new(101, @"C:\Apps\Spotify\Spotify.exe", 100);
        List<AppViewModel.RoutineAppOutputLease> activeLeases =
        [
            new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        ];
        List<RoutineProcessSnapshot> processSnapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            processSnapshot,
        ];

        bool result = AppViewModel.ShouldSkipRoutineAppStartMatchForExistingLease(match, processSnapshot, activeLeases, processSnapshots);

        Assert.True(result);
    }

    [Fact]
    public void ShouldSkipRoutineAppStartMatchForExistingLease_ReturnsFalse_ForSeparateRootOfSameRoutine()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };
        AppViewModel.RoutineAppStartMatch match = new(routine, 200);
        RoutineProcessSnapshot processSnapshot = new(200, @"C:\Apps\Spotify\Spotify.exe", 1);
        List<AppViewModel.RoutineAppOutputLease> activeLeases =
        [
            new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        ];
        List<RoutineProcessSnapshot> processSnapshots =
        [
            new(100, @"C:\Apps\Spotify\Spotify.exe", 1),
            new(101, @"C:\Apps\Spotify\SpotifyPlayer.exe", 100),
            processSnapshot,
        ];

        bool result = AppViewModel.ShouldSkipRoutineAppStartMatchForExistingLease(match, processSnapshot, activeLeases, processSnapshots);

        Assert.False(result);
    }

    [Fact]
    public void ShouldSkipRoutineAppStartMatchForExistingLease_ReturnsFalse_WhenLeaseRootPidWasReused()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };
        AppViewModel.RoutineAppStartMatch match = new(routine, 100);
        RoutineProcessSnapshot processSnapshot = new(100, @"C:\Apps\Spotify\Spotify.exe", 1);
        List<AppViewModel.RoutineAppOutputLease> activeLeases =
        [
            new("routine-1:100", "routine-1", "Spotify", 100, @"C:\Apps\Spotify\Spotify.exe", "out-1", "Speakers")
        ];
        List<RoutineProcessSnapshot> processSnapshots =
        [
            new(100, @"C:\Apps\Discord\Discord.exe", 1),
        ];

        bool result = AppViewModel.ShouldSkipRoutineAppStartMatchForExistingLease(match, processSnapshot, activeLeases, processSnapshots);

        Assert.False(result);
    }

    [Fact]
    public void TryClaimRoutineAppStartMatch_RemovesStaleReusedPidLease_AndAllowsClaim()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();
        activeLeases["routine-1:100"] = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Spotify",
            100,
            @"C:\Apps\Discord\Discord.exe",
            "out-1",
            "Speakers");

        bool claimed = InvokeTryClaimRoutineAppStartMatch(
            viewModel,
            "routine-1",
            100,
            new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1));

        Assert.True(claimed);
        Assert.DoesNotContain("routine-1:100", activeLeases.Keys);
    }

    [Fact]
    public void TryClaimRoutineAppStartMatch_ReturnsFalse_WhenMatchingLeaseAlreadyExists()
    {
        using var harness = CreateStatefulHarness();
        AppViewModel viewModel = harness.ViewModel;
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();
        activeLeases["routine-1:100"] = new AppViewModel.RoutineAppOutputLease(
            "routine-1:100",
            "routine-1",
            "Spotify",
            100,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Speakers");

        bool claimed = InvokeTryClaimRoutineAppStartMatch(
            viewModel,
            "routine-1",
            100,
            new RoutineProcessSnapshot(100, @"C:\Apps\Spotify\Spotify.exe", 1));

        Assert.False(claimed);
        Assert.Contains("routine-1:100", activeLeases.Keys);
    }

    [Fact]
    public void ShouldSkipRoutineAppStartMatchForExistingLease_ReturnsFalse_WhenNoActiveLeasesExist()
    {
        AudioRoutine routine = new()
        {
            Id = "routine-1",
            Enabled = true,
            UsesApplicationTrigger = true,
            TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            SwitchOutputPerApp = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
        };
        AppViewModel.RoutineAppStartMatch match = new(routine, 101);
        RoutineProcessSnapshot processSnapshot = new(101, @"C:\Apps\Spotify\Spotify.exe", 100);

        bool result = AppViewModel.ShouldSkipRoutineAppStartMatchForExistingLease(match, processSnapshot, [], []);

        Assert.False(result);
    }

    [Fact]
    public void SynchronizePendingRoutineAppStartRoots_RemovesClaimsForUnwatchedRoutines()
    {
        HashSet<string> currentClaims = ["routine-1:100", "routine-2:200"];
        List<AudioRoutine> watchedRoutines =
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            }
        ];

        HashSet<string> result = AppViewModel.SynchronizePendingRoutineAppStartRoots(currentClaims, watchedRoutines);

        Assert.Single(result);
        Assert.Contains("routine-1:100", result);
        Assert.DoesNotContain("routine-2:200", result);
    }

    [Fact]
    public void GetInvalidRoutineStatefulSessionKeys_ReturnsNewestInvalidSessionsFirst()
    {
        Dictionary<string, AppViewModel.RoutineStatefulSession> activeSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application-launch:routine-a:100"] = new(
                "application-launch:routine-a:100",
                "routine-a",
                "Routine A",
                RoutineTriggerKind.Application,
                activationSequence: 1,
                restorePreviousAudioOnDeactivate: true,
                restoreSnapshot: null,
                rootProcessId: 100),
            ["steam-big-picture:routine-b"] = new(
                "steam-big-picture:routine-b",
                "routine-b",
                "Routine B",
                RoutineTriggerKind.SteamBigPicture,
                activationSequence: 3,
                restorePreviousAudioOnDeactivate: true,
                restoreSnapshot: null),
            ["application-launch:routine-c:200"] = new(
                "application-launch:routine-c:200",
                "routine-c",
                "Routine C",
                RoutineTriggerKind.Application,
                activationSequence: 2,
                restorePreviousAudioOnDeactivate: true,
                restoreSnapshot: null,
                rootProcessId: 200),
        };

        List<AudioRoutine> watchedAppStartRoutines =
        [
            new()
            {
                Id = "routine-c",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Application,
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
                OutputDeviceId = "out-1",
            }
        ];
        List<AudioRoutine> watchedSteamBigPictureRoutines = [];

        List<string> result = AppRoutineStatefulCoordinator.GetInvalidRoutineStatefulSessionKeys(
            activeSessions,
            watchedAppStartRoutines,
            watchedSteamBigPictureRoutines);

        Assert.Equal(["steam-big-picture:routine-b", "application-launch:routine-a:100"], result);

    }

    [Theory]
    [InlineData(1, 10, true)]
    [InlineData(2, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(11, 10, false)]
    public void ShouldLogEveryNthOccurrence_ReturnsExpectedValue(int occurrence, int every, bool expected)
    {
        bool result = AudioPilot.Services.Audio.AudioDeviceService.ShouldLogEveryNthOccurrence(occurrence, every);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildRoutineExecutionLogContext_IncludesStableExecutionFields()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Desk Speakers",
            TriggerKind = RoutineTriggerKind.Hotkey,
            OutputDeviceId = "out-1",
            SwitchOutputPerApp = false,
        };

        string context = AppViewModel.BuildRoutineExecutionLogContext(routine, "hotkey", showOverlay: true, applicationProcessId: null);

        Assert.Contains($"routineId={LogPrivacy.Id("routine-1")}", context);
        Assert.Contains($"routineName={LogPrivacy.Label("Desk Speakers")}", context);
        Assert.Contains("source=hotkey", context);
        Assert.Contains("triggerKind=Hotkey", context);
        Assert.Contains("showOverlay=True", context);
        Assert.Contains("applicationProcessId=none", context);
        Assert.Contains("hasOutputTarget=True", context);
        Assert.Contains("hasInputTarget=False", context);
        Assert.Contains("hasMasterVolumeTarget=False", context);
        Assert.Contains("hasMicVolumeTarget=False", context);
    }

    [Fact]
    public void BuildRoutineExecutionLogContext_RedactsApplicationProcessId()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Desk Speakers",
            TriggerKind = RoutineTriggerKind.Hotkey,
            OutputDeviceId = "out-1",
            SwitchOutputPerApp = false,
        };
        string context = AppViewModel.BuildRoutineExecutionLogContext(routine, "application-launch", showOverlay: true, applicationProcessId: 321);
        Assert.Contains("applicationProcessId=id[len=3 hash=", context, StringComparison.Ordinal);
        Assert.DoesNotContain("applicationProcessId=321", context, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRoutineExecutionResultLogContext_IncludesExecutionResultFields()
    {
        var result = new AppViewModel.RoutineExecutionResult(
            Success: true,
            OutputDeviceName: "Desk Speakers",
            InputDeviceName: null,
            AwaitingAppCompletion: true,
            AppOutputApplied: true,
            AppInputApplied: false,
            OutputSucceeded: true,
            InputSucceeded: null,
            MasterVolumeSucceeded: true,
            MicVolumeSucceeded: null,
            OutputFailureDetail: "Output switch threw InvalidOperationException.",
            InputFailureDetail: "Per-app input routing is pending until the application produces audio.");

        string context = AppViewModel.BuildRoutineExecutionResultLogContext(result);

        Assert.Contains("success=True", context);
        Assert.Contains("awaitingAppCompletion=True", context);
        Assert.Contains("appOutputApplied=True", context);
        Assert.Contains("appInputApplied=False", context);
        Assert.Contains("outputSucceeded=True", context);
        Assert.Contains("inputSucceeded=none", context);
        Assert.Contains("masterVolumeSucceeded=True", context);
        Assert.Contains("micVolumeSucceeded=none", context);
        Assert.Contains($"outputDevice={LogPrivacy.Device("Desk Speakers")}", context);
        Assert.Contains("inputDevice=none", context);
        Assert.Contains("outputFailureDetail=Output switch threw InvalidOperationException.", context);
        Assert.Contains("inputFailureDetail=Per-app input routing is pending until the application produces audio.", context);
    }

    [Fact]
    public void BuildRoutineAppOutputLeaseLogContext_IncludesLeaseIdentityAndCounts()
    {
        var lease = new AppViewModel.RoutineAppOutputLease(
            "routine-1:123",
            "routine-1",
            "Desk",
            123,
            @"C:\Apps\Spotify\Spotify.exe",
            "out-1",
            "Desk Speakers",
            "in-1",
            "Desk Mic")
        {
            CompletionOverlayShown = true,
        };
        lease.AppliedOutputProcessIds.Add(123);
        lease.AppliedInputProcessIds.Add(456);

        string context = AppViewModel.BuildRoutineAppOutputLeaseLogContext(lease);

        Assert.Contains($"leaseKey={LogPrivacy.Id("routine-1:123")}", context);
        Assert.Contains($"routineId={LogPrivacy.Id("routine-1")}", context);
        Assert.Contains($"routineName={LogPrivacy.Label("Desk")}", context);
        Assert.Contains("rootProcessId=id[len=3 hash=", context, StringComparison.Ordinal);
        Assert.DoesNotContain("rootProcessId=123", context, StringComparison.Ordinal);
        Assert.Contains("hasOutputTarget=True", context);
        Assert.Contains("hasInputTarget=True", context);
        Assert.Contains("completionOverlayShown=True", context);
        Assert.Contains("appliedOutputProcessCount=1", context);
        Assert.Contains("appliedInputProcessCount=1", context);
    }

    [Fact]
    public void BuildRoutineStatefulSessionLogContext_IncludesRestoreDecision()
    {
        var session = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-1:321",
            "routine-1",
            "Desk",
            RoutineTriggerKind.Application,
            activationSequence: 7,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: new AppViewModel.RoutineAudioRestoreSnapshot("out-1", "Desk Speakers", "in-1", "Desk Mic"),
            rootProcessId: 321);

        string context = AppViewModel.BuildRoutineStatefulSessionLogContext(session, shouldRestore: true);

        Assert.Contains($"sessionKey={LogPrivacy.Session("application-launch:routine-1:321")}", context);
        Assert.Contains($"routineId={LogPrivacy.Id("routine-1")}", context);
        Assert.Contains($"routineName={LogPrivacy.Label("Desk")}", context);
        Assert.Contains("triggerKind=Application", context);
        Assert.Contains("activationSequence=7", context);
        Assert.Contains("rootProcessId=id[len=3 hash=", context, StringComparison.Ordinal);
        Assert.DoesNotContain("rootProcessId=321", context, StringComparison.Ordinal);
        Assert.Contains("restorePreviousAudioOnDeactivate=True", context);
        Assert.Contains("shouldRestore=True", context);
        Assert.Contains("hasRestoreSnapshot=True", context);
    }

    [Fact]
    public void BuildRoutineDeviceSwitchLogContext_IncludesCorrelationAndTargetFields()
    {
        var routine = new AudioRoutine
        {
            Id = "routine-1",
            Name = "Desk",
            OutputDeviceId = "out-1",
            OutputDeviceName = "Desk Speakers",
            SwitchOutputPerApp = true,
        };

        string context = AppViewModel.BuildRoutineDeviceSwitchLogContext(
            routine,
            flow: "output",
            opId: "routine-output:0123456789abcdef0123456789abcdef",
            applicationProcessId: 321,
            perAppRouting: true);

        Assert.Contains($"routineId={LogPrivacy.Id("routine-1")}", context);
        Assert.Contains($"routineName={LogPrivacy.Label("Desk")}", context);
        Assert.Contains("flow=output", context);
        Assert.Contains("opId=routine-output:0123456789abcdef0123456789abcdef", context);
        Assert.DoesNotContain("opId=routine-output:routine-1", context, StringComparison.Ordinal);
        Assert.Contains("applicationProcessId=id[len=3 hash=", context, StringComparison.Ordinal);
        Assert.DoesNotContain("applicationProcessId=321", context, StringComparison.Ordinal);
        Assert.Contains("perAppRouting=True", context);
        Assert.Contains($"targetDeviceId={LogPrivacy.Id("out-1")}", context);
        Assert.Contains($"targetDevice={LogPrivacy.Device("Desk Speakers")}", context);
    }

    [Fact]
    public void BuildRoutineRestoreSnapshotLogContext_IncludesHashedSnapshotFields()
    {
        var snapshot = new AppViewModel.RoutineAudioRestoreSnapshot(
            "out-1",
            "Desk Speakers",
            "in-1",
            "Desk Mic");

        string context = AppViewModel.BuildRoutineRestoreSnapshotLogContext(snapshot);

        Assert.Contains("hasOutputSnapshot=True", context);
        Assert.Contains($"outputDeviceId={LogPrivacy.Id("out-1")}", context);
        Assert.Contains($"outputDevice={LogPrivacy.Device("Desk Speakers")}", context);
        Assert.Contains("hasInputSnapshot=True", context);
        Assert.Contains($"inputDeviceId={LogPrivacy.Id("in-1")}", context);
        Assert.Contains($"inputDevice={LogPrivacy.Device("Desk Mic")}", context);
    }

    [Fact]
    public void LogRoutineAppStartMatchSkipped_IncludesCorrelatedOperationId()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppViewModelRoutineAppStartTests), "routine-app-start-skip.log", LogLevel.Info);
        using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(Dispatcher.CurrentDispatcher, loggerScope.Logger);
        var match = new AppViewModel.RoutineAppStartMatch(
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Desk Speakers",
                UsesApplicationTrigger = true,
                TriggerAppPath = @"C:\Apps\Spotify\Spotify.exe",
            },
            321);

        harness.ViewModel.LogRoutineAppStartMatchSkippedForTests(match, "claim-unavailable", "app-start-routine:321:abc");

        string logText = loggerScope.DisposeAndReadLogText();

        Assert.Contains("routine-application-trigger-match-skipped", logText, StringComparison.Ordinal);
        Assert.Contains("opId=app-start-routine:321:abc", logText, StringComparison.Ordinal);
        Assert.Contains("processId=id[len=3 hash=", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("processId=321", logText, StringComparison.Ordinal);
        Assert.Contains("applicationProcessId=id[len=3 hash=", logText, StringComparison.Ordinal);
        Assert.Contains($"routineId={LogPrivacy.Id("routine-1")}", logText, StringComparison.Ordinal);
        Assert.Contains($"routineName={LogPrivacy.Label("Desk")}", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(AppConstants.Audio.LogEvents.ViewModel.App.RoutineApplicationTriggerBatch, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyRoutineAppOutputLeasesAsync_WhenLiveLeasesAreAlreadyComplete_DoesNotAcquireSessionSnapshots()
    {
        using var harness = AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(
            Dispatcher.CurrentDispatcher,
            audioSessionServiceFactory: static _ => new AudioSessionService(new ThrowingAudioDeviceEnumerator()));
        AppViewModel viewModel = harness.ViewModel;
        string currentExecutablePath = Environment.ProcessPath!;
        int currentProcessId = Environment.ProcessId;
        Dictionary<string, AppViewModel.RoutineAppOutputLease> activeLeases = viewModel.GetActiveRoutineAppOutputLeasesForTests();

        var lease = new AppViewModel.RoutineAppOutputLease(
            $"routine-1:{currentProcessId}",
            "routine-1",
            "Current Process",
            currentProcessId,
            currentExecutablePath,
            "out-1",
            "Speakers");
        lease.AppliedOutputProcessIds.Add((uint)currentProcessId);
        activeLeases[lease.LeaseKey] = lease;

        viewModel.SetAppStartTriggeredRoutinesForTests(
        [
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "Current Process",
                Enabled = true,
                UsesApplicationTrigger = true,
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = currentExecutablePath,
                SwitchOutputPerApp = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
            }
        ]);

        await viewModel.ApplyRoutineAppOutputLeasesForTestsAsync("app-start-lease-refresh:test", 1, CancellationToken.None);

        AppViewModel.RoutineAppOutputLease refreshedLease = Assert.Single(activeLeases.Values);
        Assert.Equal(lease.LeaseKey, refreshedLease.LeaseKey);
        Assert.Equal([(uint)currentProcessId], [.. refreshedLease.AppliedOutputProcessIds]);
    }

    private static AppViewModelHarnessBuilder.RoutineStatefulHarness CreateStatefulHarness()
    {
        return AppViewModelHarnessBuilder.CreateRoutineStatefulHarness(Dispatcher.CurrentDispatcher);
    }

    private static Dictionary<string, AppViewModel.RoutineStatefulSession> GetActiveRoutineStatefulSessions(AppViewModel viewModel)
    {
        return viewModel.GetActiveRoutineStatefulSessionsForTests();
    }

    private static async Task InvokeDeactivateRoutineStatefulSessionAsync(AppViewModel viewModel, string sessionKey)
    {
        await viewModel.DeactivateRoutineStatefulSessionForTestsAsync(sessionKey);
    }

    private static async Task InvokeApplyRoutineAppOutputLeasesAsync(
        AppViewModel viewModel,
        string operationId,
        int coalescedSignals,
        CancellationToken cancellationToken)
    {
        await viewModel.ApplyRoutineAppOutputLeasesForTestsAsync(operationId, coalescedSignals, cancellationToken);
    }

    private static bool InvokeTryClaimRoutineAppStartMatch(
        AppViewModel viewModel,
        string routineId,
        int processId,
        RoutineProcessSnapshot processSnapshot)
    {
        return viewModel.TryClaimRoutineAppStartMatchForTests(routineId, processId, processSnapshot);
    }

    private sealed class ThrowingAudioDeviceEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new InvalidOperationException("Session snapshot acquisition should not be reached.");

        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new InvalidOperationException("Session snapshot acquisition should not be reached.");

        public MMDevice GetDefaultPlaybackDevice() => throw new InvalidOperationException("Session snapshot acquisition should not be reached.");

        public MMDevice? GetDefaultRecordingDevice() => throw new InvalidOperationException("Session snapshot acquisition should not be reached.");

        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new InvalidOperationException("Session snapshot acquisition should not be reached.");

        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new InvalidOperationException("Session snapshot acquisition should not be reached.");
    }

}
