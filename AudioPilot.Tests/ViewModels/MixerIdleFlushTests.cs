using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.ViewModels;

[Collection("WpfApplicationIsolation")]
public sealed class MixerIdleFlushTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HidingPreservesLatestSliderWriteBeforeClearingRows(bool microphone)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var fixture = new Fixture(microphone);
            await using var pause = await AudioQueuePause.StartAsync();
            fixture.Row.Volume = 50;
            fixture.Row.Volume = 73;
            fixture.Mixer.TrimIdleState();
            Task<MixerViewModel.MixerMutationResult> flush = fixture.GetFlush();
            Assert.True(fixture.Row.HasPendingVolumeEdit);
            Assert.Single(fixture.Mixer.Sessions);
            Assert.Equal(73, fixture.Row.Volume);
            await pause.ReleaseAsync();
            Assert.Equal(1, (await flush).Attempted);
            await TestExecutionGuards.WaitUntilAsync(() => fixture.Mixer.Sessions.Count == 0, "Idle trim did not finish.");
            Assert.Equal(1, fixture.EndpointQueries);
            Assert.False(fixture.Row.HasPendingVolumeEdit);
        });
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("remove")]
    [InlineData("supersede")]
    public async Task DeferredWriteRespectsShutdownRowRemovalAndNewerEdits(string change)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var fixture = new Fixture(false);
            await using var pause = await AudioQueuePause.StartAsync();
            fixture.Row.Volume = 73;
            fixture.Mixer.TrimIdleState();
            Task<MixerViewModel.MixerMutationResult> flush = fixture.GetFlush();
            await using AudioQueuePause? newerWrites = change == "supersede" ? AudioQueuePause.Queue() : null;
            try
            {
                if (change == "shutdown") fixture.Mixer.Cleanup();
                else if (change == "remove") fixture.Rows.TryRemove("master:primary", out _);
                else fixture.Row.Volume = 80;
                await pause.ReleaseAsync();
                if (newerWrites != null) await newerWrites.WaitUntilEnteredAsync();
                Assert.Equal(0, (await flush).Attempted);
                await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                Assert.Equal(0, fixture.EndpointQueries);
                if (change == "supersede")
                {
                    Assert.Single(fixture.Mixer.Sessions);
                    Assert.Equal(80, fixture.Row.Volume);
                    Assert.True(fixture.Row.HasPendingVolumeEdit);
                }
            }
            finally { fixture.Mixer.Cleanup(); }
        });
    }

    [Fact]
    public async Task QueuedLeadingAppWriteCompletesBeforeIdleTrimAndUsesMonotonicTime()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var fixture = new Fixture(false, process: true);
            await using var pause = await AudioQueuePause.StartAsync();
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            fixture.Row.Volume = 73;
            long after = System.Diagnostics.Stopwatch.GetTimestamp();
            object states = TestPrivateAccess.GetField<object>(fixture.Mixer, "_throttleStates");
            object state = states.GetType().GetProperty("Item")!.GetValue(states, [fixture.Id])!;
            long applied = (long)state.GetType().GetField("LastAppliedTimestamp")!.GetValue(state)!;
            Assert.InRange(applied, before, after);
            fixture.Mixer.TrimIdleState();
            Task<MixerViewModel.MixerMutationResult> flush = fixture.GetFlush();
            await pause.ReleaseAsync();
            await flush;
            await TestExecutionGuards.WaitUntilAsync(() => fixture.Mixer.Sessions.Count == 0, "Idle trim did not wait for leading write.");
            Assert.Equal(1, fixture.EndpointQueries);
        });
    }

    [Fact]
    public async Task RepeatedHidingWaitsForThePreviouslyCapturedWrite()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var fixture = new Fixture(false);
            await using var pause = await AudioQueuePause.StartAsync();
            fixture.Row.Volume = 73;
            fixture.Mixer.TrimIdleState();
            Task<MixerViewModel.MixerMutationResult> first = fixture.GetFlush();
            fixture.Mixer.TrimIdleState();
            Task<MixerViewModel.MixerMutationResult> second = fixture.GetFlush();
            Assert.Single(fixture.Mixer.Sessions);
            await pause.ReleaseAsync();
            Assert.Equal(1, (await first).Attempted);
            Assert.Equal(0, (await second).Attempted);
            await TestExecutionGuards.WaitUntilAsync(() => fixture.Mixer.Sessions.Count == 0, "Repeated idle trim did not finish.");
            Assert.Equal(1, fixture.EndpointQueries);
        });
    }

    [Fact]
    public async Task ReopeningCancelsOnlyTheDeferredTrim()
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var fixture = new Fixture(false);
            var completion = new TaskCompletionSource<MixerViewModel.MixerMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task trim = TestPrivateAccess.InvokeNonPublicTask(fixture.Mixer, "CompleteIdleVolumeFlushAsync", completion.Task, null, 0);
            fixture.Mixer.CancelIdleTrim();
            completion.SetResult(MixerViewModel.MixerMutationResult.Success());
            await trim;
            Assert.Single(fixture.Mixer.Sessions);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HidingPreservesAutomaticUnmuteWithTheSliderWrite(bool microphone)
    {
        await SharedStaDispatcherHost.RunAsync(async () =>
        {
            using var fixture = new Fixture(microphone);
            await using var pause = await AudioQueuePause.StartAsync();
            fixture.Row.SetStateFromSystem(0, true);
            TestPrivateAccess.GetField<ConcurrentDictionary<string, float>>(fixture.Mixer, "_userSetVolumes")[fixture.Id] = 0;
            fixture.Row.Volume = 10;
            fixture.Mixer.TrimIdleState();
            Task<MixerViewModel.MixerMutationResult> flush = fixture.GetFlush();
            Assert.True(fixture.Row.HasPendingMuteEdit);
            Assert.False(TestPrivateAccess.GetField<CancellationTokenSource>(fixture.Mixer, "_muteApplyCts").IsCancellationRequested);
            await pause.ReleaseAsync();
            await flush;
            await TestExecutionGuards.WaitUntilAsync(() => fixture.Mixer.Sessions.Count == 0, "Idle trim did not finish after unmute.");
            Assert.Equal(2, fixture.EndpointQueries);
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ReaderWriterLockSlim _queryLock = new();
        public MixerViewModel Mixer { get; }
        public AudioSessionItem Row { get; }
        public string Id { get; }
        public ConcurrentDictionary<string, AudioSessionItem> Rows { get; }
        public int EndpointQueries;

        public Fixture(bool microphone, bool process = false)
        {
            var audio = (AudioDeviceService)RuntimeHelpers.GetUninitializedObject(typeof(AudioDeviceService));
            var query = new AudioDeviceEndpointQueryHelper(null!, _queryLock, Logger.Instance,
                () => { Interlocked.Increment(ref EndpointQueries); return true; },
                () => [Role.Multimedia], () => [Role.Multimedia], (_, _) => { });
            TestPrivateAccess.SetField(audio, "_endpointQueryHelper", query);
            var sessions = (AudioSessionService)RuntimeHelpers.GetUninitializedObject(typeof(AudioSessionService));
            TestPrivateAccess.SetField(sessions, "_recentSnapshotCache", new AudioSessionRecentSnapshotCache());
            TestPrivateAccess.SetField(audio, "_sessionService", sessions);
            Mixer = MixerViewModelChurnTests.CreateMixerForApplyTests(microphone ? AudioMixerMode.Input : AudioMixerMode.Output);
            TestPrivateAccess.SetField(Mixer, "_audio", audio);
            Id = process ? "pid:42" : microphone ? "mic:primary" : "master:primary";
            Row = new AudioSessionItem("Endpoint", 25, !microphone && !process, microphone && !process,
                processId: process ? 42u : null, endpointId: "original");
            Mixer.Sessions.Add(Row);
            Rows = TestPrivateAccess.GetField<ConcurrentDictionary<string, AudioSessionItem>>(Mixer, "_sessionsById");
            Rows[Id] = Row;
            Mixer.AttachOwnedVolumeChangedHandler(Id, Row);
        }

        public Task<MixerViewModel.MixerMutationResult> GetFlush()
            => TestPrivateAccess.GetField<Task<MixerViewModel.MixerMutationResult>>(Mixer, "_idleVolumeFlushTask");

        public void Dispose() { Mixer.Cleanup(); _queryLock.Dispose(); }
    }

    private sealed class AudioQueuePause : IAsyncDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _work = Task.CompletedTask;

        public static async Task<AudioQueuePause> StartAsync()
        {
            AudioQueuePause pause = Queue();
            await pause.WaitUntilEnteredAsync();
            return pause;
        }

        public static AudioQueuePause Queue()
        {
            var pause = new AudioQueuePause();
            pause._work = ComThreadingHelper.RunOnCoreAudioThreadAsync(() =>
            {
                pause._entered.SetResult();
                Assert.True(pause._release.Wait(TimeSpan.FromSeconds(5)), "Paused audio queue was not released.");
            });
            return pause;
        }

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        public async Task ReleaseAsync() { _release.Set(); await _work; }
        public async ValueTask DisposeAsync() { await ReleaseAsync(); _release.Dispose(); }
    }
}
