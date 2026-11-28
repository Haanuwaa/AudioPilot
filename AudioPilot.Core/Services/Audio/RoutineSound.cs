using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPilot.Services.Audio;

internal sealed class RoutineSoundWaveProvider : IWaveProvider
{
    private const int SampleRate = 48_000;
    private const int FirstNoteFrames = SampleRate * 62 / 1_000;
    private const int SecondNoteStart = SampleRate * 74 / 1_000;
    private const int SecondNoteFrames = SampleRate * 64 / 1_000;
    private const int ThirdNoteStart = SampleRate * 151 / 1_000;
    private const int ThirdNoteFrames = SampleRate * 104 / 1_000;
    private const int AttackFrames = SampleRate * 5 / 1_000;
    private const int ReleaseFrames = SampleRate * 12 / 1_000;
    private const int TotalFrames = ThirdNoteStart + ThirdNoteFrames;
    private const float Amplitude = 0.075f;
    private int _position;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    public int Read(Span<byte> buffer)
    {
        Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
        int count = Math.Min(samples.Length, TotalFrames - _position);
        for (int index = 0; index < count; index++, _position++)
        {
            // E5, B5, G-sharp5. The final note rings a little longer.
            samples[index] = _position switch
            {
                < SecondNoteStart => RenderNote(_position, FirstNoteFrames, 659.255),
                < ThirdNoteStart => RenderNote(_position - SecondNoteStart, SecondNoteFrames, 987.767),
                _ => RenderNote(_position - ThirdNoteStart, ThirdNoteFrames, 830.609),
            };
        }
        return count * sizeof(float);
    }

    private static float RenderNote(int frame, int length, double frequency)
    {
        if (frame >= length) return 0;

        double progress = frame / (double)length;
        double attack = Math.Min(1, frame / (double)AttackFrames);
        double release = Math.Min(1, (length - frame - 1) / (double)ReleaseFrames);
        double phase = Math.Tau * frequency * frame / SampleRate;
        double tone = Math.Sin(phase) + 0.15 * Math.Sin(2 * phase) * Math.Exp(-4 * progress);
        return (float)(tone * attack * release * Math.Exp(-2.2 * progress) * Amplitude);
    }
}

internal static class RoutineSoundPlayer
{
    internal static Task PlayAsync(Func<bool> canPlay, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (!canPlay()) return;
            ComThreadingHelper.ThrowIfComInitializationFailed(nameof(PlayAsync));
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render) return;

            AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;
            if (endpointVolume.Mute || endpointVolume.MasterVolumeLevelScalar <= 0 || !canPlay()) return;

            await using var player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode()
                .WithEventSync().WithLatency(50).WithCategory(AudioStreamCategory.Media).Build();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is { } error) completion.TrySetException(error);
                else completion.TrySetResult();
            };
            player.Init(new RoutineSoundWaveProvider());
            cancellationToken.ThrowIfCancellationRequested();
            if (!canPlay() || endpointVolume.Mute) return;
            player.Play();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }, cancellationToken);
}
