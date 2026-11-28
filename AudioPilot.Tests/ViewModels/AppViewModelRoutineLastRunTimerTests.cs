using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

public sealed class AppViewModelRoutineLastRunTimerTests
{
    [Fact]
    public void HandleWindowVisibilityChanged_WhenVisibleOnRoutinesTabWithTrackedLastRun_StartsTimer()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRoutineLastRunTimerTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.ApplyRoutinesFromSettings(
            [
                new AudioRoutine { Id = "routine-1", Name = "Desk" },
            ]);
            SetRoutineLastRunState(viewModel, "routine-1", DateTimeOffset.UtcNow.AddMinutes(-2));
            viewModel.MarkStartupVisibilityResolved();
            viewModel.SelectedSettingsTabIndex = 2;

            viewModel.HandleWindowVisibilityChanged(true);

            Assert.True(GetRoutineTimer(viewModel).IsEnabled);
        });
    }

    [Fact]
    public void SelectedSettingsTabIndex_WhenLeavingRoutinesTab_StopsTimer()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRoutineLastRunTimerTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.ApplyRoutinesFromSettings(
            [
                new AudioRoutine { Id = "routine-1", Name = "Desk" },
            ]);
            SetRoutineLastRunState(viewModel, "routine-1", DateTimeOffset.UtcNow.AddMinutes(-2));
            viewModel.MarkStartupVisibilityResolved();
            viewModel.SelectedSettingsTabIndex = 2;
            viewModel.HandleWindowVisibilityChanged(true);

            viewModel.SelectedSettingsTabIndex = 3;

            Assert.False(GetRoutineTimer(viewModel).IsEnabled);
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenHidden_StopsTimer()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRoutineLastRunTimerTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.ApplyRoutinesFromSettings(
            [
                new AudioRoutine { Id = "routine-1", Name = "Desk" },
            ]);
            SetRoutineLastRunState(viewModel, "routine-1", DateTimeOffset.UtcNow.AddMinutes(-2));
            viewModel.MarkStartupVisibilityResolved();
            viewModel.SelectedSettingsTabIndex = 2;
            viewModel.HandleWindowVisibilityChanged(true);

            viewModel.HandleWindowVisibilityChanged(false);

            Assert.False(GetRoutineTimer(viewModel).IsEnabled);
        });
    }

    [Fact]
    public void HandleWindowVisibilityChanged_WhenNoTrackedLastRunExists_TimerStaysStopped()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelRoutineLastRunTimerTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.ApplyRoutinesFromSettings(
            [
                new AudioRoutine { Id = "routine-1", Name = "Desk" },
            ]);
            viewModel.MarkStartupVisibilityResolved();
            viewModel.SelectedSettingsTabIndex = 2;

            viewModel.HandleWindowVisibilityChanged(true);

            Assert.False(GetRoutineTimer(viewModel).IsEnabled);
        });
    }

    private static DispatcherTimer GetRoutineTimer(AppViewModel viewModel)
    {
        FieldInfo? field = typeof(AppViewModel).GetField("_routineLastRunRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<DispatcherTimer>(field!.GetValue(viewModel));
    }

    private static void SetRoutineLastRunState(AppViewModel viewModel, string routineId, DateTimeOffset timestamp)
    {
        MethodInfo? method = typeof(AppViewModel).GetMethod(
            "SetRoutineLastRunState",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(RoutineLastRunState), typeof(string), typeof(DateTimeOffset?)],
            modifiers: null);
        Assert.NotNull(method);

        _ = method!.Invoke(viewModel, [routineId, RoutineLastRunState.Succeeded, null, timestamp]);
    }
}
