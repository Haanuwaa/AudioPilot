using AudioPilot.Models;
using AudioPilot.Services.Diagnostics;

namespace AudioPilot.Tests.Services.Diagnostics;

public sealed class ExecutionHistoryServiceTests
{
    [Fact]
    public void Record_EnforcesCapacityAndKeepsNewestEntriesFirst()
    {
        var service = new ExecutionHistoryService(capacity: 2);

        service.Record(CreateEntry("op-1", ExecutionHistoryKind.Media));
        service.Record(CreateEntry("op-2", ExecutionHistoryKind.Switch));
        service.Record(CreateEntry("op-3", ExecutionHistoryKind.Mute));

        IReadOnlyList<ExecutionHistoryEntry> entries = service.GetEntries();

        Assert.Equal(2, entries.Count);
        Assert.Equal("op-3", entries[0].OpId);
        Assert.Equal("op-2", entries[1].OpId);
    }

    [Fact]
    public void GetEntry_IsCaseInsensitive()
    {
        var service = new ExecutionHistoryService();
        service.Record(CreateEntry("Cli-Op-ABC", ExecutionHistoryKind.Routine));

        ExecutionHistoryEntry? entry = service.GetEntry("cli-op-abc");

        Assert.NotNull(entry);
        Assert.Equal("Cli-Op-ABC", entry!.OpId);
    }

    [Fact]
    public void GetEntries_FiltersByKindAndLimit()
    {
        var service = new ExecutionHistoryService();
        service.Record(CreateEntry("op-1", ExecutionHistoryKind.Media));
        service.Record(CreateEntry("op-2", ExecutionHistoryKind.Mute));
        service.Record(CreateEntry("op-3", ExecutionHistoryKind.Mute));

        IReadOnlyList<ExecutionHistoryEntry> entries = service.GetEntries(limit: 1, kind: ExecutionHistoryKind.Mute);

        ExecutionHistoryEntry entry = Assert.Single(entries);
        Assert.Equal("op-3", entry.OpId);
        Assert.Equal(ExecutionHistoryKind.Mute, entry.Kind);
    }

    private static ExecutionHistoryEntry CreateEntry(string opId, ExecutionHistoryKind kind)
    {
        return new ExecutionHistoryEntry(
            OpId: opId,
            TimestampUtc: DateTimeOffset.UtcNow,
            Kind: kind,
            Source: "test",
            Action: kind.ToString().ToLowerInvariant(),
            Success: true,
            Skipped: false,
            Summary: "ok");
    }
}
