using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("AppDialogServiceIsolation")]
public sealed class AppViewModelMuteInteractionTests : IDisposable
{
    [Theory]
    [InlineData("mic", true)]
    [InlineData("mic", false)]
    [InlineData("sound", true)]
    [InlineData("sound", false)]
    [InlineData("deafen", true)]
    [InlineData("deafen", false)]
    public Task CliMuteWaitsForFailedWrites_AndJoinsAnIdenticalPendingRequest(string target, bool toggle) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ =>
        {
            entered.TrySetResult();
            release.Wait();
            throw new InvalidOperationException("write failed");
        };
        AudioDeviceService.SetPlaybackMuteOverrideForTests = static _ => throw new InvalidOperationException("write failed");
        var model = harness.ViewModel;
        Task<bool> command = (target, toggle) switch
        {
            ("mic", true) => model.ToggleMuteMicFromCliAsync(),
            ("mic", false) => model.SetMuteMicFromCliAsync(true),
            ("sound", true) => model.ToggleMuteSoundFromCliAsync(),
            ("sound", false) => model.SetMuteSoundFromCliAsync(true),
            ("deafen", true) => model.ToggleDeafenFromCliAsync(),
            _ => model.SetDeafenFromCliAsync(true),
        };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.False(command.IsCompleted);
            Task<bool> joined = target switch
            {
                "mic" => model.SetMuteMicFromCliAsync(true),
                "sound" => model.SetMuteSoundFromCliAsync(true),
                _ => model.SetDeafenFromCliAsync(true),
            };
            Assert.False(joined.IsCompleted);
            release.Set();
            Assert.False(await command);
            Assert.False(await joined);
        }
        finally
        {
            release.Set();
            await model.CleanupAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CliMuteDoesNotReportSuccessAfterCancellationOrSupersession(bool cancel) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ => { entered.TrySetResult(); release.Wait(); };
        AudioDeviceService.SetPlaybackMuteOverrideForTests = static _ => { };
        var model = harness.ViewModel;
        Task<bool> command = model.SetMuteMicFromCliAsync(true, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            }
            else
            {
                model.MuteMic = false;
                release.Set();
                Assert.False(await command);
            }
        }
        finally
        {
            release.Set();
            await model.CleanupAsync();
        }
    });

    [Fact]
    public Task HotkeyAndCliMuteHistoryUsesCompletedWritesAndDoesNotDuplicateCliEntries() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
        using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher, allowBackgroundWork: true);
        var nativeTraces = new List<string?>();
        AudioDeviceService.SetMicrophoneMuteOverrideForTests = _ =>
        {
            nativeTraces.Add(OperationTrace.CurrentId);
            throw new InvalidOperationException("private device details");
        };
        AudioDeviceService.SetPlaybackMuteOverrideForTests = static _ => { };
        var outcome = await harness.ViewModel.ChangeMuteStateAsync(() => harness.ViewModel.MuteMic = true);
        Assert.False(outcome.Succeeded);
        Assert.False(await harness.ViewModel.ToggleMuteMicFromCliAsync());
        string json = harness.ViewModel.GetDiagnosticsHistoryFromCli(true, 30, "mute", redactOutput: true);
        var entries = System.Text.Json.Nodes.JsonNode.Parse(json)!["data"]!["entries"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Single(entries, entry => entry!["source"]!.GetValue<string>() == "hotkey");
        Assert.Single(entries, entry => entry!["source"]!.GetValue<string>() == "cli");
        Assert.All(entries, entry => Assert.False(entry!["success"]!.GetValue<bool>()));
        Assert.All(entries, entry => Assert.Contains(entry!["traceId"]!.GetValue<string>(), nativeTraces));
        Assert.Equal(2, nativeTraces.Distinct().Count());
        Assert.Null(OperationTrace.CurrentId);
        Assert.DoesNotContain("private device details", json, StringComparison.Ordinal);
        Assert.True(await harness.ViewModel.SetMuteMicFromCliAsync(false));
        var noChange = System.Text.Json.Nodes.JsonNode.Parse(harness.ViewModel.GetDiagnosticsHistoryFromCli(true, 30, "mute", redactOutput: true))!["data"]!["entries"]!.AsArray();
        Assert.Equal(3, noChange.Count);
        Assert.True(noChange[0]!["skipped"]!.GetValue<bool>());
        await harness.ViewModel.CleanupAsync();
    });

    [Theory]
    [InlineData(AudioMixerMode.Output)]
    [InlineData(AudioMixerMode.Input)]
    public void EndpointNotification_PreservesExplicitDeafenUntilAnEndpointIsUnmuted(AudioMixerMode first)
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(workspace, Dispatcher.CurrentDispatcher);
            TestPrivateAccess.SetField(harness.ViewModel, "_deafenBackingField", true);
            AudioMixerMode second = first == AudioMixerMode.Output ? AudioMixerMode.Input : AudioMixerMode.Output;
            harness.ViewModel.ProjectEndpointVolumeStateFromCommand(first, "endpoint", 50, true);
            Assert.True(harness.ViewModel.Deafen);
            harness.ViewModel.ProjectEndpointVolumeStateFromCommand(second, "endpoint", 50, true);
            Assert.True(harness.ViewModel.Deafen);
            Assert.False(harness.ViewModel.MuteMic);
            Assert.False(harness.ViewModel.MuteSound);
            harness.ViewModel.ProjectEndpointVolumeStateFromCommand(first, "endpoint", 50, false);
            Assert.False(harness.ViewModel.Deafen);
            harness.ViewModel.ProjectEndpointVolumeStateFromCommand(first, "endpoint", 50, true);
            Assert.False(harness.ViewModel.Deafen);
            Assert.True(harness.ViewModel.MuteMic);
            Assert.True(harness.ViewModel.MuteSound);
        });
    }

    [Fact]
    public void MuteApplyFailures_RollBackAllThreePropertiesOnTheUiDispatcher()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: true);
            int dispatcherThreadId = Environment.CurrentManagedThreadId;

            AssertRollbackOnDispatcher(
                harness.ViewModel,
                nameof(harness.ViewModel.MuteSound),
                setValue: () => harness.ViewModel.MuteSound = true,
                configureFailure: () => AudioDeviceService.SetPlaybackMuteOverrideForTests = static _ => throw new InvalidOperationException("playback-failure"),
                getValue: () => harness.ViewModel.MuteSound,
                dispatcherThreadId);

            AssertRollbackOnDispatcher(
                harness.ViewModel,
                nameof(harness.ViewModel.MuteMic),
                setValue: () => harness.ViewModel.MuteMic = true,
                configureFailure: () => AudioDeviceService.SetMicrophoneMuteOverrideForTests = static _ => throw new InvalidOperationException("microphone-failure"),
                getValue: () => harness.ViewModel.MuteMic,
                dispatcherThreadId);

            AssertRollbackOnDispatcher(
                harness.ViewModel,
                nameof(harness.ViewModel.Deafen),
                setValue: () => harness.ViewModel.Deafen = true,
                configureFailure: () =>
                {
                    AudioDeviceService.SetMicrophoneMuteOverrideForTests = static _ => { };
                    AudioDeviceService.SetPlaybackMuteOverrideForTests = static _ => throw new InvalidOperationException("deafen-failure");
                },
                getValue: () => harness.ViewModel.Deafen,
                dispatcherThreadId);
        });
    }

    [Fact]
    public void StaleMuteFailure_DoesNotOverwriteNewerDeafenIntent()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: true);
            using var firstPlaybackStarted = new ManualResetEventSlim(false);
            using var releaseFirstPlayback = new ManualResetEventSlim(false);
            int playbackCalls = 0;

            AudioDeviceService.SetMicrophoneMuteOverrideForTests = static _ => { };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = _ =>
            {
                if (Interlocked.Increment(ref playbackCalls) != 1)
                {
                    return;
                }

                firstPlaybackStarted.Set();
                Assert.True(releaseFirstPlayback.Wait(TimeSpan.FromSeconds(5)));
                throw new InvalidOperationException("stale-playback-failure");
            };

            harness.ViewModel.MuteSound = true;
            Assert.True(firstPlaybackStarted.Wait(TimeSpan.FromSeconds(5)));

            harness.ViewModel.Deafen = true;
            releaseFirstPlayback.Set();
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Assert.True(harness.ViewModel.Deafen);
            Assert.False(harness.ViewModel.MuteMic);
            Assert.False(harness.ViewModel.MuteSound);
            Assert.Equal(2, playbackCalls);
        });
    }

    [Fact]
    public void RapidMuteChanges_AreSerializedAndLatestIntentWins()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: true);
            using var firstPlaybackStarted = new ManualResetEventSlim(false);
            using var releaseFirstPlayback = new ManualResetEventSlim(false);
            var appliedPlaybackStates = new List<bool>();

            AudioDeviceService.SetMicrophoneMuteOverrideForTests = static _ => { };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = mute =>
            {
                lock (appliedPlaybackStates)
                {
                    appliedPlaybackStates.Add(mute);
                }

                if (mute)
                {
                    firstPlaybackStarted.Set();
                    Assert.True(releaseFirstPlayback.Wait(TimeSpan.FromSeconds(5)));
                }
            };

            harness.ViewModel.MuteSound = true;
            Assert.True(firstPlaybackStarted.Wait(TimeSpan.FromSeconds(5)));

            harness.ViewModel.MuteSound = false;
            releaseFirstPlayback.Set();
            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());

            Assert.Equal([true, false], appliedPlaybackStates);
            Assert.False(harness.ViewModel.MuteSound);
            Assert.False(harness.ViewModel.Deafen);
        });
    }

    [Fact]
    public void MuteSound_ProjectsToInitializedMixerBeforeDeviceWriteCompletes()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: true);
            using var playbackWriteStarted = new ManualResetEventSlim(false);
            using var releasePlaybackWrite = new ManualResetEventSlim(false);
            MixerViewModel mixer = harness.ViewModel.Mixer;
            var master = new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false);
            TestPrivateAccess.SetField(
                mixer,
                "<Sessions>k__BackingField",
                new ObservableCollection<AudioSessionItem> { master });

            AudioDeviceService.SetMicrophoneMuteOverrideForTests = static _ => { };
            AudioDeviceService.SetPlaybackMuteOverrideForTests = _ =>
            {
                playbackWriteStarted.Set();
                Assert.True(releasePlaybackWrite.Wait(TimeSpan.FromSeconds(5)));
            };

            try
            {
                harness.ViewModel.MuteSound = true;

                Assert.True(master.IsMuted);
                Assert.True(playbackWriteStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(master.IsMuted);
            }
            finally
            {
                releasePlaybackWrite.Set();
            }

            TestPrivateAccess.RunTaskOnDispatcher(harness.ViewModel.WaitForQueuedBackgroundTasksForTestsAsync());
        });
    }

    [Fact]
    public void EndpointVolumeCommand_ProjectsVolumeAndMuteWithoutDeviceWriteBack()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            EnsureApplication();
            using var workspace = new TestSettingsWorkspace(nameof(AppViewModelMuteInteractionTests));
            using var harness = AppViewModelHarnessBuilder.CreateInteractionHarness(
                workspace,
                Dispatcher.CurrentDispatcher,
                allowBackgroundWork: true);
            var mixer = new MixerViewModel(harness.Audio, Dispatcher.CurrentDispatcher, deviceCache: harness.DeviceCache);
            TestPrivateAccess.SetField(harness.ViewModel, "_mixer", mixer);
            var master = new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false, endpointId: "playback-primary");
            TestPrivateAccess.GetField<System.Collections.Concurrent.ConcurrentDictionary<string, AudioSessionItem>>(mixer, "_sessionsById")["master:primary"] = master;
            int writeBacks = 0;
            master.VolumeChanged += _ => writeBacks++;
            master.MuteChanged += _ => writeBacks++;
            TestPrivateAccess.SetField(
                mixer,
                "<Sessions>k__BackingField",
                new ObservableCollection<AudioSessionItem> { master });

            harness.ViewModel.ProjectEndpointVolumeStateFromCommand(
                AudioMixerMode.Output,
                "playback-primary",
                72f,
                isMuted: true);

            Assert.Equal(72f, master.Volume);
            Assert.True(master.IsMuted);
            Assert.True(harness.ViewModel.MuteSound);
            Assert.False(harness.ViewModel.Deafen);
            Assert.Equal(0, writeBacks);
        });
    }

    private static void AssertRollbackOnDispatcher(
        AppViewModel viewModel,
        string propertyName,
        Action setValue,
        Action configureFailure,
        Func<bool> getValue,
        int dispatcherThreadId)
    {
        AudioDeviceService.ResetTestHooks();
        configureFailure();
        var notificationThreads = new List<int>();

        void handler(object? _, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, propertyName, StringComparison.Ordinal))
            {
                notificationThreads.Add(Environment.CurrentManagedThreadId);
            }
        }

        viewModel.PropertyChanged += handler;
        try
        {
            setValue();
            TestPrivateAccess.RunTaskOnDispatcher(viewModel.WaitForQueuedBackgroundTasksForTestsAsync());
        }
        finally
        {
            viewModel.PropertyChanged -= handler;
        }

        Assert.False(getValue());
        Assert.Equal(2, notificationThreads.Count);
        Assert.All(notificationThreads, threadId => Assert.Equal(dispatcherThreadId, threadId));
    }

    private static void EnsureApplication()
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
    }

    public void Dispose()
    {
        AudioDeviceService.ResetTestHooks();
    }
}
