using System.Buffers.Binary;
using AudioPilot.Services.Audio.Metering;
using AudioPilot.Services.Audio.Testing;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioInputLevelMeterTests
{
    [Theory]
    [InlineData(WaveFormatEncoding.Adpcm, 16)]
    [InlineData(WaveFormatEncoding.IeeeFloat, 16)]
    [InlineData(WaveFormatEncoding.Extensible, 32)]
    public void UnsupportedEncoding_IsRejectedInsteadOfInterpretedAsPcm(WaveFormatEncoding encoding, int bits)
    {
        WaveFormat format = WaveFormat.CreateCustomFormat(encoding, 48_000, 1, 48_000 * bits / 8, bits / 8, bits);
        AudioEndpointTestException failure = Assert.Throws<AudioEndpointTestException>(() =>
            AudioInputLevelMeter.CalculateLinearLevels(new byte[format.BlockAlign], format));
        Assert.Equal(AudioEndpointTestFailureKind.UnsupportedFormat, failure.FailureKind);
    }

    [Fact]
    public void UnknownExtensibleSubtype_IsRejectedInsteadOfInterpretedAsPcm()
    {
        var format = new WaveFormatExtensible(48_000, 32, 1, Guid.Empty, 32, 0);
        AudioEndpointTestException failure = Assert.Throws<AudioEndpointTestException>(() =>
            AudioInputLevelMeter.CalculateLinearLevels(new byte[format.BlockAlign], format));
        Assert.Equal(AudioEndpointTestFailureKind.UnsupportedFormat, failure.FailureKind);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    [InlineData(32, 24)]
    [InlineData(32, 32)]
    public void ExtensiblePcm_UsesContainerWidthForLeftAlignedValidBits(int bits, int validBits)
    {
        var format = new WaveFormatExtensible(192_000, bits, 1, AudioMediaSubtypes.MEDIASUBTYPE_PCM, validBits, 0);
        (double rms, double peak) = AudioInputLevelMeter.CalculateLinearLevels(CreateHalfScalePcm(bits), format);
        Assert.InRange(rms, 0.499, 0.501);
        Assert.InRange(peak, 0.499, 0.501);
    }

    [Fact]
    public void ExtensibleFloat_UsesFloatSubtypeForMultichannelPackets()
    {
        var format = new WaveFormatExtensible(192_000, 32, 8, AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT, 32, 0);
        byte[] packet = new byte[format.BlockAlign * 1_920];
        for (int offset = 0; offset < packet.Length; offset += sizeof(float))
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset), BitConverter.SingleToInt32Bits(0.5f));
        (double rms, double peak) = AudioInputLevelMeter.CalculateLinearLevels(packet, format);
        Assert.Equal(0.5, rms);
        Assert.Equal(0.5, peak);
    }

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
        timestamp = 99;
        meter.Process(FloatBytes(0.01f), format, AudioClientBufferFlags.None);
        Assert.Equal(100, meter.Read().PeakPercent);

        timestamp = 433;
        meter.Process(FloatBytes(0.01f), format, AudioClientBufferFlags.None);
        Assert.InRange(meter.Read().PeakPercent, 50.0, 50.1);
    }

    [Fact]
    public void PeakEnvelope_DecayIgnoresDisplayCadenceAndResetDropsTheOldPeak()
    {
        var frequent = new AudioPeakEnvelope(1_000);
        var delayed = new AudioPeakEnvelope(1_000);
        Assert.Equal(0, frequent.Update(0, 0));
        Assert.Equal(0, delayed.Update(0, 0));
        for (int now = 16; now < 500; now += 16) frequent.Update(-60, now);
        Assert.Equal(-36, frequent.Update(-60, 500), 8);
        Assert.Equal(-36, delayed.Update(-60, 500), 8);
        delayed.Reset();
        Assert.Equal(-60, delayed.Update(-60, 501));
        Assert.Equal(-6, delayed.Update(-6, 502));
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

        Assert.InRange(firstActivePacketLevel, 60, 75);
        Assert.InRange(meter.Read().LevelPercent, 85, 91);
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
