using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineDelayAndAppAudioTests
{
    [Fact]
    public void TriggerDelaySeconds_ClampsSummarizesFingerprintsAndRoundTrips()
    {
        var routine = new AudioRoutine
        {
            Name = "Delayed headset",
            MasterVolumePercent = 60,
            TriggerKind = RoutineTriggerKind.SteamBigPicture,
            TriggerDelaySeconds = 120,
        };

        Assert.Equal(AudioRoutine.MaxTriggerDelaySeconds, routine.TriggerDelaySeconds);
        Assert.True(routine.HasTriggerDelay);
        Assert.Contains("Delay 60s", routine.TriggerSummary, StringComparison.Ordinal);
        Assert.Contains("Automatic trigger delay: 60s", routine.RoutineDetailsOptionsSummary, StringComparison.Ordinal);

        string fingerprint60 = AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(routine);
        AudioRoutine cloned = routine.Clone();
        Assert.Equal(60, cloned.TriggerDelaySeconds);

        cloned.TriggerDelaySeconds = -5;
        Assert.Equal(0, cloned.TriggerDelaySeconds);
        Assert.False(cloned.HasTriggerDelay);
        Assert.NotEqual(fingerprint60, AppRoutineStatefulCoordinator.CreateRoutineConfigurationFingerprint(cloned));

        using var editor = new RoutineEditorViewModel([], [], routine, preloadNetworks: false);
        Assert.Equal("60", editor.TriggerDelaySecondsText);
        Assert.Equal(60, editor.TriggerDelaySeconds);
        Assert.Null(editor.Validate());

        editor.TriggerDelaySecondsText = "61";
        Assert.NotNull(editor.Validate());
        Assert.Contains(editor.ValidationIssues, static issue => issue.FieldKey == "TriggerDelay" && issue.Section == RoutineEditorSection.Triggers);

        editor.TriggerDelaySeconds = 5;
        Assert.Null(editor.Validate());
        Assert.Equal(5, editor.BuildRoutine().TriggerDelaySeconds);

        // Removing all automatic triggers clears validation trap even if invalid delay text remains.
        editor.TriggerDelaySecondsText = "999";
        Assert.NotNull(editor.Validate());
        editor.RemoveAutomaticTrigger(Assert.Single(editor.TriggerEntries));
        Assert.False(editor.HasAutomaticTriggers);
        Assert.Null(editor.Validate());
        Assert.Equal(0, editor.BuildRoutine().TriggerDelaySeconds);
    }

    [Fact]
    public void SettingsNormalizationValidationAndTransfer_PreserveAllNewRoutineProperties()
    {
        var inputMonitorOnly = new AudioRoutine
        {
            Id = "monitor-only",
            Name = "Toggle monitor",
            InputMonitoringAction = RoutineInputMonitoringAction.Enable,
            TriggerKind = RoutineTriggerKind.SteamBigPicture,
            TriggerDelaySeconds = 4,
            RestorePreviousAudioOnDeactivate = true,
        };
        var appAudioOnly = new AudioRoutine
        {
            Id = "app-audio-only",
            Name = "Duck Spotify",
            RouteToApplication = true,
            TargetAppPath = @"C:\Apps\Spotify.exe",
            AppVolumePercent = 25m,
            AppMuteAction = RoutineMuteAction.Unmute,
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Games\game.exe",
            TriggerDelaySeconds = 3,
            RestorePreviousAudioOnDeactivate = true,
        };

        var settings = new Settings();
        settings.Routines.Items = [inputMonitorOnly, appAudioOnly];

        SettingsValidationService.Normalize(settings);
        SettingsDiagnosticsResult diagnostics = SettingsValidationService.EvaluateDiagnostics(
            settings,
            activeOutputDevices: null,
            activeInputDevices: null,
            includeDeviceAvailability: false);
        Assert.DoesNotContain(diagnostics.Warnings, static w => w.Code.StartsWith("invalid-routine-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, settings.Routines.Items.Count);

        AudioRoutine normMonitor = settings.Routines.Items[0];
        Assert.Equal(RoutineInputMonitoringAction.Enable, normMonitor.InputMonitoringAction);
        Assert.Equal(4, normMonitor.TriggerDelaySeconds);

        AudioRoutine normApp = settings.Routines.Items[1];
        Assert.Equal(25m, normApp.AppVolumePercent);
        Assert.Equal(RoutineMuteAction.Unmute, normApp.AppMuteAction);
        Assert.Equal(3, normApp.TriggerDelaySeconds);

        string exportedJson = JsonSerializer.Serialize(settings.Routines.Items, SettingsJson.Options);
        List<AudioRoutine> imported = RoutineTransferService.ParseRoutineCollection(exportedJson);
        Assert.Equal(2, imported.Count);
        Assert.Equal(RoutineInputMonitoringAction.Enable, imported[0].InputMonitoringAction);
        Assert.Equal(4, imported[0].TriggerDelaySeconds);
        Assert.Equal(25m, imported[1].AppVolumePercent);
        Assert.Equal(RoutineMuteAction.Unmute, imported[1].AppMuteAction);
        Assert.Equal(3, imported[1].TriggerDelaySeconds);
    }

    [Fact]
    public void MarkLeaseProcessApplied_KeepsOutputSessionMonitoringActiveWhileAppAudioRemainsPending()
    {
        var lifetime = new RoutineLifetimeService();
        var routine = new AudioRoutine
        {
            Id = "route-and-duck",
            Name = "Headset + Duck",
            RouteToApplication = true,
            TargetAppPath = @"C:\Apps\Spotify.exe",
            OutputDeviceId = "headset-out",
            OutputDeviceName = "Headset",
            AppVolumePercent = 30m,
        };

        RoutineAppOutputLease lease = lifetime.RegisterLease(
            routine,
            processId: 600,
            outputApplied: false,
            inputApplied: false,
            overlayShown: false,
            appAudioApplied: false,
            cancellationToken: TestContext.Current.CancellationToken)!.Value.Lease;

        Assert.Equal((1, 0), lifetime.PendingLeaseCounts);

        // Output routing completes first; app audio is still pending, so _pendingOutput must stay 1.
        lifetime.MarkLeaseProcessApplied(lease, 600, output: true);
        Assert.Equal((1, 0), lifetime.PendingLeaseCounts);

        // Once app audio also completes, _pendingOutput drops to 0.
        lifetime.MarkLeaseAppAudioApplied(lease, 600);
        Assert.Equal((0, 0), lifetime.PendingLeaseCounts);
    }

    [Fact]
    public void EditorDirtyTrackingAndTimeIndexBounds_HandleEdgeCasesSafely()
    {
        var existing = new AudioRoutine
        {
            Name = "Desk",
            MasterVolumePercent = 50m,
        };

        using var editor = new RoutineEditorViewModel([], [], existing, preloadNetworks: false);
        Assert.False(editor.HasUnsavedChanges);

        editor.ShowInTrayMenu = true;
        Assert.True(editor.HasUnsavedChanges);
        editor.ShowInTrayMenu = false;
        Assert.False(editor.HasUnsavedChanges);

        editor.PlaySoundAfterApplying = true;
        Assert.True(editor.HasUnsavedChanges);
        editor.PlaySoundAfterApplying = false;
        Assert.False(editor.HasUnsavedChanges);

        editor.MasterVolumePercentText = "999";
        Assert.True(editor.HasUnsavedChanges);
        editor.MasterVolumePercentText = "50";
        Assert.False(editor.HasUnsavedChanges);

        // Out-of-range ComboBox resets (-1 or above max) during 12h <-> 24h switches must not throw.
        editor.AddAutomaticTrigger();
        RoutineTriggerDraftViewModel draft = editor.ActiveTriggerDraft!;
        draft.SelectedTriggerMode = "Scheduled";
        int originalHour = draft.ScheduleHourIndex;
        draft.ScheduleHourIndex = -1;
        draft.ScheduleHourIndex = 99;
        draft.ScheduleMinuteIndex = -1;
        draft.ScheduleMinuteIndex = 99;
        draft.ScheduleAmPmIndex = -1;
        draft.ScheduleAmPmIndex = 5;
        Assert.Equal(originalHour, draft.ScheduleHourIndex);
        editor.CancelTriggerEdit();
    }

    [Fact]
    public void RestoreToSessions_DoesNotOverwriteSiblingSessionWhenExpiredSessionRestores()
    {
        var expiredSession = new FakeAppAudioSession(77, "inst-1", volume: 0.2f, mute: false);
        var siblingSession = new FakeAppAudioSession(77, "inst-2", volume: 0.9f, mute: false);

        RoutineAppAudioApplicationResult applied = RoutineAppSessionAudioService.ApplyToSessions(
            () => [expiredSession, siblingSession],
            Logger.Instance,
            volumePercent: 50m,
            muteAction: RoutineMuteAction.Unchanged,
            captureRestore: true,
            operationId: "sibling-test");

        Assert.True(applied.Applied);
        expiredSession.IsExpired = true;

        applied.Change!.Restore();
        Assert.InRange(siblingSession.Volume, 0.899f, 0.901f);
    }

    [Fact]
    public async Task RoutineLifetimeService_DelaysAutomaticTriggers_SkipsManual_AndCancelsOnDeactivate()
    {
        using var clock = new ManualTimeProvider();
        var lifetime = new RoutineLifetimeService(clock);
        var routine = new AudioRoutine
        {
            Id = "delayed-game",
            Name = "Game",
            MasterVolumePercent = 50,
            TriggerKind = RoutineTriggerKind.Application,
            ApplicationTriggerMode = ApplicationTriggerMode.AppLaunch,
            TriggerAppPath = @"C:\Games\game.exe",
            TriggerDelaySeconds = 5,
        };

        // Manual run bypasses trigger delay completely.
        bool manualRan = false;
        await lifetime.RunActivationAsync(
            routine,
            100,
            _ =>
            {
                manualRan = true;
                return Task.FromResult(true);
            },
            isManual: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(manualRan);

        // Automatic run waits for the configured delay before executing.
        bool automaticRan = false;
        Task<bool> delayedTask = lifetime.RunActivationAsync(
            routine,
            200,
            _ =>
            {
                automaticRan = true;
                return Task.FromResult(true);
            },
            isManual: false,
            cancellationToken: TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Yield();
        Assert.False(automaticRan);
        Assert.False(delayedTask.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await delayedTask);
        Assert.True(automaticRan);

        // Process exit during the delay cancels cleanly without executing.
        bool cancelledRunExecuted = false;
        Task<bool> cancelledTask = lifetime.RunActivationAsync(
            routine,
            300,
            _ =>
            {
                cancelledRunExecuted = true;
                return Task.FromResult(true);
            },
            isManual: false,
            cancellationToken: TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(2));
        lifetime.CancelProcess(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
        Assert.False(cancelledRunExecuted);
    }

    [Fact]
    public void RoutineAppSessionAudioService_AppliesRestoresAndSkipsExternallyModifiedSessions()
    {
        var session = new FakeAppAudioSession(42, "session-42", volume: 0.9f, mute: false);
        RoutineAppAudioApplicationResult result = RoutineAppSessionAudioService.ApplyToSessions(
            () => [session],
            Logger.Instance,
            volumePercent: 30m,
            muteAction: RoutineMuteAction.Mute,
            captureRestore: true,
            operationId: "test-apply");

        Assert.True(result.Success);
        Assert.True(result.Applied);
        Assert.False(result.DeferredNoAudio);
        Assert.True(result.Changed);
        Assert.InRange(session.Volume, 0.299f, 0.301f);
        Assert.True(session.Mute);
        Assert.NotNull(result.Change);

        // User manually unmutes in mixer; volume stays at routine value.
        session.Mute = false;
        result.Change!.Restore();

        // Volume restores to 0.9, but mute is respected (remains unmuted).
        Assert.InRange(session.Volume, 0.899f, 0.901f);
        Assert.False(session.Mute);

        // No active sessions => deferred continuation.
        RoutineAppAudioApplicationResult deferred = RoutineAppSessionAudioService.ApplyToSessions(
            () => [],
            Logger.Instance,
            volumePercent: 25m,
            muteAction: RoutineMuteAction.Unchanged,
            captureRestore: false,
            operationId: "test-deferred");

        Assert.True(deferred.Success);
        Assert.False(deferred.Applied);
        Assert.True(deferred.DeferredNoAudio);
    }

    [Fact]
    public async Task RoutineExecutionService_SupportsPerAppAudioOnlyAndDeferredContinuation()
    {
        bool restored = false;
        var routine = new AudioRoutine
        {
            Name = "Duck Spotify",
            RouteToApplication = true,
            TargetAppPath = @"C:\Apps\Spotify.exe",
            AppVolumePercent = 20m,
            AppMuteAction = RoutineMuteAction.Unmute,
        };

        Assert.True(routine.HasAppVolumeTarget);
        Assert.True(routine.HasAppMuteTarget);
        Assert.True(routine.HasAppAudioTarget);
        Assert.True(routine.HasExecutionTarget);
        Assert.Contains("App vol 20%", routine.TargetSummary, StringComparison.Ordinal);

        RoutineExecutionOperations immediateOps = CreateExecutionOperations(
            (_, _, vol, mute, capture, _) =>
            {
                Assert.Equal(20m, vol);
                Assert.Equal(RoutineMuteAction.Unmute, mute);
                return new RoutineAppAudioApplicationResult(
                    true,
                    Applied: true,
                    DeferredNoAudio: false,
                    Change: capture ? new CallbackAudioChange(() => restored = true) : null,
                    Changed: true);
            });

        RoutineExecutionResult immediateResult = await new RoutineExecutionService(immediateOps, Logger.Instance).ExecuteAsync(
            routine,
            new RoutineExecutionOptions(CaptureAudioRestoration: true),
            processId: 777,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(immediateResult.Success);
        Assert.True(immediateResult.AppAudioApplied);
        Assert.Equal(true, immediateResult.AppAudioSucceeded);
        Assert.False(immediateResult.AwaitingAppCompletion);
        Assert.True(immediateResult.AudioChanged);
        immediateResult.AudioRestoration!.Complete(restore: true);
        Assert.True(restored);

        RoutineExecutionOperations deferredOps = CreateExecutionOperations(
            (_, _, _, _, _, _) => new RoutineAppAudioApplicationResult(true, Applied: false, DeferredNoAudio: true));

        RoutineExecutionResult deferredResult = await new RoutineExecutionService(deferredOps, Logger.Instance).ExecuteAsync(
            routine,
            new RoutineExecutionOptions(AllowDeferredRouting: true),
            processId: 777,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(deferredResult.Success);
        Assert.True(deferredResult.AwaitingAppCompletion);
        Assert.True(deferredResult.HasPerAppRoutingContinuation);
        Assert.False(deferredResult.AppAudioApplied);
    }

    [Fact]
    public async Task AppAudioLease_TracksPendingWorkAppliesDeferredAndPlansRestoreOnDeactivate()
    {
        var lifetime = new RoutineLifetimeService();
        var routine = new AudioRoutine
        {
            Id = "spotify-duck",
            Name = "Duck Spotify",
            RouteToApplication = true,
            TargetAppPath = @"C:\Apps\Spotify.exe",
            AppVolumePercent = 25m,
            TriggerKind = RoutineTriggerKind.Application,
            TriggerAppPath = @"C:\Games\game.exe",
            RestorePreviousAudioOnDeactivate = true,
        };

        RoutineLifetimeService.LeaseRegistration? reg = lifetime.RegisterLease(
            routine,
            processId: 500,
            outputApplied: false,
            inputApplied: false,
            overlayShown: false,
            appAudioApplied: false,
            captureAppAudioRestore: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(reg);
        Assert.Equal((1, 0), lifetime.PendingLeaseCounts);
        Assert.True(AppRoutineAppStartCoordinator.HasPendingLeaseApplications(reg!.Value.Lease));

        _ = lifetime.Register(routine, 500, snapshot: null, routingLease: reg.Value.Lease, cancellationToken: TestContext.Current.CancellationToken);

        bool applied = false;
        bool overlayShown = false;
        bool completed = false;
        await AppRoutineAppStartCoordinator.ExecuteLeaseApplicationsAsync(
            lifetime.GetLeases(),
            [new RoutineProcessSnapshot(500, @"C:\Apps\Spotify.exe", null, null, null, null)],
            [],
            (_, _, _, _) => [500u],
            (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult(false),
            (_, _, _) => { overlayShown = true; return Task.CompletedTask; },
            lifetime.MarkLeaseOverlayShown,
            _ => completed = true,
            Logger.Instance,
            TestContext.Current.CancellationToken,
            tryApplyAppAudioAsync: (lease, pid) =>
            {
                applied = true;
                lifetime.MarkLeaseAppAudioApplied(lease, pid, [new RoutineAppSessionRestoreRecord(pid, "inst-1", 0.8f, 0.25f, null, null)]);
                return Task.FromResult(true);
            });

        Assert.True(applied);
        Assert.True(overlayShown);
        Assert.True(completed);
        Assert.Equal((0, 0), lifetime.PendingLeaseCounts);

        await lifetime.DeactivateAsync(Assert.Single(lifetime.CaptureDeactivations(static _ => true)), (deactivating, shouldRestore) =>
        {
            Assert.True(shouldRestore);
            var (releasedLease, _, _, restoreAppAudio) = Assert.Single(lifetime.ReleaseSessionRouting(deactivating, shouldRestore));
            Assert.True(restoreAppAudio);
            Assert.Single(releasedLease.RestorableAppAudioSnapshots);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task RoutineEditorWindow_ShowsAppAudioControlsAndDelayPresetsCleanly() =>
        SharedStaDispatcherHost.RunAsync(() =>
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
            application.Resources = resources;

            using var model = new RoutineEditorViewModel([], [], preloadNetworks: false)
            {
                Name = "Spotify Duck",
            };
            var window = new RoutineEditorWindow(model);

            try
            {
                Assert.Equal(nameof(RoutineEditorViewModel.RouteToApplication), System.Windows.Data.BindingOperations.GetBinding(window.AppAudioTargetsPanel, UIElement.VisibilityProperty)?.Path.Path);
                Assert.Equal(nameof(RoutineEditorViewModel.HasAutomaticTriggers), System.Windows.Data.BindingOperations.GetBinding(window.TriggerDelayPanel, UIElement.VisibilityProperty)?.Path.Path);
                Assert.False(model.RouteToApplication);
                Assert.False(model.HasAutomaticTriggers);

                model.SwitchToApplicationRouting();
                model.TargetAppPath = @"C:\Apps\Spotify.exe";
                model.SetAppVolumeEnabled = true;
                model.AppVolumePercentText = "35";
                model.AppMuteAction = RoutineMuteAction.Unmute;

                Assert.True(model.RouteToApplication);
                Assert.Null(model.Validate());
                Assert.Contains("App 35%", model.VolumeTargetsSummary, StringComparison.Ordinal);

                model.AddAutomaticTrigger();
                model.ActiveTriggerDraft!.SelectedTriggerMode = "Steam Big Picture";
                Assert.True(model.ConfirmTriggerEdit());
                Assert.True(model.HasAutomaticTriggers);

                window.DelayPreset5Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(5, model.TriggerDelaySeconds);

                AudioRoutine built = model.BuildRoutine();
                Assert.Equal(35m, built.AppVolumePercent);
                Assert.Equal(RoutineMuteAction.Unmute, built.AppMuteAction);
                Assert.Equal(5, built.TriggerDelaySeconds);

                // Validation jump navigates directly to AppVolume and TriggerDelay fields.
                model.AppVolumePercentText = "150";
                var appVolIssue = Assert.Single(model.ValidationIssues, static i => i.FieldKey == "AppVolume");
                window.NavigateToValidationIssue(appVolIssue);
                Assert.Equal(RoutineEditorSection.Actions, model.SelectedSection);
                Assert.True(window.VolumeTargetsExpander.IsExpanded);

                model.TriggerDelaySecondsText = "99";
                var delayIssue = Assert.Single(model.ValidationIssues, static i => i.FieldKey == "TriggerDelay");
                window.NavigateToValidationIssue(delayIssue);
                Assert.Equal(RoutineEditorSection.Triggers, model.SelectedSection);

                // Switching back to System routing clears per-app audio targets on save.
                model.SwitchToSystemRouting();
                Assert.False(model.RouteToApplication);
                AudioRoutine systemBuilt = model.BuildRoutine();
                Assert.Null(systemBuilt.AppVolumePercent);
                Assert.Equal(RoutineMuteAction.Unchanged, systemBuilt.AppMuteAction);
            }
            finally
            {
                window.Close();
                application.Resources = originalResources;
            }
            return Task.CompletedTask;
        });

    [Fact]
    public void CalmInitialValidationAndRoutineDetailsPolish_WorkAsExpected()
    {
        using var fresh = new RoutineEditorViewModel([], [], suggestedName: "Routine 1", preloadNetworks: false);
        Assert.False(fresh.HasValidationIssues);
        Assert.False(fresh.HasActionsValidationIssue);
        Assert.False(fresh.CanResetChanges);

        // Typing a routine name stays calm without flashing a missing-action error.
        fresh.Name = "Evening desk";
        Assert.False(fresh.HasValidationIssues);
        Assert.True(fresh.CanResetChanges);

        // Real mistakes (like invalid volume) still show immediately.
        fresh.MasterVolumePercentText = "150";
        Assert.True(fresh.HasActionsValidationIssue);
        fresh.MasterVolumePercentText = string.Empty;
        Assert.False(fresh.HasActionsValidationIssue);

        // Switching to "An application" stays calm until the user types an invalid path or tries to save/check/test.
        fresh.SwitchToApplicationRouting();
        Assert.False(fresh.HasValidationIssues);
        Assert.False(fresh.HasActionsValidationIssue);

        fresh.TargetAppPath = "not-a-valid-target";
        Assert.Contains(fresh.ValidationIssues, static i => i.FieldKey == "TargetApp");
        fresh.TargetAppPath = string.Empty;
        Assert.False(fresh.HasValidationIssues);

        // Inline trigger editing stays calm while on Triggers, then warns if the user switches away.
        fresh.SelectedSection = RoutineEditorSection.Triggers;
        fresh.AddAutomaticTrigger();
        Assert.False(fresh.HasTriggersValidationIssue);
        fresh.SelectedSection = RoutineEditorSection.Actions;
        Assert.Contains(fresh.ValidationIssues, static i => i.FieldKey == "TriggerEditor");
        fresh.CancelTriggerEdit();
        Assert.False(fresh.HasTriggersValidationIssue);

        // Switching to Conditions auto-expands ConditionsExpander; checking RequireDevice stays calm until leaving tab or validating.
        Assert.False(fresh.IsConditionsExpanded);
        fresh.SelectedSection = RoutineEditorSection.Conditions;
        Assert.True(fresh.IsConditionsExpanded);
        fresh.RequireDevice = true;
        Assert.False(fresh.HasConditionsValidationIssue);
        fresh.SelectedSection = RoutineEditorSection.Actions;
        Assert.Contains(fresh.ValidationIssues, static i => i.FieldKey == "RequiredDevice");
        fresh.RequireDevice = false;

        // Attempting Save / Check / Test reveals the missing per-app configuration and action warnings.
        fresh.MarkValidationAttempted();
        Assert.True(fresh.HasActionsValidationIssue);
        Assert.Contains(fresh.ValidationIssues, static i => i.FieldKey == "TargetApp");
        Assert.Contains(fresh.ValidationIssues, static i => i.FieldKey == "OutputDevice");

        // Routine details polish: Per-app audio-only vs device routing, App playback mute, Last run status, Target display.
        var appAudioOnly = new AudioRoutine
        {
            Name = "Duck Spotify",
            Enabled = false,
            RouteToApplication = true,
            TargetAppPath = @"C:\Apps\Spotify.exe",
            AppVolumePercent = 25m,
            AppMuteAction = RoutineMuteAction.Mute,
            LastRunState = RoutineLastRunState.Skipped,
            LastRunDetail = "Target application is not running.",
        };

        Assert.True(appAudioOnly.HasLastRunStatus);
        Assert.False(appAudioOnly.HasLastRunBadge);
        Assert.Contains("App playback: Mute", appAudioOnly.RoutineDetailsTargetSummary, StringComparison.Ordinal);
        Assert.Equal("Per-app audio", appAudioOnly.RoutineDetailsOptionsSummary);
        Assert.Equal("Spotify", appAudioOnly.TargetAppDisplayPresentation(AudioPilot.Localization.LocalizationService.English));

        appAudioOnly.OutputDeviceId = "out-1";
        appAudioOnly.OutputDeviceName = "Headset";
        Assert.Equal("App device routing", appAudioOnly.RoutineDetailsOptionsSummary);
    }

    [Fact]
    public void ResetToInitialState_RestoresBothNewAndExistingRoutinesCleanly()
    {
        // New routine reset restores suggested name, starter templates, and clears everything.
        using var fresh = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [new CycleDevice { Id = "in-1", Name = "Mic" }],
            suggestedName: "Routine 7",
            preloadNetworks: false);

        fresh.ApplyStarterTemplate(RoutineStarterTemplate.NightVolumeCap);
        Assert.False(fresh.ShowStarterTemplates);
        Assert.True(fresh.RequireTimeWindow);
        Assert.True(fresh.CanResetChanges);

        fresh.ResetToInitialState();
        Assert.Equal("Routine 7", fresh.Name);
        Assert.True(fresh.ShowStarterTemplates);
        Assert.False(fresh.RequireTimeWindow);
        Assert.Equal(string.Empty, fresh.MasterVolumePercentText);
        Assert.False(fresh.HasUnsavedChanges);
        Assert.False(fresh.CanResetChanges);

        // Existing routine reset restores original fields, triggers, hotkey, and conditions.
        var existing = new AudioRoutine
        {
            Id = "existing-1",
            Name = "Work calls",
            Enabled = true,
            OutputDeviceId = "out-1",
            OutputDeviceName = "Speakers",
            MasterVolumePercent = 45m,
            InputMonitoringAction = RoutineInputMonitoringAction.Disable,
            Hotkey = "Ctrl+Alt+W",
            TriggerKind = RoutineTriggerKind.SteamBigPicture,
            TriggerDelaySeconds = 4,
            Conditions = new RoutineConditions { ConnectedNetwork = "OfficeWiFi" },
        };

        using var editor = new RoutineEditorViewModel(
            [new CycleDevice { Id = "out-1", Name = "Speakers" }],
            [new CycleDevice { Id = "in-1", Name = "Mic" }],
            existing,
            preloadNetworks: false);

        Assert.False(editor.CanResetChanges);
        editor.Name = "Changed";
        editor.Enabled = false;
        editor.MasterVolumePercentText = "90";
        editor.InputMonitoringAction = RoutineInputMonitoringAction.Enable;
        editor.TriggerDelaySeconds = 10;
        editor.RequiredNetworkName = "HomeWiFi";
        editor.AddAutomaticTrigger();
        Assert.True(editor.IsEditingAutomaticTrigger);
        Assert.True(editor.CanResetChanges);

        editor.ResetToInitialState();
        Assert.False(editor.IsEditingAutomaticTrigger);
        Assert.Equal("Work calls", editor.Name);
        Assert.True(editor.Enabled);
        Assert.Equal("45", editor.MasterVolumePercentText);
        Assert.Equal(RoutineInputMonitoringAction.Disable, editor.InputMonitoringAction);
        Assert.Equal(4, editor.TriggerDelaySeconds);
        Assert.Equal("OfficeWiFi", editor.RequiredNetworkName);
        Assert.Equal("Ctrl+Alt+W", editor.EditorHotkey.ToHotkeyString());
        Assert.False(editor.HasUnsavedChanges);
        Assert.False(editor.CanResetChanges);
    }

    [Fact]
    public Task RoutineEditorWindow_DefaultHeightFitsVolumeExpanderWithoutClippingAndResetWorks() =>
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
                Source = new Uri("pack://application:,,,/AudioPilot;component/Themes/DarkTheme.xaml", UriKind.Absolute),
            });
            application.Resources = resources;

            var dialogService = new RecordingAppDialogService { YesNoResponse = AppDialogResult.Declined };
            using var model = new RoutineEditorViewModel(
                [new CycleDevice { Id = "out-1", Name = "Speakers" }],
                [new CycleDevice { Id = "in-1", Name = "Mic" }],
                suggestedName: "Routine 1",
                preloadNetworks: false);

            var window = new RoutineEditorWindow(model, dialogService: dialogService);
            try
            {
                // Approximate Windows client area for the default 880x720 window (16px horizontal chrome, 39px titlebar + bottom border).
                var content = (FrameworkElement)window.Content;
                double clientWidth = window.Width - 16;
                double clientHeight = window.Height - 39;
                content.Measure(new Size(clientWidth, clientHeight));
                content.Arrange(new Rect(0, 0, clientWidth, clientHeight));
                content.UpdateLayout();
                window.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

                // Both collapsed expanders fit comfortably inside the default 880x720 viewport (no clipped top border / double line).
                Assert.True(window.VolumeTargetsExpander.ActualHeight > 0);
                Assert.True(window.RoutineEditorScrollViewer.ViewportHeight > 0);
                Point expanderBottom = window.VolumeTargetsExpander.TranslatePoint(
                    new Point(0, window.VolumeTargetsExpander.ActualHeight),
                    window.RoutineEditorScrollViewer);
                Assert.True(
                    expanderBottom.Y <= window.RoutineEditorScrollViewer.ViewportHeight + 0.5,
                    $"Expected VolumeTargetsExpander bottom ({expanderBottom.Y:F1}) to fit inside ScrollViewer viewport ({window.RoutineEditorScrollViewer.ViewportHeight:F1}).");

                // Pristine new routine has no initial warning card and Reset is disabled.
                Assert.Equal(Visibility.Collapsed, window.ValidationIssuesCard.Visibility);
                Assert.False(window.ResetRoutineButton.IsEnabled);

                // Applying a template enables Reset; declining keeps edits; confirming restores launch state.
                model.ApplyStarterTemplate(RoutineStarterTemplate.GamingHeadset);
                Assert.True(window.ResetRoutineButton.IsEnabled);

                Assert.False(await window.ConfirmAndResetAsync());
                Assert.Equal("Gaming headset", model.Name);

                dialogService.YesNoResponse = AppDialogResult.Confirmed;
                Assert.True(await window.ConfirmAndResetAsync());
                Assert.Equal("Routine 1", model.Name);
                Assert.True(model.ShowStarterTemplates);
                Assert.False(window.ResetRoutineButton.IsEnabled);
            }
            finally
            {
                window.Close();
                application.Resources = originalResources;
            }
        });

    private static RoutineExecutionOperations CreateExecutionOperations(
        Func<uint, string, decimal?, RoutineMuteAction, bool, string, RoutineAppAudioApplicationResult> applyAppAudio)
    {
        return new RoutineExecutionOperations(
            _ => [],
            (_, _) => null,
            (_, _, _, _, _) => Task.FromResult(default(BluetoothReconnectAttemptResult)),
            (_, _, _, _, _) => Task.CompletedTask,
            _ => ValueTask.FromResult((true, (string?)null)),
            (_, _, _, _) => ValueTask.FromResult(default(ProcessAudioDeviceSwitchResult)),
            (_, _, _, _) => true)
        {
            ApplyAppAudio = applyAppAudio,
        };
    }

    private sealed class FakeAppAudioSession(uint processId, string instanceId, float volume, bool mute) : IRoutineAppAudioSession
    {
        public uint ProcessId { get; } = processId;
        public string InstanceId { get; } = instanceId;
        public bool IsExpired { get; set; }
        public float Volume { get; set; } = volume;
        public bool Mute { get; set; } = mute;
        public void Dispose() { }
    }

    private sealed class CallbackAudioChange(Action onRestore) : IRoutineAudioChange
    {
        public void Restore() => onRestore();
        public void Dispose() { }
    }
}
