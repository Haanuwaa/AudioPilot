using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioMeterSamplerTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void VisibilityLeases_KeepOtherPresentationsAliveAndRejectAdmissionAfterShutdown()
    {
        using var demand = new MixerVisibilityDemand();
        using var main = demand.Acquire(AudioMixerMode.Output);
        using var flyout = demand.Acquire(AudioMixerMode.Output);
        using var input = demand.Acquire(AudioMixerMode.Input);
        Assert.Equal(AudioMeterModes.Both, demand.Current.Modes);
        long bothVersion = demand.Current.Version;
        main.Dispose();
        main.Dispose();
        Assert.Equal((AudioMeterModes.Both, bothVersion), demand.Current);
        flyout.Dispose();
        Assert.Equal(AudioMeterModes.Input, demand.Current.Modes);
        demand.Dispose();
        input.Dispose();
        Assert.Equal(AudioMeterModes.None, demand.Current.Modes);
        Assert.Throws<ObjectDisposedException>(() => demand.Acquire(AudioMixerMode.Input));
    }

    [Fact]
    public async Task LastHiddenPresentation_ReleasesNativeObjectsOnOwnerThreadAndStopsPolling()
    {
        using var time = new ManualTimeProvider();
        var backend = new Backend();
        int created = 0;
        using var sampler = new AudioMeterSampler(() => { Interlocked.Increment(ref created); return backend; }, time);
        Assert.Equal(0, created);
        using var main = sampler.AcquireVisibility(AudioMixerMode.Output);
        await backend.NextReadAsync();
        await WaitForSnapshotAsync(sampler, AudioMixerMode.Output);
        using var flyout = sampler.AcquireVisibility(AudioMixerMode.Output);
        main.Dispose();
        Assert.True(sampler.TryGetLatest(AudioMixerMode.Output, out _));
        flyout.Dispose();
        await backend.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        int reads = backend.ReadCount;
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(reads, backend.ReadCount);
        Assert.False(sampler.TryGetLatest(AudioMixerMode.Output, out _));
        Assert.Equal(backend.ReadThread, backend.DisposeThread);
        Assert.Equal(ApartmentState.MTA, backend.Apartment);
        await sampler.StopAsync(TestToken);
    }

    [Fact]
    public async Task ModeChangeDuringBlockedRead_DiscardsOldResultAndRefreshesInputWithoutOverlap()
    {
        using var time = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        var backend = new Backend { BlockFirst = release };
        using var sampler = new AudioMeterSampler(() => backend, time);
        using var output = sampler.AcquireVisibility(AudioMixerMode.Output);
        ReadCall first = await backend.NextReadAsync();
        using var input = sampler.AcquireVisibility(AudioMixerMode.Input);
        output.Dispose();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(sampler.TryGetLatest(AudioMixerMode.Output, out _));
        Assert.False(sampler.TryGetLatest(AudioMixerMode.Input, out _));
        // Many refresh signals while blocked still have only one operation in flight.
        for (int i = 0; i < 100; i++) sampler.RequestRefresh();
        Assert.Equal(1, backend.ReadCount);
        release.Set();
        ReadCall second = await backend.NextReadAsync();
        Assert.Equal(AudioMeterModes.Input, second.Modes);
        Assert.True(second.Refresh);
        AudioMeterSnapshot snapshot = await WaitForSnapshotAsync(sampler, AudioMixerMode.Input);
        Assert.All(snapshot.Batch.Readings, reading => Assert.Equal(AudioMixerMode.Input, reading.Source.Mode));
        Assert.False(sampler.TryGetLatest(AudioMixerMode.Output, out _));
        Assert.True(backend.BlockReleased);
        await sampler.StopAsync(TestToken);
    }

    [Fact]
    public async Task ShutdownDuringNativeRead_DetachesPromptlyButDefersOwnerThreadDisposal()
    {
        using var release = new ManualResetEventSlim();
        var backend = new Backend { BlockFirst = release };
        using var sampler = new AudioMeterSampler(() => backend);
        using var visible = sampler.AcquireVisibility(AudioMixerMode.Input);
        ReadCall read = await backend.NextReadAsync();
        try
        {
            sampler.Dispose();
            Assert.True(read.Token.IsCancellationRequested);
            Assert.False(sampler.Completion.IsCompleted);
            Assert.False(backend.Disposed.Task.IsCompleted);
            Assert.False(sampler.TryGetLatest(AudioMixerMode.Input, out _));
            Assert.Throws<ObjectDisposedException>(() => sampler.AcquireVisibility(AudioMixerMode.Output));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.StopAsync(new CancellationToken(true)));
        }
        finally { release.Set(); }
        await sampler.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        Assert.Equal(backend.ReadThread, backend.DisposeThread);
        Assert.Equal(1, backend.DisposeCount);
        Assert.True(backend.BlockReleased);
    }

    [Fact]
    public async Task StalledSample_ExpiresAndReopeningReusesWorkerAfterReleasingNativeReaders()
    {
        using var time = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        var first = new Backend { BlockReadNumber = 2, BlockFirst = release };
        var replacement = new Backend();
        int created = 0;
        using var sampler = new AudioMeterSampler(() => Interlocked.Increment(ref created) == 1 ? first : replacement, time);
        using var output = sampler.AcquireVisibility(AudioMixerMode.Output);
        await first.NextReadAsync();
        await WaitForSnapshotAsync(sampler, AudioMixerMode.Output);
        await time.WaitForActiveTimerAsync(TestToken);
        time.Advance(TimeSpan.FromMilliseconds(300));
        await first.NextReadAsync();
        try
        {
            Assert.False(sampler.TryGetLatest(AudioMixerMode.Output, out _));
            output.Dispose();
        }
        finally { release.Set(); }
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        using var input = sampler.AcquireVisibility(AudioMixerMode.Input);
        await replacement.NextReadAsync();
        await WaitForSnapshotAsync(sampler, AudioMixerMode.Input);
        Assert.Equal(first.ReadThread, replacement.ReadThread);
        Assert.Equal(first.ReadThread, first.DisposeThread);
        Assert.True(first.BlockReleased);
        await sampler.StopAsync(TestToken);
    }

    [Fact]
    public async Task ReopeningDuringFailedIdleDisposal_UsesFreshBackendOnOwnerThread()
    {
        using var time = new ManualTimeProvider();
        using var release = new ManualResetEventSlim();
        var first = new Backend
        {
            OnDispose = () =>
            {
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Backend disposal was not released.");
                throw new InvalidOperationException("Injected idle backend disposal failure.");
            }
        };
        var replacement = new Backend();
        int created = 0;
        using var sampler = new AudioMeterSampler(() => Interlocked.Increment(ref created) == 1 ? first : replacement, time);
        using (sampler.AcquireVisibility(AudioMixerMode.Output))
        {
            await first.NextReadAsync();
            await WaitForSnapshotAsync(sampler, AudioMixerMode.Output);
        }
        try
        {
            await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
            using var input = sampler.AcquireVisibility(AudioMixerMode.Input);
            Assert.Equal(1, Volatile.Read(ref created));
            Assert.False(sampler.TryGetLatest(AudioMixerMode.Output, out _));
            release.Set();

            ReadCall read = await replacement.NextReadAsync();
            Assert.Equal(AudioMeterModes.Input, read.Modes);
            Assert.True(read.Refresh);
            var snapshot = await WaitForSnapshotAsync(sampler, AudioMixerMode.Input);
            Assert.Null(snapshot.ErrorCode);
            Assert.Equal(first.ReadThread, first.DisposeThread);
            Assert.Equal(first.DisposeThread, replacement.ReadThread);
            Assert.False(sampler.Completion.IsCompleted);
            await sampler.StopAsync(TestToken);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(1, replacement.DisposeCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task FailedRead_IsUnknownRatherThanSilenceAndCanRecoverAfterBackoff()
    {
        using var time = new ManualTimeProvider();
        var backend = new Backend { FailFirst = true };
        using var sampler = new AudioMeterSampler(() => backend, time);
        using var visible = sampler.AcquireVisibility(AudioMixerMode.Output);
        await backend.NextReadAsync();
        AudioMeterSnapshot failed = await WaitForSnapshotAsync(sampler, AudioMixerMode.Output);
        Assert.Equal(unchecked((int)0x80004005), failed.ErrorCode);
        Assert.Equal(AudioMeterModes.None, failed.Batch.CompleteModes);
        Assert.Empty(failed.Batch.Readings);
        for (int index = 0; index < 100; index++) sampler.RequestRefresh();
        await time.WaitForActiveTimerAsync(TestToken);
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, backend.ReadCount);
        time.Advance(TimeSpan.FromMilliseconds(750));
        ReadCall retry = await backend.NextReadAsync();
        Assert.True(retry.Refresh);
        AudioMeterSnapshot recovered = await WaitForSnapshotAsync(sampler, AudioMixerMode.Output);
        Assert.Null(recovered.ErrorCode);
        Assert.Equal(0, Assert.Single(recovered.Batch.Readings).Peak);
        await sampler.StopAsync(TestToken);
    }

    [Theory]
    [InlineData(float.NaN, null)]
    [InlineData(float.PositiveInfinity, null)]
    [InlineData(-0.1f, 0f)]
    [InlineData(1.1f, 1f)]
    public void NativePeakValidation_DoesNotPresentInvalidNumbersAsSilence(float native, float? expected)
    {
        AudioMeterReading reading = AudioMeterReading.FromPeak(new(AudioMixerMode.Input, "endpoint"), native);
        Assert.Equal(expected, reading.Peak);
        Assert.Equal(expected.HasValue ? AudioMeterStatus.Available : AudioMeterStatus.Unavailable, reading.Status);
    }

    private static async Task<AudioMeterSnapshot> WaitForSnapshotAsync(AudioMeterSampler sampler, AudioMixerMode mode)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (sampler.TryGetLatest(mode, out AudioMeterSnapshot? result)) return result!;
            await Task.Delay(1, deadline.Token);
        }
    }

    private sealed record ReadCall(AudioMeterModes Modes, bool Refresh, CancellationToken Token);

    private sealed class Backend : IAudioMeterBackend
    {
        private readonly Channel<ReadCall> _reads = Channel.CreateUnbounded<ReadCall>();
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim? BlockFirst;
        internal Action? OnDispose;
        internal int BlockReadNumber = 1;
        internal bool FailFirst;
        internal bool BlockReleased;
        internal int ReadCount, ReadThread, DisposeThread, DisposeCount;
        internal ApartmentState Apartment;

        internal async Task<ReadCall> NextReadAsync() => await _reads.Reader.ReadAsync(TestToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestToken);

        public AudioMeterBatch Read(AudioMeterModes modes, bool refresh, CancellationToken cancellationToken)
        {
            ReadThread = Environment.CurrentManagedThreadId;
            Apartment = Thread.CurrentThread.GetApartmentState();
            int number = Interlocked.Increment(ref ReadCount);
            _reads.Writer.TryWrite(new(modes, refresh, cancellationToken));
            if (number == BlockReadNumber && BlockFirst != null) BlockReleased = BlockFirst.Wait(TimeSpan.FromSeconds(10), TestToken);
            if (number == 1 && FailFirst) throw new COMException("Injected read failure", unchecked((int)0x80004005));
            var readings = ImmutableArray.CreateBuilder<AudioMeterReading>();
            if ((modes & AudioMeterModes.Output) != 0) readings.Add(AudioMeterReading.FromPeak(new(AudioMixerMode.Output, "output"), 0));
            if ((modes & AudioMeterModes.Input) != 0) readings.Add(AudioMeterReading.FromPeak(new(AudioMixerMode.Input, "input"), 0));
            return new(readings.ToImmutable(), modes);
        }

        public void Dispose()
        {
            DisposeThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref DisposeCount);
            Disposed.TrySetResult();
            OnDispose?.Invoke();
        }
    }
}
