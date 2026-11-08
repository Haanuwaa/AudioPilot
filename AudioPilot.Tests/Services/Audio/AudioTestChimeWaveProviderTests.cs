using System.Runtime.InteropServices;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioTestChimeWaveProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chime_HasFiniteExpectedDurationAndBoundedSamples(bool stereo)
    {
        var provider = new AudioTestChimeWaveProvider(stereo);
        byte[] audio = ReadAll(provider);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(audio);

        Assert.InRange(AudioTestChimeWaveProvider.DurationSeconds, 1, 2);
        Assert.Equal((int)(AudioTestChimeWaveProvider.SampleRate * AudioTestChimeWaveProvider.DurationSeconds) * provider.WaveFormat.Channels, samples.Length);
        Assert.All(samples.ToArray(), sample => Assert.InRange(sample, -0.341f, 0.341f));
        Assert.Equal(0, provider.Read(new byte[provider.WaveFormat.BlockAlign]));
    }

    [Fact]
    public void StereoChime_SequencesLeftRightThenBoth()
    {
        var provider = new AudioTestChimeWaveProvider(stereo: true);
        byte[] audio = ReadAll(provider);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(audio);

        (double left, double right) = SegmentEnergy(samples, 0.02, 0.28);
        (double left, double right) second = SegmentEnergy(samples, 0.36, 0.62);
        (double left, double right) third = SegmentEnergy(samples, 0.70, 0.82);
        (double left, double right) fourth = SegmentEnergy(samples, 0.90, 1.58);

        Assert.True(left > 1 && right == 0);
        Assert.True(second.right > 1 && second.left == 0);
        Assert.True(third.left > 1 && third.right > 1);
        Assert.InRange(third.left / third.right, 0.999, 1.001);
        Assert.True(fourth.left > 1 && fourth.right > 1);
        Assert.InRange(fourth.left / fourth.right, 0.999, 1.001);
    }

    [Fact]
    public void SegmentBoundaries_AreSilentToPreventClicks()
    {
        var provider = new AudioTestChimeWaveProvider(stereo: true);
        byte[] audio = ReadAll(provider);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(audio);

        foreach (double time in new[] { 0.0, 0.30, 0.34, 0.64, 0.68, 0.84, 0.88 })
        {
            int sample = (int)(time * AudioTestChimeWaveProvider.SampleRate) * 2;
            Assert.InRange(Math.Abs(samples[sample]), 0, 0.00001f);
            Assert.InRange(Math.Abs(samples[sample + 1]), 0, 0.00001f);
        }

        Assert.InRange(Math.Abs(samples[^1]), 0, 0.00001f);
        Assert.InRange(Math.Abs(samples[^2]), 0, 0.00001f);
    }

    [Fact]
    public void Chime_IsIndependentOfReadBufferSize()
    {
        byte[] normal = ReadAll(new AudioTestChimeWaveProvider(stereo: true));
        byte[] small = ReadAll(new AudioTestChimeWaveProvider(stereo: true), bufferSize: 60);

        Assert.Equal(normal, small);
    }

    private static byte[] ReadAll(AudioTestChimeWaveProvider provider, int bufferSize = 4096)
    {
        var result = new List<byte>();
        byte[] buffer = new byte[bufferSize];
        int read;
        while ((read = provider.Read(buffer)) > 0)
        {
            result.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return [.. result];
    }

    private static (double Left, double Right) SegmentEnergy(
        ReadOnlySpan<float> samples,
        double startSeconds,
        double endSeconds)
    {
        int startFrame = (int)(startSeconds * AudioTestChimeWaveProvider.SampleRate);
        int endFrame = (int)(endSeconds * AudioTestChimeWaveProvider.SampleRate);
        double left = 0;
        double right = 0;
        for (int frame = startFrame; frame < endFrame; frame++)
        {
            left += Math.Abs(samples[frame * 2]);
            right += Math.Abs(samples[(frame * 2) + 1]);
        }

        return (left, right);
    }
}
