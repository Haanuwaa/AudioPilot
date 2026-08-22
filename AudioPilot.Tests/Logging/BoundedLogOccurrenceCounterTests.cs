using AudioPilot.Logging;

namespace AudioPilot.Tests.Logging;

public sealed class BoundedLogOccurrenceCounterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLogOccurrenceCounter(capacity));
    }

    [Fact]
    public void Increment_TracksKeysIndependentlyAndIgnoresCase()
    {
        BoundedLogOccurrenceCounter counter = new(4);

        Assert.Equal(1, counter.Increment("output:operation"));
        Assert.Equal(2, counter.Increment("OUTPUT:OPERATION"));
        Assert.Equal(1, counter.Increment("input:operation"));
    }

    [Fact]
    public void Increment_EvictsLeastRecentlyUsedKeyAtCapacity()
    {
        BoundedLogOccurrenceCounter counter = new(2);

        Assert.Equal(1, counter.Increment("oldest"));
        Assert.Equal(1, counter.Increment("retained"));
        Assert.Equal(2, counter.Increment("retained"));
        Assert.Equal(1, counter.Increment("new"));

        Assert.Equal(3, counter.Increment("retained"));
        Assert.Equal(1, counter.Increment("oldest"));
    }

    [Fact]
    public void RemoveAndClear_ResetCounts()
    {
        BoundedLogOccurrenceCounter counter = new(2);

        Assert.Equal(1, counter.Increment("one"));
        Assert.Equal(2, counter.Increment("one"));
        counter.Remove("one");
        Assert.Equal(1, counter.Increment("one"));

        counter.Increment("two");
        counter.Clear();

        Assert.Equal(1, counter.Increment("one"));
        Assert.Equal(1, counter.Increment("two"));
    }

    [Fact]
    public void Increment_IsAtomicAcrossConcurrentCallers()
    {
        BoundedLogOccurrenceCounter counter = new(2);

        Parallel.For(0, 100, _ => counter.Increment("operation"));

        Assert.Equal(101, counter.Increment("operation"));
    }
}
