using NAudio.Wave;

namespace AudioPilot.Services.Audio.Testing;

internal sealed class AudioTestChimeWaveProvider : IWaveProvider
{
    internal const int SampleRate = 48_000;
    internal const double DurationSeconds = 2.4;
    private const double SegmentSeconds = 0.72;
    private const double GapSeconds = 0.12;
    private const double FadeSeconds = 0.025;
    private const float Amplitude = 0.34f;
    private static readonly double[] Frequencies = [440.0, 554.37, 659.25];

    private readonly int _channels;
    private readonly long _totalFrames = (long)(SampleRate * DurationSeconds);
    private long _positionFrames;

    public AudioTestChimeWaveProvider(bool stereo)
    {
        _channels = stereo ? 2 : 1;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, _channels);
    }

    public WaveFormat WaveFormat { get; }

    internal long TotalFrames => _totalFrames;

    public int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> buffer)
    {
        int requestedFrames = buffer.Length / WaveFormat.BlockAlign;
        int availableFrames = (int)Math.Min(requestedFrames, _totalFrames - _positionFrames);
        Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            buffer[..(availableFrames * WaveFormat.BlockAlign)]);

        int sampleOffset = 0;
        for (int frame = 0; frame < availableFrames; frame++)
        {
            long absoluteFrame = _positionFrames + frame;
            double time = absoluteFrame / (double)SampleRate;
            GetFrame(time, out float left, out float right);

            samples[sampleOffset++] = left;
            if (_channels == 2)
            {
                samples[sampleOffset++] = right;
            }
        }

        _positionFrames += availableFrames;
        return availableFrames * WaveFormat.BlockAlign;
    }

    private void GetFrame(double time, out float left, out float right)
    {
        left = 0;
        right = 0;

        double cycleLength = SegmentSeconds + GapSeconds;
        int segment = (int)(time / cycleLength);
        if (segment is < 0 or > 2)
        {
            return;
        }

        double localTime = time - (segment * cycleLength);
        if (localTime >= SegmentSeconds)
        {
            return;
        }

        double fadeIn = Math.Clamp(localTime / FadeSeconds, 0, 1);
        double fadeOut = Math.Clamp((SegmentSeconds - localTime) / FadeSeconds, 0, 1);
        double fade = 0.5 - (0.5 * Math.Cos(Math.PI * Math.Min(fadeIn, fadeOut)));
        float sample = (float)(Math.Sin(2 * Math.PI * Frequencies[segment] * localTime) * Amplitude * fade);

        if (_channels == 1)
        {
            left = sample;
            return;
        }

        switch (segment)
        {
            case 0:
                left = sample;
                break;
            case 1:
                right = sample;
                break;
            default:
                left = sample;
                right = sample;
                break;
        }
    }
}
