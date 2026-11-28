using System.Buffers.Binary;
using AudioPilot.Services.Audio.Testing;
using BenchmarkDotNet.Attributes;
using NAudio.Wave;

namespace AudioPilot.Benchmarks;

[MemoryDiagnoser]
public class AudioDspBenchmarks
{
    public enum SampleFormat { Float32, Pcm16 }

    [Params(SampleFormat.Float32, SampleFormat.Pcm16)]
    public SampleFormat Format { get; set; }

    private WaveFormat _waveFormat = null!;
    private byte[] _inputFrame = null!;
    private byte[] _outputFrame = null!;
    private LowLatencyMonitorBuffer _monitorBuffer = null!;

    [GlobalSetup]
    public void SetUp()
    {
        const int sampleRate = 48_000;
        const int channels = 2;
        const int framesPerPacket = 480; // 10 ms at 48 kHz
        int totalSamples = framesPerPacket * channels;

        if (Format == SampleFormat.Float32)
        {
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _inputFrame = new byte[totalSamples * 4];
            for (int i = 0; i < totalSamples; i++)
            {
                float value = MathF.Sin(i * 0.08f) * 0.5f;
                BinaryPrimitives.WriteInt32LittleEndian(_inputFrame.AsSpan(i * 4, 4), BitConverter.SingleToInt32Bits(value));
            }
        }
        else
        {
            _waveFormat = new WaveFormat(sampleRate, 16, channels);
            _inputFrame = new byte[totalSamples * 2];
            for (int i = 0; i < totalSamples; i++)
            {
                short value = (short)(Math.Sin(i * 0.08) * 16_384);
                BinaryPrimitives.WriteInt16LittleEndian(_inputFrame.AsSpan(i * 2, 2), value);
            }
        }

        _outputFrame = new byte[_inputFrame.Length];
        _monitorBuffer = new LowLatencyMonitorBuffer(_waveFormat, TimeSpan.FromMilliseconds(50), gain: 0.75f);
    }

    [Benchmark]
    public double CalculateInputLevels()
    {
        (double rms, double peak) = AudioInputLevelMeter.CalculateLinearLevels(_inputFrame, _waveFormat);
        return rms + peak;
    }

    [Benchmark]
    public int MonitorRoundTripWithGain()
    {
        _monitorBuffer.AddSamples(_inputFrame);
        return _monitorBuffer.Read(_outputFrame);
    }

    internal static void ValidateCases()
    {
        foreach (SampleFormat format in Enum.GetValues<SampleFormat>())
        {
            var benchmark = new AudioDspBenchmarks { Format = format };
            benchmark.SetUp();
            double levels = benchmark.CalculateInputLevels();
            int bytesRead = benchmark.MonitorRoundTripWithGain();
            if (levels <= 0 || bytesRead != benchmark._outputFrame.Length)
                throw new InvalidOperationException($"Audio DSP benchmark fixture validation failed for {format}.");
        }
    }
}
