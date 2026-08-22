using NAudio.Wave;

namespace AudioPilot.Services.Audio.Testing;

internal sealed class AudioTestChimeWaveProvider : IWaveProvider
{
    internal const int SampleRate = 48_000;
    internal const double DurationSeconds = 1.6;
    private const double AttackSeconds = 0.008;
    private const double ReleaseSeconds = 0.04;
    private const float Amplitude = 0.34f;
    private static readonly (double Start, double Duration, double Frequency)[] Notes =
    [
        (0.00, 0.30, 523.25),
        (0.34, 0.30, 659.25),
        (0.68, 0.16, 783.99),
        (0.88, 0.72, 1046.50),
    ];

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

        for (int noteIndex = 0; noteIndex < Notes.Length; noteIndex++)
        {
            var (start, duration, frequency) = Notes[noteIndex];
            double localTime = time - start;
            if (localTime < 0 || localTime >= duration)
            {
                continue;
            }

            double attack = Math.Min(localTime / AttackSeconds, 1);
            double release = Math.Min((duration - localTime) / ReleaseSeconds, 1);
            double fade = 0.5 - (0.5 * Math.Cos(Math.PI * Math.Min(attack, release)));
            double decay = Math.Exp(-5 * localTime / duration);
            double phase = 2 * Math.PI * frequency * localTime;
            double tone = (0.80 * Math.Sin(phase))
                + (0.16 * Math.Sin(2 * phase) * decay)
                + (0.04 * Math.Sin(3 * phase) * decay);
            float sample = (float)(tone * Amplitude * fade * decay);

            if (_channels == 1 || noteIndex != 1) left = sample;
            if (_channels == 2 && noteIndex != 0) right = sample;
            return;
        }
    }
}
