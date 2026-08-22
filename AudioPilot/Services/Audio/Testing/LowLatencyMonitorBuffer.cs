using NAudio.Utils;
using NAudio.Wave;

namespace AudioPilot.Services.Audio.Testing;

internal sealed class LowLatencyMonitorBuffer : IWaveProvider
{
    private readonly CircularBuffer _buffer;
    private readonly Lock _lock = new();
    private readonly int _blockAlign;
    private long _droppedBytes;

    public LowLatencyMonitorBuffer(WaveFormat waveFormat, TimeSpan capacity)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);
        WaveFormat = waveFormat;
        _blockAlign = Math.Max(1, waveFormat.BlockAlign);
        int requestedCapacity = Math.Max(_blockAlign, waveFormat.ConvertLatencyToByteSize((int)Math.Ceiling(capacity.TotalMilliseconds)));
        CapacityBytes = requestedCapacity - (requestedCapacity % _blockAlign);
        _buffer = new CircularBuffer(CapacityBytes);
    }

    public WaveFormat WaveFormat { get; }

    internal int CapacityBytes { get; }

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

    public void AddSamples(ReadOnlySpan<byte> source)
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

            _buffer.Write(source);
        }
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> destination)
    {
        int read;
        lock (_lock)
        {
            read = _buffer.Read(destination);
        }

        destination[read..].Clear();
        return destination.Length;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Reset();
        }
    }
}
