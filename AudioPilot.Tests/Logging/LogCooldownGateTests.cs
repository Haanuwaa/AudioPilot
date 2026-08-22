using AudioPilot.Logging;

namespace AudioPilot.Tests.Logging;

public sealed class LogCooldownGateTests
{
    [Fact]
    public void Constructor_RejectsNegativeCooldown()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogCooldownGate(-1));
    }

    [Fact]
    public void TryEnter_AdmitsFirstCallAndNextCallAtBoundary()
    {
        long nowTick = 1_000;
        var gate = new LogCooldownGate(100, () => nowTick);

        Assert.True(gate.TryEnter("query"));

        nowTick = 1_099;
        Assert.False(gate.TryEnter("query"));

        nowTick = 1_100;
        Assert.True(gate.TryEnter("query"));
    }

    [Fact]
    public void TryEnter_TracksKeysIndependentlyAndClearReleasesState()
    {
        var gate = new LogCooldownGate(100, static () => 1_000);

        Assert.True(gate.TryEnter("playback"));
        Assert.True(gate.TryEnter("capture"));

        gate.Clear();

        Assert.True(gate.TryEnter("playback"));
    }

    [Fact]
    public void TryEnter_AdmitsOnlyOneConcurrentCallerPerKey()
    {
        var gate = new LogCooldownGate(100, static () => 1_000);
        int admitted = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.TryEnter("query"))
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.Equal(1, admitted);
    }

    [Fact]
    public void TryEnter_RecoversIfInjectedClockMovesBackward()
    {
        long nowTick = 1_000;
        var gate = new LogCooldownGate(100, () => nowTick);
        Assert.True(gate.TryEnter("query"));

        nowTick = 900;

        Assert.True(gate.TryEnter("query"));
    }
}
