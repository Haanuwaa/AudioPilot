using AudioPilot.Models;
using AudioPilot.Services.Routines;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineHotkeyCycleTests
{
    private static AudioRoutine Member(string id, int order, string group = "Desk") => new()
    { Id = id, Name = id, DisplayOrder = order, HotkeyCycleGroup = group, Hotkey = "Ctrl+Alt+R", MasterVolumePercent = 50, Enabled = true };

    [Fact]
    public void CommitValidation_AllowsOnlyExplicitGroupSharingAndRejectsInvalidTimeWindows()
    {
        AudioRoutine[] members = [Member("one", 1), Member("two", 2)];
        var settings = new Settings { Routines = new() { Items = [.. members] } };
        Assert.False(AppViewModel.ValidateSettingsForCommit(settings).HasBlockingIssues);
        Assert.Empty(AppViewModel.BuildDuplicateHotkeyKeySet(settings));
        members[1].HotkeyCycleGroup = "Other";
        Assert.True(AppViewModel.ValidateSettingsForCommit(settings).HasBlockingIssues);
        members[1].HotkeyCycleGroup = "Desk";
        settings.Hotkeys.App.ShowAudioStatus = "Ctrl+Alt+R";
        Assert.True(AppViewModel.ValidateSettingsForCommit(settings).HasBlockingIssues);
        settings.Hotkeys.App.ShowAudioStatus = "";
        members[1].Hotkey = "Ctrl+Alt+T";
        Assert.True(AppViewModel.ValidateSettingsForCommit(settings).HasBlockingIssues);
        members[1].Hotkey = "Ctrl+Alt+R";
        members[1].Conditions = new() { TimeWindow = new() { Start = new(9, 0), End = new(9, 0) } };
        Assert.True(AppViewModel.ValidateSettingsForCommit(settings).HasBlockingIssues);
    }

    [Fact]
    public async Task Cycling_UsesSavedOrderSkipsDisabledAndConditionsAndWraps()
    {
        var service = new RoutineHotkeyCycleService();
        AudioRoutine[] members = [Member("third", 3), Member("first", 1), Member("second", 2), Member("disabled", 4)];
        members[3].Enabled = false;
        List<string> attempted = [];
        Task<RoutineExecutionResult> Execute(AudioRoutine routine, CancellationToken _)
        {
            attempted.Add(routine.Id);
            return Task.FromResult(routine.Id == "second" ? new RoutineExecutionResult(false, null, null, Skipped: true, SkipCode: "condition") : new(true, null, null));
        }
        Assert.Equal("first", (await service.RunAsync("desk", members, Execute, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal("third", (await service.RunAsync("Desk", members, Execute, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal("first", (await service.RunAsync("Desk", members, Execute, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal(["first", "second", "third", "first"], attempted);
    }

    [Fact]
    public async Task FailureStopsCycleAndUnmetConditionsDoNotAdvanceCursor()
    {
        var service = new RoutineHotkeyCycleService();
        AudioRoutine[] members = [Member("one", 1), Member("two", 2)];
        static Task<RoutineExecutionResult> Success(AudioRoutine _, CancellationToken __) => Task.FromResult(new RoutineExecutionResult(true, null, null));
        Assert.Equal("one", (await service.RunAsync("Desk", members, Success, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal("routine-cycle-no-eligible-routines", (await service.RunAsync("Desk", members,
            (_, _) => Task.FromResult(new RoutineExecutionResult(false, null, null, Skipped: true)), TestContext.Current.CancellationToken)).Code);
        int attempts = 0;
        var failed = await service.RunAsync("Desk", members, (_, _) => { attempts++; return Task.FromResult(new RoutineExecutionResult(false, null, null)); }, TestContext.Current.CancellationToken);
        Assert.Equal("routine-cycle-failed", failed.Code);
        Assert.Equal("two", failed.Routine?.Id);
        Assert.Equal(1, attempts);
        Assert.Equal("two", (await service.RunAsync("Desk", members, Success, TestContext.Current.CancellationToken)).Routine?.Id);
    }

    [Fact]
    public async Task OverlappingPressIsDroppedCancellationReleasesGateAndGroupsKeepIndependentPositions()
    {
        var service = new RoutineHotkeyCycleService();
        AudioRoutine[] members = [Member("one", 1), Member("two", 2), Member("other", 3, "Other")];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = service.RunAsync("Desk", members, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(true, null, null);
        }, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        static Task<RoutineExecutionResult> Success(AudioRoutine _, CancellationToken __) => Task.FromResult(new RoutineExecutionResult(true, null, null));
        Assert.Equal("routine-cycle-busy", (await service.RunAsync("Desk", members, Success, TestContext.Current.CancellationToken)).Code);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("one", (await service.RunAsync("Desk", members, Success, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal("other", (await service.RunAsync("Other", members, Success, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal("two", (await service.RunAsync("Desk", members, Success, TestContext.Current.CancellationToken)).Routine?.Id);
        members[1].DisplayOrder = 0;
        Assert.Equal("one", (await service.RunAsync("Desk", members, Success, TestContext.Current.CancellationToken)).Routine?.Id);
        Assert.Equal("two", (await service.RunAsync("Desk", [members[1]], Success, TestContext.Current.CancellationToken)).Routine?.Id);
    }

    [Fact]
    public void GroupEditingSharesBindingButDoesNotHideUnrelatedOrGlobalConflicts()
    {
        AudioRoutine[] members = [Member("one", 1), Member("two", 2)];
        using var editor = new RoutineEditorViewModel([], [], otherRoutines: members) { Name = "New", MasterVolumePercentText = "45", HotkeyCycleGroup = "desk" };
        Assert.Equal("Ctrl+Alt+R", editor.EditorHotkey.ToHotkeyString());
        Assert.Null(editor.Validate());
        editor.HotkeyCycleGroup = "Unrelated";
        Assert.NotNull(editor.Validate());
        editor.HotkeyCycleGroup = "Desk";
        editor.EditorHotkey.LoadFromString("Ctrl+Alt+T");
        AudioRoutine edited = editor.BuildRoutine();
        Assert.NotNull(RoutineHotkeyGroups.Validate(edited, members));
        RoutineHotkeyGroups.UpdateMemberHotkeys(edited, members);
        Assert.All(members, member => Assert.Equal("Ctrl+Alt+T", member.Hotkey));
        Assert.Null(RoutineHotkeyGroups.Validate(edited, members));
        Assert.Single(RoutineHotkeyGroups.Representatives(members));
        members[1].HotkeyCycleGroup = "";
        Assert.Equal(2, RoutineHotkeyGroups.Representatives(members).Count());
        using var reserved = new RoutineEditorViewModel([], [], edited, reservedHotkeyKeys: ["Ctrl+Alt+T"], otherRoutines: members);
        Assert.NotNull(reserved.Validate());
        var duplicate = AppViewModel.CreateRoutineDuplicate(edited, members, () => "copy");
        Assert.Empty(duplicate.HotkeyCycleGroup);
        Assert.Empty(duplicate.Hotkey);
    }
}
