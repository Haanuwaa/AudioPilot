using System.Windows;
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
                SwitchOutputPerApp = true,
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
            Assert.True(duplicate.SwitchOutputPerApp);
            Assert.False(duplicate.ShowInTrayMenu);
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
                OutputDeviceName = "Speakers",
                InputDeviceName = "USB Mic",
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

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private static void EnsureApplication()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
    }
}
