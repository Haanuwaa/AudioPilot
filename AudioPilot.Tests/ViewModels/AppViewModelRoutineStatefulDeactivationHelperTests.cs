using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineStatefulDeactivationHelperTests
{
    [Fact]
    public async Task ApplyAsync_RestoresAndUpdatesMonitors_WhenRequested()
    {
        List<string> calls = [];
        var session = new AppViewModel.RoutineStatefulSession(
            "steam-big-picture:routine-steam",
            "routine-steam",
            "Steam",
            RoutineTriggerKind.SteamBigPicture,
            activationSequence: 4,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: null);

        await AppViewModelRoutineStatefulDeactivationHelper.ApplyAsync(
            session,
            shouldRestore: true,
            Logger.Instance,
            value =>
            {
                calls.Add($"restore:{value.SessionKey}");
                return Task.CompletedTask;
            },
            () => calls.Add("app-start-monitor"),
            () => calls.Add("steam-monitor"));

        Assert.Equal(
            [
                "restore:steam-big-picture:routine-steam",
                "app-start-monitor",
                "steam-monitor"
            ],
            calls);
    }

    [Fact]
    public async Task ApplyAsync_StillUpdatesMonitors_WhenRestoreThrows()
    {
        List<string> calls = [];
        var session = new AppViewModel.RoutineStatefulSession(
            "application-launch:routine-desk:100",
            "routine-desk",
            "Desk",
            RoutineTriggerKind.Application,
            activationSequence: 5,
            restorePreviousAudioOnDeactivate: true,
            restoreSnapshot: null,
            rootProcessId: 100);

        await AppViewModelRoutineStatefulDeactivationHelper.ApplyAsync(
            session,
            shouldRestore: true,
            Logger.Instance,
            _ => throw new InvalidOperationException("boom"),
            () => calls.Add("app-start-monitor"),
            () => calls.Add("steam-monitor"));

        Assert.Equal(
            [
                "app-start-monitor",
                "steam-monitor"
            ],
            calls);
    }
}
