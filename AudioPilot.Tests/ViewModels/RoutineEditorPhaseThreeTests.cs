using System.Windows;
using System.Windows.Controls;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineEditorPhaseThreeTests
{
    [Fact]
    public void InputMonitoringAction_RoundTripsValidatesSummarizesAndExecutes()
    {
        using var editor = new RoutineEditorViewModel([], [], preloadNetworks: false)
        {
            Name = "Sidetone",
            InputMonitoringAction = RoutineInputMonitoringAction.Enable,
        };

        Assert.Null(editor.Validate());
        Assert.Equal(1, editor.ConfiguredActionsCount);
        Assert.Contains("Input monitoring on", editor.VolumeTargetsSummary, StringComparison.Ordinal);

        AudioRoutine saved = editor.BuildRoutine();
        Assert.Equal(RoutineInputMonitoringAction.Enable, saved.InputMonitoringAction);
        Assert.True(saved.HasInputMonitoringTarget);
        Assert.True(saved.HasExecutionTarget);
        Assert.Equal("Monitor", saved.TargetKindBadgeText);
        Assert.Contains("Input monitoring: Enable", saved.TargetSummary, StringComparison.Ordinal);

        using var reopened = new RoutineEditorViewModel([], [], saved, preloadNetworks: false);
        Assert.Equal(RoutineInputMonitoringAction.Enable, reopened.InputMonitoringAction);
        Assert.True(reopened.IsVolumeTargetsExpanded);
    }

    [Fact]
    public async Task RoutineExecutionService_AppliesAndRestoresInputMonitoringAction()
    {
        bool? appliedState = null;
        bool restored = false;
        var ops = new RoutineExecutionOperations(
            _ => [],
            (_, _) => null,
            (_, _, _, _, _) => Task.FromResult(default(BluetoothReconnectAttemptResult)),
            (_, _, _, _, _) => Task.CompletedTask,
            _ => ValueTask.FromResult((true, (string?)null)),
            (_, _, _, _) => ValueTask.FromResult(default(ProcessAudioDeviceSwitchResult)),
            (_, _, _, _) => true)
        {
            ApplyInputMonitoring = (enable, captureRestore, _) =>
            {
                appliedState = enable;
                IRoutineAudioChange? change = captureRestore ? new CallbackAudioChange(() => restored = true) : null;
                return new RoutineInputMonitoringApplicationResult(true, change, Changed: true);
            },
        };

        var service = new RoutineExecutionService(ops, Logger.Instance);
        var routine = new AudioRoutine
        {
            Name = "Enable sidetone",
            InputMonitoringAction = RoutineInputMonitoringAction.Enable,
        };

        RoutineExecutionResult result = await service.ExecuteAsync(
            routine,
            new RoutineExecutionOptions(CaptureAudioRestoration: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(true, result.InputMonitoringSucceeded);
        Assert.True(result.AudioChanged);
        Assert.Equal(true, appliedState);
        Assert.NotNull(result.AudioRestoration);

        result.AudioRestoration!.Complete(restore: true);
        Assert.True(restored);
    }

    [Fact]
    public void StarterTemplates_ShowOnlyForNewRoutinesAndConfigureExpectedDefaults()
    {
        using var existingEditor = new RoutineEditorViewModel([], [], new AudioRoutine { Name = "Existing", MasterVolumePercent = 40 }, preloadNetworks: false);
        Assert.False(existingEditor.ShowStarterTemplates);

        using var nightEditor = new RoutineEditorViewModel([], [], preloadNetworks: false);
        Assert.True(nightEditor.ShowStarterTemplates);
        nightEditor.ApplyStarterTemplate(RoutineStarterTemplate.NightVolumeCap);
        Assert.False(nightEditor.ShowStarterTemplates);
        Assert.Equal("25", nightEditor.MasterVolumePercentText);
        Assert.True(nightEditor.RequireTimeWindow);
        Assert.Equal("22:00", nightEditor.ConditionStart);
        Assert.Equal("07:00", nightEditor.ConditionEnd);
        Assert.Equal(RoutineEditorSection.Conditions, nightEditor.SelectedSection);

        using var gamingEditor = new RoutineEditorViewModel([], [], preloadNetworks: false);
        gamingEditor.ApplyStarterTemplate(RoutineStarterTemplate.GamingHeadset);
        Assert.Equal("65", gamingEditor.MasterVolumePercentText);
        Assert.Equal("85", gamingEditor.MicVolumePercentText);
        Assert.True(gamingEditor.RestorePreviousAudioOnDeactivate);
        Assert.Equal(RoutineTriggerKind.SteamBigPicture, Assert.Single(gamingEditor.TriggerEntries).Trigger.Kind);

        using var workEditor = new RoutineEditorViewModel([], [], preloadNetworks: false);
        workEditor.ApplyStarterTemplate(RoutineStarterTemplate.WorkCalls);
        Assert.Equal(RoutineInputMonitoringAction.Disable, workEditor.InputMonitoringAction);
        Assert.True(workEditor.ShowInTrayMenu);

        using var appEditor = new RoutineEditorViewModel([], [], preloadNetworks: false);
        appEditor.ApplyStarterTemplate(RoutineStarterTemplate.PerAppRouting);
        Assert.True(appEditor.RouteToApplication);
    }

    [Fact]
    public Task TestRunButton_ExecutesDraftWithoutClosingOrSavingEditor() =>
        SharedStaDispatcherHost.RunAsync(async () =>
        {
            TestExecutionGuards.EnsureSharedWpfApplication();
            Application application = Application.Current;
            ResourceDictionary originalResources = application.Resources;
            ResourceDictionary resources = new()
            {
                ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
                ["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter(),
                ["EditableTextContextMenu"] = new ContextMenu(),
                ["AppMenuDeleteIcon"] = new object(),
            };
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Relative),
            });
            AudioRoutine? tested = null;
            RoutineEditorWindow? window = null;
            using var model = new RoutineEditorViewModel([], [], new AudioRoutine { Name = "Desk", MasterVolumePercent = 55 }, preloadNetworks: false);
            try
            {
                application.Resources = resources;
                window = new RoutineEditorWindow(
                    model,
                    testRoutine: (draft, _) =>
                    {
                        tested = draft;
                        return Task.FromResult(new RoutineExecutionResult(true, "Speakers", null, AudioChanged: true));
                    });
                Assert.Equal(Visibility.Visible, window.TestRoutineButton.Visibility);
                window.TestRoutineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await TestExecutionGuards.WaitUntilAsync(
                    () => tested != null && window.RailReadinessCard.Visibility == Visibility.Visible,
                    "Test run did not complete",
                    TimeSpan.FromSeconds(5));

                Assert.Equal("Desk", tested!.Name);
                Assert.Equal(55m, tested.MasterVolumePercent);
                Assert.Null(window.ResultRoutine);
                Assert.Equal(Visibility.Collapsed, window.ReadinessPanel.Visibility);
                Assert.Contains("Test run applied", window.RailReadinessSummaryText.Text, StringComparison.Ordinal);

                // Narrow layout moves the result into the footer panel instead of duplicating it.
                window.UpdateResponsiveLayout(500);
                Assert.Equal(Visibility.Visible, window.ReadinessPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.RailReadinessCard.Visibility);
                Assert.Contains("Test run applied", window.ReadinessText.Text, StringComparison.Ordinal);
            }
            finally
            {
                window?.Close();
                application.Resources = originalResources;
            }
        });

    private sealed class CallbackAudioChange(Action onRestore) : IRoutineAudioChange
    {
        public void Restore() => onRestore();
        public void Dispose() { }
    }
}
