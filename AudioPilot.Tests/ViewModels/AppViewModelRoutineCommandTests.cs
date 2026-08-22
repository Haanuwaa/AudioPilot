using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("AppDialogServiceIsolation")]
public sealed class AppViewModelRoutineCommandTests : IDisposable
{
    private readonly TestSettingsWorkspace _workspace = new(nameof(AppViewModelRoutineCommandTests));

    [Fact]
    public void DuplicateRoutineCommand_InsertsDisabledCopyAfterSelectedRoutine()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = true,
                OutputDeviceId = "out-1",
                OutputDeviceName = "Speakers",
                Hotkey = "Ctrl+Alt+D",
                TriggerKind = RoutineTriggerKind.Application,
                TriggerAppPath = @"C:\Apps\Discord\Discord.exe",
                RouteToApplication = true,
                ShowInTrayMenu = true,
            });
            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-2",
                Name = "Headset",
                Enabled = true,
            });
            viewModel.SelectedRoutineIndex = 0;
            viewModel.SelectedRoutines.Add(viewModel.Routines[0]);

            viewModel.DuplicateRoutineCommand.Execute(null);

            Assert.Equal(3, viewModel.Routines.Count);
            AudioRoutine duplicate = viewModel.Routines[1];
            Assert.Equal(1, viewModel.SelectedRoutineIndex);
            Assert.Equal("Desk (Copy)", duplicate.Name);
            Assert.NotEqual("routine-1", duplicate.Id);
            Assert.False(duplicate.Enabled);
            Assert.Equal(string.Empty, duplicate.Hotkey);
            Assert.Equal("out-1", duplicate.OutputDeviceId);
            Assert.Equal(@"C:\Apps\Discord\Discord.exe", duplicate.TriggerAppPath);
            Assert.True(duplicate.RouteToApplication);
            Assert.True(duplicate.ShowInTrayMenu);
            Assert.Equal(2, duplicate.DisplayOrder);
            Assert.Equal(3, viewModel.Routines[2].DisplayOrder);
            Assert.True(viewModel.HasUnsavedRoutineChanges);
        });
    }

    [Fact]
    public void DuplicateRoutineCommand_UsesIncrementedDuplicateSuffix_WhenPreferredNameAlreadyExists()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.Routines.Add(new AudioRoutine { Id = "routine-1", Name = "Desk" });
            viewModel.Routines.Add(new AudioRoutine { Id = "routine-2", Name = "Desk (Copy)" });
            viewModel.SelectedRoutineIndex = 0;

            viewModel.DuplicateRoutineCommand.Execute(null);

            Assert.Equal("Desk (Copy 2)", viewModel.Routines[1].Name);
        });
    }

    [Fact]
    public void DuplicateRoutineCommand_TruncatesSourceName_ToPreserveDuplicateSuffixWithinLimit()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-1",
                Name = "VeryLongRoutineNameIndeed",
            });
            viewModel.SelectedRoutineIndex = 0;

            viewModel.DuplicateRoutineCommand.Execute(null);

            string duplicateName = viewModel.Routines[1].Name;
            Assert.Equal("VeryLongRoutineNameIndeed (Copy)", duplicateName);
        });
    }

    [Fact]
    public void DuplicateRoutineCommand_TruncatesFurther_WhenIncrementedSuffixIsNeeded()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;

            viewModel.Routines.Add(new AudioRoutine { Id = "routine-1", Name = "VeryLongRoutineNameIndeed" });
            viewModel.Routines.Add(new AudioRoutine { Id = "routine-2", Name = "VeryLongRoutineNameIndeed (Copy)" });
            viewModel.SelectedRoutineIndex = 0;

            viewModel.DuplicateRoutineCommand.Execute(null);

            string duplicateName = viewModel.Routines[1].Name;
            Assert.Equal("VeryLongRoutineNameIndeed (Copy 2)", duplicateName);
        });
    }

    [Fact]
    public void CopyRoutineCommand_WritesStructuredSummaryToClipboardWriter()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;
            viewModel.Routines.Add(new AudioRoutine
            {
                Id = "routine-1",
                Name = "Desk",
                Enabled = false,
                OutputDeviceId = "out",
                OutputDeviceName = "Speakers",
                InputDeviceId = "in",
                InputDeviceName = "USB Mic",
                CommunicationsOutput = new() { Id = "calls", Name = "Headset" },
                OutputMuteAction = RoutineMuteAction.Mute,
                MasterVolumePercent = 35,
                Conditions = new() { ConnectedNetwork = "Home" },
                Hotkey = "Ctrl+Alt+D",
                ShowInTrayMenu = true,
            });
            viewModel.SelectedRoutineIndex = 0;
            viewModel.SelectedRoutines.Add(viewModel.Routines[0]);

            string? copiedText = null;
            Func<string, bool> originalWriter = AppViewModel.RoutineClipboardTextWriter;
            AppViewModel.RoutineClipboardTextWriter = text =>
            {
                copiedText = text;
                return true;
            };

            try
            {
                viewModel.CopyRoutineCommand.Execute(null);
                TestPrivateAccess.RunTaskOnDispatcher(
                    Assert.IsType<RelayCommand>(viewModel.CopyRoutineCommand).LastExecutionTaskForTests);
            }
            finally
            {
                AppViewModel.RoutineClipboardTextWriter = originalWriter;
            }

            Assert.NotNull(copiedText);
            Assert.Contains("Routine: Desk", copiedText, StringComparison.Ordinal);
            Assert.Contains("Status: Disabled", copiedText, StringComparison.Ordinal);
            Assert.Contains("Output: Speakers", copiedText, StringComparison.Ordinal);
            Assert.Contains("Input: USB Mic", copiedText, StringComparison.Ordinal);
            Assert.Contains("Triggers: Hotkey: Ctrl+Alt+D | Tray menu", copiedText, StringComparison.Ordinal);
            Assert.Contains("Communications output: Headset", copiedText, StringComparison.Ordinal);
            Assert.Contains("Output: Mute", copiedText, StringComparison.Ordinal);
            Assert.Contains("Master: 35%", copiedText, StringComparison.Ordinal);
            Assert.Contains("Conditions:", copiedText, StringComparison.Ordinal);
            Assert.Contains("Home", copiedText, StringComparison.Ordinal);
            Assert.DoesNotContain("Timing:", copiedText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CopyRoutineCommand_ShowsError_WhenClipboardWriterFails()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;
            viewModel.Routines.Add(new AudioRoutine { Id = "routine-1", Name = "Desk" });
            viewModel.SelectedRoutineIndex = 0;
            viewModel.SelectedRoutines.Add(viewModel.Routines[0]);

            Func<string, bool> originalWriter = AppViewModel.RoutineClipboardTextWriter;
            AppViewModel.RoutineClipboardTextWriter = static _ => false;

            try
            {
                viewModel.CopyRoutineCommand.Execute(null);
                TestPrivateAccess.RunTaskOnDispatcher(
                    Assert.IsType<RelayCommand>(viewModel.CopyRoutineCommand).LastExecutionTaskForTests);
            }
            finally
            {
                AppViewModel.RoutineClipboardTextWriter = originalWriter;
            }

            Assert.Contains(
                harness.Messages.ErrorMessages,
                entry => entry.message.Contains("clipboard", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void RunSelectedRoutineCommand_RequiresOneEnabledSelection()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher);
            AppViewModel viewModel = harness.ViewModel;
            var routine = new AudioRoutine { Id = "first", Name = "First", MasterVolumePercent = 35 };
            viewModel.Routines.Add(routine);
            var command = Assert.IsType<RelayCommand>(viewModel.RunSelectedRoutineCommand);
            Assert.False(command.CanExecute(null));
            viewModel.SelectedRoutineIndex = 0;
            viewModel.SelectedRoutines.Add(routine);
            Assert.True(command.CanExecute(null));
            int notifications = 0;
            command.CanExecuteChanged += (_, _) => notifications++;
            routine.Enabled = false;
            Assert.False(command.CanExecute(null));
            Assert.True(notifications > 0);
            routine.Enabled = true;
            var other = new AudioRoutine { Id = "second", Name = "Second", MasterVolumePercent = 50 };
            viewModel.Routines.Add(other);
            viewModel.SelectedRoutines.Add(other);
            Assert.False(command.CanExecute(null));
            viewModel.SelectedRoutines.Remove(other);
            Assert.True(command.CanExecute(null));
            viewModel.Cleanup();
            Assert.False(command.CanExecute(null));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunSelectedRoutineCommand_UsesDraftWithoutSaving_AndRespectsConditions(bool deviceAvailable)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(_workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
            AppViewModel viewModel = harness.ViewModel;
            var saved = new AudioRoutine
            {
                Id = "draft",
                Name = "Draft",
                MasterVolumePercent = 35,
                Conditions = new() { Device = new() { Id = "required", Playback = false } }
            };
            harness.SetCachedSettings(new Settings { Routines = new RoutinesSettings { Items = [saved] } });
            var draft = saved.Clone();
            viewModel.Routines.Add(draft);
            draft.MasterVolumePercent = 55;
            viewModel.SelectedRoutineIndex = 0;
            viewModel.SelectedRoutines.Add(draft);
            List<int> volumes = [];
            viewModel.RoutineExecutionOperationsForTests = new(
                _ => deviceAvailable ? [new() { Id = "required" }] : [], (_, target) => target,
                (_, _, _, _, _) => throw new InvalidOperationException("Unexpected reconnect."),
                (_, _, _, _, _) => Task.CompletedTask,
                _ => throw new InvalidOperationException("Unexpected device switch."),
                (_, _, _, _) => throw new InvalidOperationException("Unexpected application routing."),
                (_, _, percent, _) => { volumes.Add(percent); return true; });
            var command = Assert.IsType<RelayCommand>(viewModel.RunSelectedRoutineCommand);
            command.Execute(null);
            TestPrivateAccess.RunTaskOnDispatcher(command.LastExecutionTaskForTests);
            Assert.Equal(deviceAvailable ? [55] : Array.Empty<int>(), volumes);
            Assert.Equal(deviceAvailable ? RoutineLastRunState.Succeeded : RoutineLastRunState.Skipped, draft.LastRunState);
            Assert.Equal(35, saved.MasterVolumePercent);
            Assert.True(viewModel.HasUnsavedRoutineChanges);
            Assert.False(File.Exists(Path.Combine(_workspace.PrimaryDir, "settings.json")));
        });
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private static void EnsureApplication()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
    }
}
