using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class RoutineEditorWindowTests
{
    private static readonly string[] MenuIconNames = ["Undo", "Redo", "Cut", "Copy", "Paste", "Delete", "SelectAll"];
    private static readonly string[] FooterNames = ["CheckRoutineButton", "SaveRoutineButton", "RoutineSummaryText"];

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task EditableComboBoxes_KeepTheEndOfLongTextAccessible(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, () =>
        {
            var combo = new ComboBox
            {
                IsEditable = true,
                Height = 32,
                FontSize = 13,
                Text = "A deliberately long network name that still needs to remain fully editable"
            };
            var window = new Window { Content = combo };
            try
            {
                Layout(window, 240, 32);
                var input = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
                input.CaretIndex = input.Text.Length;
                input.ScrollToHorizontalOffset(10000);
                Layout(window, 240, 32);
                Rect last = input.GetRectFromCharacterIndex(input.Text.Length);
                Assert.False(last.IsEmpty);
                Assert.InRange(last.Left, 0, input.ActualWidth);
                Assert.True(input.HorizontalOffset > 0);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task RequirementsShowTheResolvedAppWithoutChangingItsIdentity(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, () =>
        {
            using var model = CreateModel();
            var window = new RoutineEditorWindow(model);
            try
            {
                TestPrivateAccess.SetField(window, "_hasQueuedPackagedAppsPreload", true);
                model.RequiredApplicationPath = "Example.Media_1234567890abc!Player";
                model.SetPackagedApplicationDisplayNames([new("Music Player", model.RequiredApplicationPath, "Example.Media_1234567890abc", "Player")]);
                Layout(window, 564, 620);
                var expander = (Expander)window.FindName("ConditionsExpander");
                expander.IsExpanded = true;
                Layout(window, 564, 620);
                Assert.True(expander.IsExpanded, $"Requirement expansion reset; view-model value={model.IsConditionsExpanded}");
                var text = (TextBlock)window.FindName("ResolvedRequiredApplicationText");
                var name = (System.Windows.Documents.Run)window.FindName("ResolvedRequiredApplicationName");
                Assert.Equal(Visibility.Visible, text.Visibility);
                Assert.True(text.ActualHeight > 0);
                Assert.Equal("Music Player", name.Text);
                Assert.Contains("Running: Music Player", model.ConditionsSummary, StringComparison.Ordinal);
                Assert.Equal(((SolidColorBrush)Application.Current.Resources["AccentTextBrush"]).Color, ((SolidColorBrush)name.Foreground).Color);
                Assert.Equal("Example.Media_1234567890abc!Player", model.BuildRoutine().Conditions.RunningAppPath);
                text.BringIntoView();
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                WpfVisualAssertions.NoBindingErrors((FrameworkElement)window.Content);
                Capture((FrameworkElement)window.Content, $"routine-requirements-{theme}.png");
                model.RequiredApplicationPath = string.Empty;
                Layout(window, 564, 620);
                Assert.Equal(Visibility.Collapsed, text.Visibility);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(AppTheme.Dark, 13)]
    [InlineData(AppTheme.Light, 13)]
    [InlineData(AppTheme.Dark, 16)]
    [InlineData(AppTheme.Light, 16)]
    public async Task SingleLineInputs_DoNotScrollVerticallyOrClipTheirText(AppTheme theme, double fontSize)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, () =>
        {
            var input = new TextBox { Height = 32, FontSize = fontSize, Text = @"C:\Apps\An example application with a deliberately long folder name\Player.exe" };
            var window = new Window { Content = input };
            try
            {
                Layout(window, 280, 32);
                var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                Assert.InRange(host.ScrollableHeight, 0, .25);
                input.ScrollToVerticalOffset(100);
                Layout(window, 280, 32);
                Assert.Equal(0, input.VerticalOffset);
                Rect character = input.GetRectFromCharacterIndex(0);
                Assert.False(character.IsEmpty);
                Assert.InRange(character.Top, 0, input.ActualHeight);
                Assert.InRange(character.Bottom, 0, input.ActualHeight);
                Assert.InRange((character.Top + character.Bottom) / 2, input.ActualHeight / 2 - 1, input.ActualHeight / 2 + 1);
                input.ScrollToHorizontalOffset(100);
                Layout(window, 280, 32);
                Assert.True(input.HorizontalOffset > 0);
                input.AcceptsReturn = true;
                input.TextWrapping = TextWrapping.Wrap;
                input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                input.Height = 80;
                input.Text = string.Join('\n', Enumerable.Repeat("Multiline diagnostics report", 12));
                Layout(window, 280, 80);
                input.ScrollToEnd();
                Layout(window, 280, 80);
                Assert.True(input.VerticalOffset > 0);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public async Task PresentationMetadataDoesNotInvalidateRoutineReadiness()
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(AppTheme.Dark, async () =>
        {
            using var model = CreateModel();
            var pending = new TaskCompletionSource<AudioPilot.Services.Routines.RoutineReadinessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new RoutineEditorWindow(model, checkRoutine: (_, _) => pending.Task);
            try
            {
                ((Button)window.FindName("CheckRoutineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await model.RefreshNetworksAsync(true);
                TestPrivateAccess.SetField(window, "_hasQueuedPackagedAppsPreload", true);
                model.AddAutomaticTrigger();
                model.CancelTriggerEdit();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.False(((Button)window.FindName("CheckRoutineButton")).IsEnabled);
                pending.SetResult(new(DateTimeOffset.UtcNow, true, "app-running-audio-not-checked",
                    [new("application-routing", "ready", "routine-target-app-running", "Routing may still wait for its audio session.")]));
                await TestExecutionGuards.WaitUntilAsync(() => !TestPrivateAccess.GetField<bool>(window, "_isCheckingRoutine"),
                    "Readiness check did not finish", TimeSpan.FromSeconds(10));
                var text = (TextBlock)window.FindName("ReadinessText");
                Assert.Contains("Ready to run now", text.Text, StringComparison.Ordinal);
                Assert.Contains("audio session", text.Text, StringComparison.Ordinal);
                await model.RefreshNetworksAsync(true);
                Assert.Contains("Ready to run now", text.Text, StringComparison.Ordinal);
                model.Name = "Edited name";
                Assert.Contains("Routine changed", text.Text, StringComparison.Ordinal);
            }
            finally { pending.TrySetResult(new(DateTimeOffset.UtcNow, true, "not-required", [])); window.Close(); }
        }));
    }

    [Fact]
    public async Task ClosingTheEditor_ReleasesReadinessAndIgnoresLateFaults()
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(AppTheme.Dark, async () =>
        {
            using var model = CreateModel();
            var pending = new TaskCompletionSource<AudioPilot.Services.Routines.RoutineReadinessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken captured = default;
            var window = new RoutineEditorWindow(model, checkRoutine: (_, token) => { captured = token; return pending.Task; });
            try
            {
                ((Button)window.FindName("CheckRoutineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Close();
                await TestExecutionGuards.WaitUntilAsync(() => !TestPrivateAccess.GetField<bool>(window, "_isCheckingRoutine"),
                    "Closing did not release the readiness check", TimeSpan.FromSeconds(10));
                Assert.True(captured.IsCancellationRequested);
                Assert.False(pending.Task.IsCompleted);
                var text = (TextBlock)window.FindName("ReadinessText");
                string before = text.Text;
                pending.SetException(new InvalidOperationException("Late check failure"));
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(before, text.Text);
            }
            finally { pending.TrySetResult(new(DateTimeOffset.UtcNow, true, "not-required", [])); window.Close(); }
        }));
    }

    [Fact]
    public async Task PackagedAppRefreshFailures_PreserveTheLatestSuccessfulInventory()
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(AppTheme.Dark, async () =>
        {
            using var model = CreateModel();
            var window = new RoutineEditorWindow(model);
            var old = new TaskCompletionSource<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> latest = [new("Music Player", "Music!App", "Music", "App")];
            try
            {
                TestPrivateAccess.SetField(window, "_packagedAppsTask", old.Task);
                TestPrivateAccess.SetField(window, "_packagedAppsTaskStartedAt", Environment.TickCount64);
                Task<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>> obsolete = TestPrivateAccess.InvokeNonPublicTask<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>(window, "LoadPackagedAppsAsync", false);
                TestPrivateAccess.SetField(window, "_packagedAppsTask", Task.FromResult(latest));
                await TestPrivateAccess.InvokeNonPublicTask(window, "LoadPackagedAppsAsync", false);
                old.SetException(new InvalidOperationException("Obsolete read failed"));
                Assert.Same(latest, await obsolete);
                Assert.Same(latest, TestPrivateAccess.GetField<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>(window, "_packagedApps"));

                var failed = new TaskCompletionSource<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>(TaskCreationOptions.RunContinuationsAsynchronously);
                TestPrivateAccess.SetField(window, "_packagedAppsTask", failed.Task);
                Task current = TestPrivateAccess.InvokeNonPublicTask(window, "LoadPackagedAppsAsync", false);
                failed.SetException(new InvalidOperationException("Current read failed"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => current);
                Assert.Same(latest, TestPrivateAccess.GetField<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>(window, "_packagedApps"));
            }
            finally { old.TrySetResult([]); window.Close(); }
        }));
    }

    [Fact]
    public async Task ClosingTheEditor_ReleasesPendingPackagedAppDiscovery()
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(AppTheme.Dark, async () =>
        {
            using var model = CreateModel();
            var window = new RoutineEditorWindow(model);
            var pending = new TaskCompletionSource<IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity>>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                TestPrivateAccess.SetField(window, "_packagedAppsTask", pending.Task);
                TestPrivateAccess.SetField(window, "_packagedAppsTaskStartedAt", Environment.TickCount64);
                Task reading = TestPrivateAccess.InvokeNonPublicTask(window, "LoadPackagedAppsAsync", false);
                window.Close();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.False(pending.Task.IsCompleted);
            }
            finally { pending.TrySetException(new InvalidOperationException("Late inventory failure")); window.Close(); }
        }));
    }

    [VisualIntegrationTheory]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task LiveEditor_UsesAnOwnedTriggerDialogAndReturnsToTheRoutine(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, () =>
        {
            using var model = CreateModel();
            RoutineEditorWindow? window = null;
            try
            {
                window = new RoutineEditorWindow(model, presentTriggerEditor: dialog =>
                    DialogWindowHelper.ShowOwnedDialog(dialog, window, () =>
                    {
                        try
                        {
                            Assert.Same(window, dialog.Owner);
                            Assert.True(((ComboBox)dialog.FindName("TriggerTypeBox")).IsKeyboardFocusWithin);
                            AssertTriggerLayout(dialog);
                        }
                        finally { dialog.DialogResult = false; }
                    }));
                window.Show();
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                ((Button)window.FindName("NewTriggerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(model.IsEditingAutomaticTrigger);
                Assert.True(window.IsVisible);
                Assert.Null(window.ResultRoutine);
            }
            finally { window?.Close(); }
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(AppTheme.Dark, 424, 442)]
    [InlineData(AppTheme.Light, 424, 442)]
    [InlineData(AppTheme.Dark, 572, 642)]
    [InlineData(AppTheme.Light, 572, 642)]
    public async Task RoutineFooter_RemainsVisibleWithLongFeedbackAndTracksCollapsedSummaries(AppTheme theme, double width, double height)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, () =>
        {
            using var model = CreateModel();
            var window = new RoutineEditorWindow(model, checkRoutine: (_, _) => throw new InvalidOperationException("Not used"));
            try
            {
                Layout(window, width, height);
                var viewer = (ScrollViewer)window.FindName("RoutineEditorScrollViewer");
                Assert.Equal(0, viewer.VerticalOffset);
                AssertFieldsWithin((FrameworkElement)viewer.Content, viewer);
                foreach (string name in FooterNames)
                {
                    var control = (FrameworkElement)window.FindName(name);
                    Assert.False(viewer.IsAncestorOf(control));
                }
                var readiness = (ScrollViewer)window.FindName("ReadinessPanel");
                readiness.Visibility = Visibility.Visible;
                ((TextBlock)window.FindName("ReadinessText")).Text = string.Join("\n", Enumerable.Repeat("A current requirement needs attention.", 30));
                Layout(window, width, height);
                Assert.InRange(readiness.ActualHeight, 1, 90);
                var content = (FrameworkElement)window.Content;
                foreach (string name in FooterNames)
                {
                    var control = (FrameworkElement)window.FindName(name);
                    Rect bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                    Assert.InRange(bounds.Top, 0, content.ActualHeight);
                    Assert.InRange(bounds.Bottom, 0, content.ActualHeight + 1);
                    Assert.InRange(bounds.Right, 0, content.ActualWidth + 1);
                }
                readiness.Visibility = Visibility.Collapsed;
                model.IsVolumeTargetsExpanded = false;
                ((Expander)window.FindName("VolumeTargetsExpander")).IsExpanded = false;
                model.MasterVolumePercentText = "35";
                Layout(window, width, height);
                var volume = (Expander)window.FindName("VolumeTargetsExpander");
                var summary = Assert.Single(Descendants(volume).OfType<TextBlock>(), text => text.Text == model.VolumeTargetsSummary);
                Assert.Equal(Visibility.Visible, summary.Visibility);
                volume.IsExpanded = true;
                Layout(window, width, height);
                Assert.Equal(Visibility.Collapsed, summary.Visibility);
                WpfVisualAssertions.NoBindingErrors(content);
                Capture(content, $"routine-editor-{theme}-{width}.png");
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(AppTheme.Dark, 360, 360)]
    [InlineData(AppTheme.Light, 360, 360)]
    [InlineData(AppTheme.Dark, 480, 540)]
    [InlineData(AppTheme.Light, 480, 540)]
    public async Task TriggerDialog_FitsEveryTriggerTypeAndCancelDiscardsTheDraft(AppTheme theme, double width, double height)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, () =>
        {
            using var model = CreateModel();
            model.AddAutomaticTrigger();
            var dialog = new RoutineTriggerEditorWindow(model, (_, _, _) => { }, (_, _, _) => Task.CompletedTask);
            try
            {
                foreach (string kind in RoutineEditorViewModel.AvailableTriggerModes)
                {
                    model.SelectedTriggerMode = kind;
                    if (kind == "Application")
                    {
                        model.TriggerAppPath = "Example.MediaSuite_1234567890abc!Player";
                        model.SetResolvedPackagedAppDisplayName("Music Player");
                    }
                    Layout(dialog, width, height);
                    AssertTriggerLayout(dialog);
                    if (kind == "Application")
                    {
                        var name = (System.Windows.Documents.Run)dialog.FindName("ResolvedAppName");
                        Assert.Equal("Music Player", name.Text);
                        Assert.Equal(((SolidColorBrush)Application.Current.Resources["AccentTextBrush"]).Color, ((SolidColorBrush)name.Foreground).Color);
                        var input = (TextBox)dialog.FindName("ApplicationTargetTextBox");
                        var buttons = (StackPanel)dialog.FindName("ApplicationPickerButtons");
                        Point bottom = input.TranslatePoint(new Point(0, input.ActualHeight), buttons);
                        Assert.InRange(-bottom.Y, 11.5, 12.5);
                    }
                    WpfVisualAssertions.NoBindingErrors((FrameworkElement)dialog.Content);
                    Capture((FrameworkElement)dialog.Content, $"routine-trigger-{theme}-{width}-{kind.Replace(' ', '-')}.png");
                }
                model.SelectedTriggerMode = "Application";
                model.TriggerAppPath = string.Empty;
                ((Button)dialog.FindName("ConfirmTriggerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(model.IsEditingAutomaticTrigger);
                Assert.NotNull(model.TriggerEditorError);
                Assert.False(dialog.IsVisible);
            }
            finally { dialog.Close(); }
            Assert.False(model.IsEditingAutomaticTrigger);
            Assert.True(model.CanSaveRoutine);
            Assert.Empty(model.TriggerEntries);
            model.AddAutomaticTrigger();
            Assert.True(model.IsEditingAutomaticTrigger);
            model.CancelTriggerEdit();
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public async Task RoutineButtons_CommitOnlyConfirmedTriggersAndCloseTheirDialogOnOwnerShutdown()
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(AppTheme.Dark, () =>
        {
            using var model = CreateModel();
            RoutineEditorWindow? window = null;
            RoutineTriggerEditorWindow? lastDialog = null;
            Action<RoutineTriggerEditorWindow> edit = _ => { };
            window = new RoutineEditorWindow(model, presentTriggerEditor: dialog => { lastDialog = dialog; edit(dialog); });
            try
            {
                var add = (Button)window.FindName("NewTriggerButton");
                edit = _ => { model.SelectedTriggerMode = "Device change"; Assert.True(model.ConfirmTriggerEdit()); };
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var first = Assert.Single(model.TriggerEntries);
                Assert.True(lastDialog!.IsClosed);
                edit = _ => { model.SelectedTriggerMode = "Scheduled"; model.ScheduleTime = new(17, 45); Assert.True(model.ConfirmTriggerEdit()); };
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, model.TriggerEntries.Count);
                Layout(window, 424, 442);
                var items = (ItemsControl)window.FindName("AutomaticTriggersItems");
                var buttons = Descendants(items).OfType<Button>().Where(button => Equals(button.Content, "Edit")).ToArray();
                edit = _ => { Assert.Equal(0, model.SelectedTriggerIndex); model.SelectedTriggerMode = "Network"; model.TriggerNetworkName = "Discarded"; };
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(first, model.TriggerEntries[0]);
                Assert.Equal(RoutineTriggerKind.DeviceChange, first.Trigger.Kind);
                edit = dialog =>
                {
                    Assert.Equal(1, model.SelectedTriggerIndex);
                    Layout(dialog, 360, 360);
                    Assert.Equal(45, Assert.Single(Descendants((FrameworkElement)dialog.Content).OfType<ComboBox>(),
                        box => System.Windows.Automation.AutomationProperties.GetName(box) == "Scheduled minute").SelectedIndex);
                    Assert.True(model.ConfirmTriggerEdit());
                };
                buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(new TimeOnly(17, 45), model.BuildRoutine().Triggers[1].Time);
                Assert.True(model.CanSaveRoutine);
                edit = dialog => { window.Close(); Assert.True(dialog.IsClosed); };
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(model.IsEditingAutomaticTrigger);
                Assert.Equal(2, model.TriggerEntries.Count);
                Assert.Null(window.ResultRoutine);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task NetworkFields_KeepIndependentSelectionsAndRecoverPermissionWarningsAcrossWindows(AppTheme theme)
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(theme, async () =>
        {
            IReadOnlyList<string> networks = ["Home", "Office"];
            bool denied = true;
            string warning = new WifiScanResult([], 5).Warning!;
            using var model = CreateModel(_ => Task.FromResult(new NetworkDiscoveryResult(networks, denied ? warning : null)));
            var parent = new RoutineEditorWindow(model);
            model.AddAutomaticTrigger();
            model.SelectedTriggerMode = "Network";
            var child = new RoutineTriggerEditorWindow(model, (_, _, _) => { }, (_, _, _) => Task.CompletedTask);
            try
            {
                Layout(parent, 424, 442); Layout(child, 360, 360);
                await model.RefreshNetworksAsync(true);
                var required = (ComboBox)parent.FindName("RequiredNetworkBox");
                var trigger = (ComboBox)child.FindName("TriggerNetworkBox");
                required.SelectedItem = "Office"; trigger.SelectedItem = "Home";
                networks = ["Guest"];
                await model.RefreshNetworksAsync(true);
                Assert.Equal("Office", required.Text); Assert.Equal("Home", trigger.Text);
                foreach (Window window in new Window[] { parent, child })
                {
                    var message = (TextBlock)window.FindName("NetworkDiscoveryWarningText");
                    Assert.Equal(warning, message.Text);
                    Assert.Equal(Visibility.Visible, message.Visibility);
                    Assert.Equal(((SolidColorBrush)Application.Current.Resources["MutedTextBrush"]).Color, ((SolidColorBrush)message.Foreground).Color);
                }
                var refresh = (Button)child.FindName("TriggerNetworkRefreshButton");
                Assert.Same(model.RefreshNetworksCommand, refresh.Command);
                refresh.ApplyTemplate(); refresh.UpdateLayout();
                Assert.False(Assert.Single(Descendants(refresh).OfType<System.Windows.Shapes.Path>(), path => path.Name == "RefreshArrow").Data.Bounds.IsEmpty);
                denied = false;
                networks = ["Home", "Office"];
                await model.RefreshNetworksAsync(true);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)child.FindName("NetworkDiscoveryWarningText")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)parent.FindName("NetworkDiscoveryWarningText")).Visibility);
                Assert.True(model.ConfirmTriggerEdit());
                Assert.Equal("Office", model.BuildRoutine().Conditions.ConnectedNetwork);
                Assert.Equal("Home", model.BuildRoutine().Triggers[0].NetworkName);
            }
            finally { child.Close(); parent.Close(); }
        }));
    }

    [Fact]
    public async Task ClosingATrigger_CancelsThePendingPickerAndPreservesTheOriginalRestorationSetting()
    {
        await SharedStaDispatcherHost.RunAsync(() => WithResources(AppTheme.Dark, async () =>
        {
            using var model = CreateModel();
            model.AddAutomaticTrigger(); model.TriggerAppPath = @"C:\Apps\Original.exe";
            Assert.True(model.ConfirmTriggerEdit());
            model.RestorePreviousAudioOnDeactivate = true;
            var original = Assert.Single(model.TriggerEntries);
            model.EditAutomaticTrigger(original);
            CancellationToken pendingToken = default;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = new RoutineTriggerEditorWindow(model, (_, _, _) => { }, async (_, _, token) =>
            {
                pendingToken = token;
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { finished.TrySetResult(); }
            });
            try
            {
                Layout(child, 360, 360);
                model.TriggerAppPath = @"C:\Apps\Discarded.exe";
                Assert.Single(Descendants((FrameworkElement)child.Content).OfType<Button>(), button => Equals(button.Content, "Pick App"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(pendingToken.CanBeCanceled);
                child.Close();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                await child.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
                Assert.True(pendingToken.IsCancellationRequested);
                Assert.False(model.IsEditingAutomaticTrigger);
                Assert.True(model.RestorePreviousAudioOnDeactivate);
                Assert.Equal(@"C:\Apps\Original.exe", original.Trigger.AppPath);
                Assert.Same(original, Assert.Single(model.TriggerEntries));
                model.AddAutomaticTrigger(); Assert.True(model.IsEditingAutomaticTrigger); model.CancelTriggerEdit();
            }
            finally { if (!child.IsClosed) child.Close(); }
        }));
    }

    private static RoutineEditorViewModel CreateModel(Func<CancellationToken, Task<NetworkDiscoveryResult>>? load = null) => new([], [],
        new AudioRoutine { Name = "Desk", MasterVolumePercent = 45, ShowInTrayMenu = true },
        loadAvailableNetworkNamesAsync: load ?? (_ => Task.FromResult(new NetworkDiscoveryResult(["Home"]))), preloadNetworks: false);

    private static async Task WithResources(AppTheme theme, Func<Task> action)
    {
        Application app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ResourceDictionary previous = app.Resources;
        Window? previousMain = app.MainWindow;
        var resources = new ResourceDictionary
        {
            ["BoolToVisibilityConverter"] = new BooleanToVisibilityConverter(),
            ["InverseBoolToVisibilityConverter"] = new InverseBooleanToVisibilityConverter(),
        };
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative) });
        foreach (string name in MenuIconNames) resources[$"AppMenu{name}Icon"] = new DrawingImage();
        app.Resources = resources;
        try { await action(); }
        finally { app.Resources = previous; app.MainWindow = previousMain; }
    }

    private static void Layout(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
    }

    private static void AssertTriggerLayout(RoutineTriggerEditorWindow dialog)
    {
        var viewer = (ScrollViewer)dialog.FindName("TriggerEditorScrollViewer");
        var content = (FrameworkElement)dialog.Content;
        AssertFieldsWithin((FrameworkElement)viewer.Content, viewer);
        var confirm = (Button)dialog.FindName("ConfirmTriggerButton");
        Assert.True(confirm.IsDefault);
        Assert.False(viewer.IsAncestorOf(confirm));
        Rect bounds = confirm.TransformToAncestor(content).TransformBounds(new Rect(confirm.RenderSize));
        Assert.InRange(bounds.Bottom, 0, content.ActualHeight + 1);
    }

    private static void AssertFieldsWithin(FrameworkElement body, ScrollViewer viewer)
    {
        foreach (FrameworkElement field in Descendants(body).OfType<FrameworkElement>()
            .Where(element => element is TextBox or ComboBox or CheckBox && element.ActualWidth > 0 && element.ActualHeight > 0))
        {
            Rect bounds = field.TransformToAncestor(body).TransformBounds(new Rect(field.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= viewer.ViewportWidth + 1, $"{field.GetType().Name}: {bounds}, viewport={viewer.ViewportWidth}");
        }
    }

    private static void Capture(FrameworkElement content, string name)
    {
        if (Environment.GetEnvironmentVariable("AUDIOPILOT_TEST_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle((Brush)content.FindResource("WindowBackgroundBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
