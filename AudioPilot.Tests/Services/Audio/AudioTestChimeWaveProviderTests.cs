using System.Runtime.InteropServices;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioTestChimeWaveProviderTests
{
    [Fact]
    public void StereoChime_HasFiniteExpectedDurationAndBoundedSamples()
    {
        var provider = new AudioTestChimeWaveProvider(stereo: true);
        byte[] audio = ReadAll(provider);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(audio);

        Assert.Equal((int)(AudioTestChimeWaveProvider.SampleRate * AudioTestChimeWaveProvider.DurationSeconds) * 2, samples.Length);
        Assert.All(samples.ToArray(), sample => Assert.InRange(sample, -0.341f, 0.341f));
        Assert.Equal(0, provider.Read(new byte[provider.WaveFormat.BlockAlign], 0, provider.WaveFormat.BlockAlign));
    }

    [Fact]
    public void StereoChime_SequencesLeftRightThenBoth()
    {
        var provider = new AudioTestChimeWaveProvider(stereo: true);
        byte[] audio = ReadAll(provider);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(audio);

        (double left, double right) = SegmentEnergy(samples, 0.10, 0.60);
        (double left, double right) second = SegmentEnergy(samples, 0.94, 1.44);
        (double left, double right) third = SegmentEnergy(samples, 1.78, 2.28);

        Assert.True(left > 1 && right == 0);
        Assert.True(second.right > 1 && second.left == 0);
        Assert.True(third.left > 1 && third.right > 1);
        Assert.InRange(third.left / third.right, 0.999, 1.001);
    }

    [Fact]
    public void SegmentBoundaries_AreSilentToPreventClicks()
    {
        var provider = new AudioTestChimeWaveProvider(stereo: true);
        byte[] audio = ReadAll(provider);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(audio);

        foreach (double time in new[] { 0.0, 0.72, 0.84, 1.56, 1.68 })
        {
            int sample = (int)(time * AudioTestChimeWaveProvider.SampleRate) * 2;
            Assert.InRange(Math.Abs(samples[sample]), 0, 0.00001f);
            Assert.InRange(Math.Abs(samples[sample + 1]), 0, 0.00001f);
        }
    }

    private static byte[] ReadAll(AudioTestChimeWaveProvider provider)
    {
        var result = new List<byte>();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
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
