using AudioPilot.Services.Audio.Testing;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

public sealed class LowLatencyMonitorBufferTests
{
    [Fact]
    public void Overflow_DropsOldestWholeFramesAndRemainsBounded()
    {
        var format = new WaveFormat(1_000, 16, 1);
        var buffer = new LowLatencyMonitorBuffer(format, TimeSpan.FromMilliseconds(10));
        short[] frames = [.. Enumerable.Range(0, 20).Select(value => (short)value)];
        byte[] bytes = new byte[frames.Length * sizeof(short)];
        Buffer.BlockCopy(frames, 0, bytes, 0, bytes.Length);

        buffer.AddSamples(bytes);

        Assert.Equal(buffer.CapacityBytes, buffer.BufferedBytes);
        Assert.Equal(bytes.Length - buffer.CapacityBytes, buffer.DroppedBytes);
        byte[] read = new byte[buffer.CapacityBytes];
        Assert.Equal(read.Length, buffer.Read(read, 0, read.Length));
        short[] retained = new short[read.Length / sizeof(short)];
        Buffer.BlockCopy(read, 0, retained, 0, read.Length);
        Assert.Equal(frames[^retained.Length..], retained);
    }

    [Fact]
    public void Read_ZeroFillsUnderrunAndAlwaysSatisfiesPlaybackRequest()
    {
        var format = new WaveFormat(48_000, 16, 2);
        var buffer = new LowLatencyMonitorBuffer(format, TimeSpan.FromMilliseconds(250));
        byte[] source = [1, 2, 3, 4];
        buffer.AddSamples(source);
        byte[] destination = [.. Enumerable.Repeat((byte)0xCC, 12)];

        int read = buffer.Read(destination, 0, destination.Length);

        Assert.Equal(destination.Length, read);
        Assert.Equal(source, destination[..source.Length]);
        Assert.All(destination[source.Length..], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ConcurrentProducerAndConsumer_NeverExceedCapacityOrBreakAlignment()
    {
        var format = new WaveFormat(48_000, 24, 2);
        var buffer = new LowLatencyMonitorBuffer(format, TimeSpan.FromMilliseconds(250));
        byte[] packet = new byte[format.BlockAlign * 97];
        byte[] playback = new byte[format.BlockAlign * 113];

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < 2_000; i++) buffer.AddSamples(packet);
        }, cancellationToken);
        Task consumer = Task.Run(() =>
        {
            for (int i = 0; i < 2_000; i++) buffer.Read(playback, 0, playback.Length);
        }, cancellationToken);
        await Task.WhenAll(producer, consumer);

        Assert.InRange(buffer.BufferedBytes, 0, buffer.CapacityBytes);
        Assert.Equal(0, buffer.BufferedBytes % format.BlockAlign);
    }
}
