using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineEditorPhaseTwoTests
{
    [Fact]
    public void EnabledToggle_UpdatesRoutineAndDirtyState()
    {
        using var editor = new RoutineEditorViewModel([], [], new AudioRoutine
        {
            Name = "Desk",
            Enabled = true,
            MasterVolumePercent = 50,
        }, preloadNetworks: false);

        Assert.True(editor.Enabled);
        Assert.False(editor.HasUnsavedChanges);

        editor.Enabled = false;
        Assert.True(editor.HasUnsavedChanges);
        Assert.False(editor.BuildRoutine().Enabled);

        editor.Enabled = true;
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public void VolumeSlidersAndToggles_SyncWithPercentTextAndPreserveDecimals()
    {
        using var editor = new RoutineEditorViewModel([], [], preloadNetworks: false);
        Assert.False(editor.SetMasterVolumeEnabled);
        Assert.False(editor.SetMicVolumeEnabled);

        editor.SetMasterVolumeEnabled = true;
        Assert.Equal("50", editor.MasterVolumePercentText);
        Assert.Equal(50d, editor.MasterVolumeSliderValue);

        editor.MasterVolumeSliderValue = 73;
        Assert.Equal("73", editor.MasterVolumePercentText);

        editor.MasterVolumePercentText = "12.5";
        Assert.True(editor.SetMasterVolumeEnabled);
        Assert.Equal(12.5d, editor.MasterVolumeSliderValue);

        editor.SetMasterVolumeEnabled = false;
        Assert.Equal(string.Empty, editor.MasterVolumePercentText);
    }

    [Fact]
    public void CaptureCurrentAudioSetup_PopulatesDevicesDistinctCommsVolumesAndMute()
    {
        ObservableCollection<CycleDevice> outputs =
        [
            new() { Id = "out-desk", Name = "Desk speakers", StableId = "stable-desk" },
            new() { Id = "out-headset", Name = "USB headset", StableId = "stable-headset" },
        ];
        ObservableCollection<CycleDevice> inputs =
        [
            new() { Id = "in-mic", Name = "Desk mic", StableId = "stable-mic" },
        ];

        using var editor = new RoutineEditorViewModel(
            outputs,
            inputs,
            preloadNetworks: false,
            captureCurrentAudioSetup: () => new RoutineAudioSetupSnapshot(
                OutputDeviceId: "out-desk",
                OutputDeviceName: "Desk speakers",
                OutputDeviceStableId: "stable-desk",
                InputDeviceId: "in-mic",
                InputDeviceName: "Desk mic",
                InputDeviceStableId: "stable-mic",
                CommunicationsOutputDeviceId: "out-headset",
                CommunicationsOutputDeviceName: "USB headset",
                CommunicationsOutputDeviceStableId: "stable-headset",
                CommunicationsInputDeviceId: "in-mic",
                CommunicationsInputDeviceName: "Desk mic",
                CommunicationsInputDeviceStableId: "stable-mic",
                MasterVolumePercent: 68,
                MicVolumePercent: 85,
                OutputMuteAction: RoutineMuteAction.Unmute,
                InputMuteAction: RoutineMuteAction.Mute));

        Assert.True(editor.CanCaptureCurrentAudioSetup);
        Assert.True(editor.CaptureCurrentAudioSetup());
        Assert.True(editor.HasCaptureSetupFeedback);

        AudioRoutine routine = editor.BuildRoutine();
        Assert.Equal("out-desk", routine.OutputDeviceId);
        Assert.Equal("in-mic", routine.InputDeviceId);
        Assert.Equal("out-headset", routine.CommunicationsOutput?.Id);
        Assert.Null(routine.CommunicationsInput);
        Assert.Equal(68m, routine.MasterVolumePercent);
        Assert.Equal(85m, routine.MicVolumePercent);
        Assert.Equal(RoutineMuteAction.Unmute, routine.OutputMuteAction);
        Assert.Equal(RoutineMuteAction.Mute, routine.InputMuteAction);
        Assert.True(editor.IsCommunicationsExpanded);
        Assert.True(editor.IsVolumeTargetsExpanded);
    }

    [Fact]
    public void SwitchToApplicationRouting_ClearsCommunicationsGracefullyWithNotice()
    {
        ObservableCollection<CycleDevice> outputs = [new() { Id = "out-1", Name = "Speakers" }];
        using var editor = new RoutineEditorViewModel(outputs, [], preloadNetworks: false)
        {
            SelectedOutputIndex = 1,
            SelectedCommunicationsOutputIndex = 1,
        };

        Assert.False(editor.CanRoutePerApp);
        Assert.True(editor.SwitchToApplicationRouting());
        Assert.True(editor.RouteToApplication);
        Assert.Equal(0, editor.SelectedCommunicationsOutputIndex);
        Assert.True(editor.ClearedCommunicationsForAppRouting);

        editor.SwitchToSystemRouting();
        Assert.False(editor.RouteToApplication);
        Assert.False(editor.ClearedCommunicationsForAppRouting);
    }

    [Fact]
    public void LiveValidationAndCycleGroupPreview_ReportIssuesAndSharedMembers()
    {
        var existing = new AudioRoutine
        {
            Id = "other-1",
            Name = "Desk audio",
            Enabled = true,
            Hotkey = "Ctrl+Alt+F9",
            HotkeyCycleGroup = "Work",
            MasterVolumePercent = 50,
        };

        using var editor = new RoutineEditorViewModel(
            [],
            [],
            reservedHotkeyKeys: ["Ctrl+Alt+F1"],
            otherRoutines: [existing],
            preloadNetworks: false);

        // Missing-action warning is calm on a pristine new routine until validation is attempted.
        Assert.False(editor.HasActionsValidationIssue);
        editor.MarkValidationAttempted();
        Assert.True(editor.HasActionsValidationIssue);
        editor.MasterVolumePercentText = "50";
        Assert.False(editor.HasActionsValidationIssue);

        editor.EditorHotkey.LoadFromString("Ctrl+Alt+F1");
        Assert.True(editor.HasHotkeyConflict);
        Assert.True(editor.HasTriggersValidationIssue);

        editor.HotkeyCycleGroup = "Work";
        Assert.False(editor.HasHotkeyConflict);
        Assert.True(editor.HasHotkeyCycleGroupMembers);
        Assert.Equal("Desk audio", Assert.Single(editor.HotkeyCycleGroupMembers));
        Assert.Contains("Desk audio", editor.HotkeyCycleGroupPreviewPresentation(AudioPilot.Localization.LocalizationService.English), StringComparison.Ordinal);
    }

    [Fact]
    public Task ResponsiveLayoutInlineTriggerEditingAndReadinessRail_WorkInWindow() =>
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
            RoutineEditorWindow? window = null;
            using var model = new RoutineEditorViewModel([], [], new AudioRoutine { Name = "Desk", MasterVolumePercent = 50 }, preloadNetworks: false);
            try
            {
                application.Resources = resources;
                window = new RoutineEditorWindow(model, checkRoutine: (_, _) => Task.FromResult(new RoutineReadinessResult(
                    DateTimeOffset.UtcNow,
                    true,
                    "not-required",
                    [new RoutineReadinessItem("enabled", "ready", "routine-enabled", "The routine is enabled.")])));
                window.UpdateResponsiveLayout(880);
                Assert.Equal(Visibility.Visible, window.PreviewRailBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, window.RoutineSummaryText.Visibility);

                window.UpdateResponsiveLayout(520);
                Assert.Equal(Visibility.Collapsed, window.PreviewRailBorder.Visibility);
                Assert.Equal(Visibility.Visible, window.RoutineSummaryText.Visibility);

                window.UpdateResponsiveLayout(880);
                window.NewTriggerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(model.IsEditingAutomaticTrigger);
                Assert.Equal(Visibility.Visible, window.InlineTriggerEditorCard.Visibility);

                model.ActiveTriggerDraft!.SelectedTriggerMode = "Device change";
                window.InlineTriggerEditorPanel.ConfirmTriggerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(model.IsEditingAutomaticTrigger);
                Assert.Equal(Visibility.Collapsed, window.InlineTriggerEditorCard.Visibility);
                Assert.Single(model.TriggerEntries);

                window.CheckRoutineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await TestExecutionGuards.WaitUntilAsync(
                    () => window.RailReadinessCard.Visibility == Visibility.Visible && window.RailReadinessChecksList.ItemsSource != null,
                    "Readiness rail did not populate",
                    TimeSpan.FromSeconds(5));

                model.RequireDevice = true;
                var issue = Assert.Single(model.ValidationIssues, static i => i.FieldKey == "RequiredDevice");
                window.NavigateToValidationIssue(issue);
                Assert.Equal(RoutineEditorSection.Conditions, model.SelectedSection);
                Assert.True(window.ConditionsExpander.IsExpanded);
            }
            finally
            {
                window?.Close();
                application.Resources = originalResources;
            }
        });
}
