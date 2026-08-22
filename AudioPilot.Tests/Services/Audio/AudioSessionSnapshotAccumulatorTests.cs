using AudioPilot.Models;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioSessionSnapshotAccumulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleEndpoints_IdleSessionsCannotOverrideActiveVolumeOrMute(bool activeFirst)
    {
        var idle = new AudioSessionSnapshot("Game", 80, "Old output", "game", null, 10, false, "idle", "old");
        var active = idle with { Volume = 20, IsMuted = true, SessionInstanceId = "active", EndpointId = "new" };
        var snapshots = new List<AudioSessionSnapshot>();
        var accumulator = new AudioSessionSnapshotAccumulator(snapshots);

        Assert.True(accumulator.Add(activeFirst ? active : idle,
            activeFirst ? AudioSessionState.AudioSessionStateActive : AudioSessionState.AudioSessionStateInactive));
        Assert.True(accumulator.Add(activeFirst ? idle : active,
            activeFirst ? AudioSessionState.AudioSessionStateInactive : AudioSessionState.AudioSessionStateActive));

        AudioSessionSnapshot result = Assert.Single(snapshots);
        Assert.Equal(20, result.Volume);
        Assert.True(result.IsMuted);
        Assert.Equal(2, result.SessionCount);
        Assert.Equal("active", result.SessionInstanceId);
        Assert.Equal("new", result.EndpointId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleActiveSessions_CombineTheirStateWithoutIdleSessionValues(bool secondMuted)
    {
        var snapshots = new List<AudioSessionSnapshot>();
        var accumulator = new AudioSessionSnapshotAccumulator(snapshots);
        var first = new AudioSessionSnapshot("Game", 20, "Output", "game", null, 10, true);

        Assert.True(accumulator.Add(first, AudioSessionState.AudioSessionStateActive));
        Assert.True(accumulator.Add(first with { Volume = 100, IsMuted = false }, AudioSessionState.AudioSessionStateInactive));
        Assert.True(accumulator.Add(first with { Volume = 40, IsMuted = secondMuted }, AudioSessionState.AudioSessionStateActive));

        AudioSessionSnapshot result = Assert.Single(snapshots);
        Assert.Equal(40, result.Volume);
        Assert.Equal(secondMuted, result.IsMuted);
        Assert.Equal(3, result.SessionCount);
    }

    [Fact]
    public void NoActiveSessions_CombinesIdleStateSoPausedApplicationsRemainControllable()
    {
        var snapshots = new List<AudioSessionSnapshot>();
        var accumulator = new AudioSessionSnapshotAccumulator(snapshots);
        var first = new AudioSessionSnapshot("Game", 20, "Output", "game", null, 10, true);

        Assert.True(accumulator.Add(first, AudioSessionState.AudioSessionStateInactive));
        Assert.True(accumulator.Add(first with { Volume = 40 }, AudioSessionState.AudioSessionStateInactive));
        Assert.True(Assert.Single(snapshots).IsMuted);
        Assert.True(accumulator.Add(first with { Volume = 30, IsMuted = false }, AudioSessionState.AudioSessionStateInactive));

        AudioSessionSnapshot result = Assert.Single(snapshots);
        Assert.Equal(40, result.Volume);
        Assert.False(result.IsMuted);
        Assert.Equal(3, result.SessionCount);
    }

    [Fact]
    public void ExpiredSession_DoesNotReserveProcessOrAffectTheRemainingGroup()
    {
        var snapshots = new List<AudioSessionSnapshot>();
        var accumulator = new AudioSessionSnapshotAccumulator(snapshots);
        var expired = new AudioSessionSnapshot("Game", 100, "Old output", "game", null, 10, false);
        var current = expired with { Volume = 20, IsMuted = true, EndpointId = "new" };

        Assert.False(accumulator.Add(expired, AudioSessionState.AudioSessionStateExpired));
        Assert.True(accumulator.Add(current, AudioSessionState.AudioSessionStateInactive));
        Assert.False(accumulator.Add(expired, AudioSessionState.AudioSessionStateExpired));
        Assert.Equal(current, Assert.Single(snapshots));
    }

    [Fact]
    public void ProcessGroups_KeepSharedRowsAndOtherProcessesIndependent()
    {
        var shared = new AudioSessionSnapshot("Master Volume", 90, "Output", null, null, null);
        List<AudioSessionSnapshot> snapshots = [shared];
        var accumulator = new AudioSessionSnapshotAccumulator(snapshots);
        var first = new AudioSessionSnapshot("Same name", 20, "Output", "first", null, 10, true);
        var second = first with { ProcessId = 11, Volume = 70, IsMuted = false };

        Assert.True(accumulator.Add(first, AudioSessionState.AudioSessionStateActive));
        Assert.True(accumulator.Add(second, AudioSessionState.AudioSessionStateActive));
        Assert.True(accumulator.Add(first with { Volume = 30 }, AudioSessionState.AudioSessionStateInactive));

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(shared, snapshots[0]);
        Assert.Equal(first with { SessionCount = 2 }, snapshots[1]);
        Assert.Equal(second, snapshots[2]);
    }
}
