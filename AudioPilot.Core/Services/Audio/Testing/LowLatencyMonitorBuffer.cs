using System.Buffers.Binary;
using NAudio.Utils;
using NAudio.Wave;

namespace AudioPilot.Services.Audio.Testing;

internal sealed class LowLatencyMonitorBuffer : IWaveProvider
{
    private readonly CircularBuffer _buffer;
    private readonly Lock _lock = new();
    private readonly int _blockAlign;
    private readonly bool _isIeeeFloat32;
    private readonly bool _isPcm;
    private readonly int _bitsPerSample;
    private float _gain;
    private long _droppedBytes;

    public LowLatencyMonitorBuffer(WaveFormat waveFormat, TimeSpan capacity, float gain = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);
        WaveFormat = waveFormat;
        _gain = NormalizeGain(gain);
        _blockAlign = Math.Max(1, waveFormat.BlockAlign);
        _bitsPerSample = waveFormat.BitsPerSample;
        _isIeeeFloat32 = _bitsPerSample == 32 && (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
            (waveFormat is WaveFormatExtensible { SubFormat: var floatSubtype } && floatSubtype == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT));
        _isPcm = _bitsPerSample is 16 or 24 or 32 && (waveFormat.Encoding == WaveFormatEncoding.Pcm ||
            (waveFormat is WaveFormatExtensible { SubFormat: var pcmSubtype } && pcmSubtype == AudioMediaSubtypes.MEDIASUBTYPE_PCM));
        int requestedCapacity = Math.Max(_blockAlign, waveFormat.ConvertLatencyToByteSize((int)Math.Ceiling(capacity.TotalMilliseconds)));
        CapacityBytes = requestedCapacity - (requestedCapacity % _blockAlign);
        _buffer = new CircularBuffer(CapacityBytes);
    }

    public WaveFormat WaveFormat { get; }

    internal int CapacityBytes { get; }

    internal float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, NormalizeGain(value));
    }

    private static float NormalizeGain(float gain) => float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 0f;

    internal int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count;
            }
        }
    }

    internal long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    public void AddSamples(ReadOnlySpan<byte> source, bool silent = false)
    {
        int alignedLength = source.Length - (source.Length % _blockAlign);
        if (alignedLength <= 0)
        {
            return;
        }

        source = source[..alignedLength];
        if (source.Length > CapacityBytes)
        {
            int skipped = source.Length - CapacityBytes;
            skipped -= skipped % _blockAlign;
            source = source[skipped..];
            Interlocked.Add(ref _droppedBytes, skipped);
        }

        lock (_lock)
        {
            int overflow = Math.Max(0, (_buffer.Count + source.Length) - CapacityBytes);
            overflow += (_blockAlign - (overflow % _blockAlign)) % _blockAlign;
            overflow = Math.Min(overflow, _buffer.Count - (_buffer.Count % _blockAlign));
            if (overflow > 0)
            {
                _buffer.Advance(overflow);
                Interlocked.Add(ref _droppedBytes, overflow);
            }

            if (silent)
            {
                Span<byte> silence = stackalloc byte[256];
                silence.Clear();
                for (int remaining = source.Length; remaining > 0; remaining -= silence.Length)
                {
                    _buffer.Write(silence[..Math.Min(remaining, silence.Length)]);
                }
            }
            else
            {
                _buffer.Write(source);
            }
        }
    }

    public int Read(Span<byte> destination)
    {
        int read;
        lock (_lock)
        {
            read = _buffer.Read(destination);
        }

        destination[read..].Clear();
        float gain = Volatile.Read(ref _gain);
        if (read > 0 && gain < 0.9999f)
        {
            ApplyGain(destination[..read], gain);
        }

        return destination.Length;
    }

    private void ApplyGain(Span<byte> bytes, float gain)
    {
        if (gain <= 0.0001f)
        {
            bytes.Clear();
            return;
        }

        if (_isIeeeFloat32)
        {
            for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
            {
                float sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset, 4), BitConverter.SingleToInt32Bits(sample * gain));
            }
            return;
        }

        if (!_isPcm)
        {
            return;
        }

        switch (_bitsPerSample)
        {
            case 16:
                for (int offset = 0; offset + 2 <= bytes.Length; offset += 2)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, 2));
                    short scaled = (short)Math.Clamp((int)MathF.Round(sample * gain), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(offset, 2), scaled);
                }
                break;
            case 24:
                for (int offset = 0; offset + 3 <= bytes.Length; offset += 3)
                {
                    int raw = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
                    if ((raw & 0x00800000) != 0) raw |= unchecked((int)0xFF000000);
                    int scaled = Math.Clamp((int)Math.Round(raw * (double)gain), -8_388_608, 8_388_607);
                    bytes[offset] = (byte)(scaled & 0xFF);
                    bytes[offset + 1] = (byte)((scaled >> 8) & 0xFF);
                    bytes[offset + 2] = (byte)((scaled >> 16) & 0xFF);
                }
                break;
            case 32:
                for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
                {
                    int sample = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
                    int scaled = (int)Math.Clamp((long)Math.Round(sample * (double)gain), int.MinValue, int.MaxValue);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset, 4), scaled);
                }
                break;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Reset();
        }
    }
}
