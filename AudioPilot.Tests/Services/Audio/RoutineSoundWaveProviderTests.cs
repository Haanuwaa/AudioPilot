using System.Runtime.InteropServices;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.Audio;

[Trait(TestCategories.Execution, TestCategories.Pure)]
public sealed class RoutineSoundWaveProviderTests
{
    [Fact]
    public void CueHasThreeQuietNotesAndEndsCleanly()
    {
        var provider = new RoutineSoundWaveProvider();
        int framesPerMillisecond = provider.WaveFormat.SampleRate / 1_000;
        var samples = new float[framesPerMillisecond * 300];
        int firstCount = provider.Read(MemoryMarshal.AsBytes(samples.AsSpan(0, 113)));
        int restCount = provider.Read(MemoryMarshal.AsBytes(samples.AsSpan(113)));
        int frames = (firstCount + restCount) / sizeof(float);

        Assert.Equal(113 * sizeof(float), firstCount);
        Assert.Equal(framesPerMillisecond * 255, frames);
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[frames - 1]);
        AssertNote(0, 62);
        AssertNote(74, 138);
        AssertNote(151, 255);
        Assert.DoesNotContain(samples[(62 * framesPerMillisecond)..(74 * framesPerMillisecond)], value => value != 0);
        Assert.DoesNotContain(samples[(138 * framesPerMillisecond)..(151 * framesPerMillisecond)], value => value != 0);
        Assert.Equal(0, provider.Read(MemoryMarshal.AsBytes(samples.AsSpan())));

        void AssertNote(int startMs, int endMs) =>
            Assert.InRange(samples[(startMs * framesPerMillisecond)..(endMs * framesPerMillisecond)].Max(MathF.Abs), 0.06f, 0.08f);
    }
}
