using System.Collections.Concurrent;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class AppViewModelTaskbarMediaTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ThumbnailCommands_ReuseOrderedMediaPipelineAndRejectCommandsDuringShutdown(int shutdownPhase)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelTaskbarMediaTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            var vm = harness.ViewModel;
            var originalLifetime = TestPrivateAccess.GetField<CancellationTokenSource>(vm, "_backgroundWorkCts");
            var originalCoordinator = TestPrivateAccess.GetField<AppCliOverlayCoordinator>(vm, "_cliOverlayCoordinator");
            using var lifetime = new CancellationTokenSource();
            using var overlay = new OverlayService(initiallyEnabled: false);
            var sends = new ConcurrentQueue<string>();
            var history = new ConcurrentQueue<ExecutionHistoryEntry>();
            var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool Send(string action) { sends.Enqueue(action); return true; }
            var coordinator = new AppCliOverlayCoordinator(harness.Audio, overlay, new MediaOverlayCommandService(),
                Logger.Instance, static () => new Settings(),
                mediaPlayPauseCommand: () => Send("play-pause"),
                mediaNextTrackCommand: () => Send("next"),
                mediaPreviousTrackCommand: () => Send("previous"),
                mediaHistoryRecorder: entry =>
                {
                    history.Enqueue(entry);
                    if (history.Count == 3) recorded.TrySetResult();
                });
            TestPrivateAccess.SetField(vm, "_backgroundWorkCts", lifetime);
            TestPrivateAccess.SetField(vm, "_cliOverlayCoordinator", coordinator);
            try
            {
                var command = vm.TaskbarMediaCommand;
                foreach (object? invalid in new object?[] { null, "next", MediaCommands.Stop })
                {
                    Assert.False(command.CanExecute(invalid));
                    command.Execute(invalid);
                }
                Assert.Empty(sends);
                RoutedUICommand[] actions = [MediaCommands.PreviousTrack, MediaCommands.TogglePlayPause, MediaCommands.NextTrack];
                var buttons = actions.Select(action => new ThumbButtonInfo { Command = command, CommandParameter = action }).ToArray();
                Assert.All(buttons, button => Assert.True(button.IsEnabled));
                foreach (var action in actions)
                {
                    Assert.True(command.CanExecute(action));
                    command.Execute(action);
                }
                TestPrivateAccess.RunTaskOnDispatcher(recorded.Task, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(["previous", "play-pause", "next"], sends);
                Assert.Equal(3, history.Count);
                Assert.All(history, entry => Assert.Equal("taskbar", entry.Source));

                if (shutdownPhase == 0) lifetime.Cancel();
                else if (shutdownPhase == 1) TestPrivateAccess.SetField(vm, "_isCleaningUp", true);
                else
                {
                    int availabilityChanges = 0;
                    void OnAvailabilityChanged(object? sender, EventArgs args) => availabilityChanges++;
                    command.CanExecuteChanged += OnAvailabilityChanged;
                    try
                    {
                        vm.BeginRuntimeShutdown();
                        Assert.True(vm.IsRuntimeShutdownRequested);
                        Assert.False(lifetime.IsCancellationRequested);
                        Assert.False(TestPrivateAccess.GetField<bool>(vm, "_isCleaningUp"));
                        Assert.All(actions, action => Assert.False(command.CanExecute(action)));
                        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                        Assert.All(buttons, button => Assert.False(button.IsEnabled));
                        vm.BeginRuntimeShutdown();
                        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                        Assert.Equal(1, availabilityChanges);
                    }
                    finally { command.CanExecuteChanged -= OnAvailabilityChanged; }
                }
                foreach (var action in actions)
                {
                    Assert.False(command.CanExecute(action));
                    command.Execute(action);
                }
                Assert.Equal(3, sends.Count);
            }
            finally
            {
                TestPrivateAccess.RunTaskOnDispatcher(coordinator.ShutdownAsync());
                TestPrivateAccess.SetField(vm, "_isCleaningUp", false);
                TestPrivateAccess.SetField(vm, "_backgroundWorkCts", originalLifetime);
                TestPrivateAccess.SetField(vm, "_cliOverlayCoordinator", originalCoordinator);
            }
        });
    }
}
