using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Services.Audio.Testing;

internal sealed class AudioInputLevelMeter
{
    private const double FloorDb = -60;
    private const double AttackSeconds = 0.035;
    private const double ReleaseSeconds = 0.22;
    private readonly Func<long> _getTimestamp;
    private readonly long _timestampFrequency;
    private readonly long _peakHoldTicks;
    private readonly double _peakDecayDbPerTick;
    private readonly Lock _lock = new();
    private AudioInputLevelSnapshot _snapshot = AudioInputLevelSnapshot.Silence;
    private double _smoothedLevelDb = FloorDb;
    private double _heldPeakDb = FloorDb;
    private long _levelTimestamp;
    private long _peakTimestamp;
    private long _revision;

    public AudioInputLevelMeter()
        : this(Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    internal AudioInputLevelMeter(Func<long> getTimestamp, long timestampFrequency)
    {
        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _timestampFrequency = timestampFrequency;
        _peakHoldTicks = (long)(timestampFrequency * 0.75);
        _peakDecayDbPerTick = 18.0 / timestampFrequency;
    }

    public void Process(ReadOnlySpan<byte> buffer, WaveFormat format, AudioClientBufferFlags flags)
    {
        (double rms, double peak) = flags.HasFlag(AudioClientBufferFlags.Silent)
            ? (0, 0)
            : CalculateLinearLevels(buffer, format);

        double levelDb = LinearToDb(rms);
        double peakDb = LinearToDb(peak);
        long now = _getTimestamp();

        lock (_lock)
        {
            if (_revision == 0)
            {
                _smoothedLevelDb = levelDb;
            }
            else
            {
                double elapsedSeconds = Math.Max(0, now - _levelTimestamp) / (double)_timestampFrequency;
                double timeConstant = levelDb >= _smoothedLevelDb ? AttackSeconds : ReleaseSeconds;
                double blend = elapsedSeconds <= 0 ? 1 : 1 - Math.Exp(-Math.Min(elapsedSeconds, 1) / timeConstant);
                _smoothedLevelDb += (levelDb - _smoothedLevelDb) * blend;
            }
            _levelTimestamp = now;

            if (peakDb >= _heldPeakDb)
            {
                _heldPeakDb = peakDb;
                _peakTimestamp = now;
            }
            else if (now - _peakTimestamp > _peakHoldTicks)
            {
                _heldPeakDb = Math.Max(peakDb, _heldPeakDb - ((now - _peakTimestamp - _peakHoldTicks) * _peakDecayDbPerTick));
                _peakTimestamp = now - _peakHoldTicks;
            }

            _snapshot = new AudioInputLevelSnapshot(
                DbToPercent(_smoothedLevelDb),
                DbToPercent(_heldPeakDb),
                _smoothedLevelDb,
                ++_revision);
        }
    }

    public AudioInputLevelSnapshot Read()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }

    internal static (double Rms, double Peak) CalculateLinearLevels(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        int sampleCount = buffer.Length / bytesPerSample;
        if (sampleCount == 0)
        {
            return (0, 0);
        }

        double sumSquares = 0;
        double peak = 0;
        int validSamples = 0;

        for (int offset = 0; offset + bytesPerSample <= buffer.Length; offset += bytesPerSample)
        {
            double sample = ReadNormalizedSample(buffer.Slice(offset, bytesPerSample), format);
            if (!double.IsFinite(sample))
            {
                continue;
            }

            sample = Math.Clamp(sample, -1, 1);
            double absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sumSquares += sample * sample;
            validSamples++;
        }

        return validSamples == 0
            ? (0, 0)
            : (Math.Sqrt(sumSquares / validSamples), peak);
    }

    private static double ReadNormalizedSample(ReadOnlySpan<byte> sample, WaveFormat format)
    {
        bool ieeeFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

        if (ieeeFloat && format.BitsPerSample == 32)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
        }

        return format.BitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
            24 => ReadInt24(sample) / 8388608.0,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0,
            _ => throw new AudioEndpointTestException(
                AudioEndpointTestFailureKind.UnsupportedFormat,
                $"The microphone format ({format.BitsPerSample}-bit {format.Encoding}) is not supported for metering."),
        };
    }

    private static int ReadInt24(ReadOnlySpan<byte> sample)
    {
        int value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x00800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static double LinearToDb(double value) =>
        value <= 0 ? FloorDb : Math.Clamp(20 * Math.Log10(value), FloorDb, 0);

    private static double DbToPercent(double value) =>
        Math.Clamp(((value - FloorDb) / -FloorDb) * 100, 0, 100);
}
