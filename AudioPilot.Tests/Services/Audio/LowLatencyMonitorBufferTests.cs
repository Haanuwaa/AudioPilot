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
        Assert.Equal(read.Length, buffer.Read(read));
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

        int read = buffer.Read(destination);

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
            for (int i = 0; i < 2_000; i++) buffer.Read(playback);
        }, cancellationToken);
        await Task.WhenAll(producer, consumer);

        Assert.InRange(buffer.BufferedBytes, 0, buffer.CapacityBytes);
        Assert.Equal(0, buffer.BufferedBytes % format.BlockAlign);
    }

    [Fact]
    public void Read_AppliesSoftwareGainAcrossPcmAndFloatFormats()
    {
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var floatBuffer = new LowLatencyMonitorBuffer(floatFormat, TimeSpan.FromMilliseconds(50), gain: 0.5f);
        byte[] floatInput = BitConverter.GetBytes(0.8f);
        floatBuffer.AddSamples(floatInput);
        byte[] floatOutput = new byte[4];
        floatBuffer.Read(floatOutput);
        Assert.InRange(BitConverter.ToSingle(floatOutput, 0), 0.399f, 0.401f);

        floatBuffer.Gain = 0f;
        floatBuffer.AddSamples(floatInput);
        floatBuffer.Read(floatOutput);
        Assert.Equal(0f, BitConverter.ToSingle(floatOutput, 0));

        var pcm16Format = new WaveFormat(48_000, 16, 1);
        var pcm16Buffer = new LowLatencyMonitorBuffer(pcm16Format, TimeSpan.FromMilliseconds(50), gain: 0.25f);
        pcm16Buffer.AddSamples(BitConverter.GetBytes((short)10_000));
        byte[] pcm16Output = new byte[2];
        pcm16Buffer.Read(pcm16Output);
        Assert.Equal((short)2_500, BitConverter.ToInt16(pcm16Output, 0));

        var pcm24Format = new WaveFormat(48_000, 24, 1);
        var pcm24Buffer = new LowLatencyMonitorBuffer(pcm24Format, TimeSpan.FromMilliseconds(50), gain: 0.5f);
        const int sample24 = -4_000_000;
        pcm24Buffer.AddSamples([(byte)(sample24 & 0xFF), (byte)((sample24 >> 8) & 0xFF), (byte)((sample24 >> 16) & 0xFF)]);
        byte[] pcm24Output = new byte[3];
        pcm24Buffer.Read(pcm24Output);
        int decoded24 = pcm24Output[0] | (pcm24Output[1] << 8) | (pcm24Output[2] << 16);
        if ((decoded24 & 0x00800000) != 0) decoded24 |= unchecked((int)0xFF000000);
        Assert.Equal(-2_000_000, decoded24);

        var pcm32Format = new WaveFormat(48_000, 32, 1);
        var pcm32Buffer = new LowLatencyMonitorBuffer(pcm32Format, TimeSpan.FromMilliseconds(50), gain: 0.5f);
        pcm32Buffer.AddSamples(BitConverter.GetBytes(1_000_000_000));
        byte[] pcm32Output = new byte[4];
        pcm32Buffer.Read(pcm32Output);
        Assert.Equal(500_000_000, BitConverter.ToInt32(pcm32Output, 0));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteGain_ClampsToSilence(float nonFiniteGain)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        var buffer = new LowLatencyMonitorBuffer(format, TimeSpan.FromMilliseconds(50), gain: nonFiniteGain);
        Assert.Equal(0f, buffer.Gain);

        buffer.AddSamples(BitConverter.GetBytes(0.75f));
        byte[] output = new byte[4];
        buffer.Read(output);
        Assert.Equal(0f, BitConverter.ToSingle(output, 0));

        buffer.Gain = 1.0f;
        buffer.Gain = nonFiniteGain;
        Assert.Equal(0f, buffer.Gain);
    }
}
