using AudioPilot.Models;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionNameDisambiguatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotCollector_PreservesExistingNamesAndGeneratesGloballyUniqueLabels(bool reverseOrder)
    {
        var snapshots = new List<AudioSessionSnapshot>
        {
            new("Player", 25, "Output", "player", null, 101, false),
            new("Player", 25, "Output", "player", null, 102, false),
            new("Player (2)", 25, "Output", "another", null, 103, false),
            new("Player (3)", 25, "Output", "fourth", null, 104, false),
        };
        if (reverseOrder) snapshots.Reverse();
        AudioSessionSnapshotCollector.DeduplicateSnapshotNames(snapshots);

        Assert.Equal(snapshots.Count, snapshots.Select(row => row.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Player (2)", snapshots.Single(row => row.ProcessId == 103).DisplayName);
        Assert.Equal("Player (3)", snapshots.Single(row => row.ProcessId == 104).DisplayName);
        Assert.Contains(snapshots, row => row.DisplayName == "Player (4)");
    }

    [Fact]
    public void GeneratedProcessAndTitleLabels_DoNotStealExistingLabelsOrCollideByCase()
    {
        (string Name, string? Process, string? Title)[] rows =
        [
            ("Player", "alpha", null),
            ("Player", "beta", null),
            ("Player (alpha)", "original", null),
            ("Viewer", "viewer", "Document"),
            ("Viewer", "viewer", "document"),
            ("Viewer (Document) (2)", "reserved", null),
            ("Unchanged", "unique", null),
        ];

        string[] names = AudioSessionNameDisambiguator.GetUniqueNames(rows,
            static row => row.Name, static row => row.Process, static row => row.Title);

        Assert.Equal(rows.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Player (alpha)", names[2]);
        Assert.Equal("Player (alpha) (2)", names[0]);
        Assert.Equal("Player (beta)", names[1]);
        Assert.Equal("Viewer (Document) (2)", names[5]);
        Assert.Equal("Unchanged", names[6]);
    }

    [Fact]
    public void AlreadyContainedProcessNames_StillReceiveUniqueLabels()
    {
        (string Name, string? Process, string? Title)[] rows =
            [("Audio Player", "audio", null), ("Audio Player", "player", null)];

        string[] names = AudioSessionNameDisambiguator.GetUniqueNames(rows,
            static row => row.Name, static row => row.Process, static row => row.Title);

        Assert.Equal(["Audio Player", "Audio Player (2)"], names);
    }
}
