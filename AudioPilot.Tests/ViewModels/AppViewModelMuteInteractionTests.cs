using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Services.Audio;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.ViewModels;

[Collection("AppDialogServiceIsolation")]
public sealed class AppViewModelMuteInteractionTests : IDisposable
{
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
            MixerViewModel mixer = harness.ViewModel.Mixer;
            var master = new AudioSessionItem("Master Volume", 50f, isMaster: true, isMic: false);
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
