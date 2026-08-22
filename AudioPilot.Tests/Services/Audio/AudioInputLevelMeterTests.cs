using System.Buffers.Binary;
using AudioPilot.Services.Audio.Testing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioInputLevelMeterTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void PcmFormats_MapHalfScaleNearNinetyFivePercent(int bits)
    {
        WaveFormat format = new(48_000, bits, 1);
        byte[] sample = CreateHalfScalePcm(bits);

        (double rms, double peak) = AudioInputLevelMeter.CalculateLinearLevels(sample, format);

        Assert.InRange(rms, 0.499, 0.501);
        Assert.InRange(peak, 0.499, 0.501);
        var meter = new AudioInputLevelMeter();
        meter.Process(sample, format, AudioClientBufferFlags.None);
        Assert.InRange(meter.Read().LevelPercent, 89.8, 90.1);
    }

    [Fact]
    public void FloatFormat_IgnoresInvalidValuesAndClampsOutOfRangeValues()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        float[] source = [float.NaN, float.PositiveInfinity, 2, -0.5f];
        byte[] bytes = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);

        (double rms, double peak) = AudioInputLevelMeter.CalculateLinearLevels(bytes, format);

        Assert.InRange(rms, 0.7905, 0.7906);
        Assert.Equal(1, peak);
    }

    [Fact]
    public void SilentFlag_ProducesFloorWithoutReadingPacketData()
    {
        var meter = new AudioInputLevelMeter();
        meter.Process([0xFF], new WaveFormat(48_000, 16, 1), AudioClientBufferFlags.Silent);

        AudioInputLevelSnapshot snapshot = meter.Read();
        Assert.Equal(0, snapshot.LevelPercent);
        Assert.Equal(-60, snapshot.LevelDb);
    }

    [Fact]
    public void PeakHoldAndDecay_UseDeterministicClock()
    {
        long timestamp = 0;
        var meter = new AudioInputLevelMeter(() => timestamp, 1_000);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        meter.Process(FloatBytes(1), format, AudioClientBufferFlags.None);
        timestamp = 749;
        meter.Process(FloatBytes(0.01f), format, AudioClientBufferFlags.None);
        Assert.Equal(100, meter.Read().PeakPercent);

        timestamp = 1_750;
        meter.Process(FloatBytes(0.01f), format, AudioClientBufferFlags.None);
        Assert.InRange(meter.Read().PeakPercent, 69.9, 70.1);
    }

    [Fact]
    public void LevelEnvelope_DecaysAcrossSilenceInsteadOfSnappingToZero()
    {
        long timestamp = 0;
        var meter = new AudioInputLevelMeter(() => timestamp, 1_000);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        meter.Process(FloatBytes(0.5f), format, AudioClientBufferFlags.None);
        double initialLevel = meter.Read().LevelPercent;
        timestamp = 10;
        meter.Process(FloatBytes(0), format, AudioClientBufferFlags.Silent);
        double firstSilentPacketLevel = meter.Read().LevelPercent;
        timestamp = 500;
        meter.Process(FloatBytes(0), format, AudioClientBufferFlags.Silent);

        Assert.InRange(initialLevel, 89.8, 90.1);
        Assert.InRange(firstSilentPacketLevel, 80, initialLevel);
        Assert.InRange(meter.Read().LevelPercent, 0, firstSilentPacketLevel - 20);
    }

    [Fact]
    public void LevelEnvelope_UsesFastAttackForNewActivity()
    {
        long timestamp = 0;
        var meter = new AudioInputLevelMeter(() => timestamp, 1_000);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        meter.Process(FloatBytes(0), format, AudioClientBufferFlags.Silent);
        timestamp = 10;
        meter.Process(FloatBytes(0.5f), format, AudioClientBufferFlags.None);
        double firstActivePacketLevel = meter.Read().LevelPercent;
        timestamp = 40;
        meter.Process(FloatBytes(0.5f), format, AudioClientBufferFlags.None);

        Assert.InRange(firstActivePacketLevel, 10, 40);
        Assert.True(meter.Read().LevelPercent > firstActivePacketLevel);
    }

    private static byte[] CreateHalfScalePcm(int bits)
    {
        byte[] bytes = new byte[bits / 8];
        switch (bits)
        {
            case 16:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, 16_384);
                break;
            case 24:
                bytes[2] = 0x40;
                break;
            case 32:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 1_073_741_824);
                break;
        }
        return bytes;
    }

    private static byte[] FloatBytes(float value) => BitConverter.GetBytes(value);
}
