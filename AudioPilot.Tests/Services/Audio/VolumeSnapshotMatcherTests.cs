using System.Collections.Concurrent;
using AudioPilot.Logging;

namespace AudioPilot.Tests.Services.Audio;

public sealed class VolumeSnapshotMatcherTests
{
    [Theory]
    [InlineData("Shared Beta", 75f)]
    [InlineData("Shared Alpha", 25f)]
    [InlineData("Shared Missing", 25f)]
    [InlineData("Shared Shared", 25f)]
    [InlineData("  shared  AUDIO player  ", 25f)]
    [InlineData("Shared Audio Player Beta", 75f)]
    [InlineData("Shared", null)]
    [InlineData("Alpha Other", null)]
    [InlineData("Unrelated", null)]
    [InlineData("   ", null)]
    public void FindFuzzyMatch_PreservesWordIntersectionAndRatio(string input, float? expected)
    {
        using var logger = Logger.CreateInMemoryForTests();
        var matcher = new VolumeSnapshotMatcher(logger, new ConcurrentDictionary<string, string>(), [], 128, TimeSpan.FromSeconds(30));
        SessionVolumeSnapshot snapshot = CreateSnapshot();

        Assert.Equal(expected, matcher.FindFuzzyMatch(input.AsSpan(), snapshot));
        Assert.Equal(75f, matcher.FindFuzzyMatch("Shared Beta".AsSpan(), snapshot));
    }

    [Fact]
    public void FindFuzzyMatch_SmallerIndexDoesNotChangeAmbiguousMatchOrder()
    {
        using var logger = Logger.CreateInMemoryForTests();
        var matcher = new VolumeSnapshotMatcher(logger, new ConcurrentDictionary<string, string>(), [], 128, TimeSpan.FromSeconds(30));
        SessionVolumeSnapshot snapshot = CreateSnapshot();
        snapshot.WordIndex["Shared"].Add("Shared Extra Entry");
        snapshot.WordIndex["Audio"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shared Audio Player Beta",
            "Shared Audio Player Alpha",
        };

        Assert.Equal(25f, matcher.FindFuzzyMatch("Shared Audio".AsSpan(), snapshot));
    }

    [Fact]
    public void FindFuzzyMatch_DeduplicatesInputWordsWithCaseSensitiveIndex()
    {
        using var logger = Logger.CreateInMemoryForTests();
        var matcher = new VolumeSnapshotMatcher(logger, new ConcurrentDictionary<string, string>(), [], 128, TimeSpan.FromSeconds(30));
        SessionVolumeSnapshot snapshot = CreateSnapshot(StringComparer.Ordinal);
        snapshot.WordIndex["shared"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Other Voice Tool" };

        Assert.Equal(25f, matcher.FindFuzzyMatch("Shared shared".AsSpan(), snapshot));
    }

    private static SessionVolumeSnapshot CreateSnapshot(StringComparer? comparer = null)
    {
        var snapshot = new SessionVolumeSnapshot
        {
            WordIndex = new Dictionary<string, HashSet<string>>(comparer ?? StringComparer.OrdinalIgnoreCase),
            ByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Shared Audio Player Alpha"] = 25f,
                ["Shared Audio Player Beta"] = 75f,
                ["Other Voice Tool"] = 90f,
            },
        };
        foreach (string name in snapshot.ByName.Keys)
        {
            VolumeSnapshotMatcher.IndexNormalizedWords(name, snapshot.WordIndex);
        }
        return snapshot;
    }
}
