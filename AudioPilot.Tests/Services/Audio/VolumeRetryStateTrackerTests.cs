using AudioPilot.Constants;

namespace AudioPilot.Tests.Services.Audio;

public sealed class VolumeRetryStateTrackerTests
{
    private long _utcNowTicks = TimeSpan.TicksPerSecond;

    [Fact]
    public void IsCircuitOpen_ReturnsTrue_AfterThreeFailuresWithinCooldown()
    {
        var tracker = CreateTracker(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        tracker.RecordFailure("device-1");
        tracker.RecordFailure("device-1");
        tracker.RecordFailure("device-1");

        Assert.True(tracker.IsCircuitOpen("device-1"));
    }

    [Fact]
    public void Reset_ClearsCircuitState()
    {
        var tracker = CreateTracker(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        tracker.RecordFailure("device-1");
        tracker.RecordFailure("device-1");
        tracker.RecordFailure("device-1");
        tracker.Reset("device-1");

        Assert.False(tracker.IsCircuitOpen("device-1"));
    }

    [Fact]
    public void DefaultCooldown_AllowsProbeAfterTransientFailureWindow()
    {
        TimeSpan defaultCooldown = TimeSpan.FromSeconds(AppConstants.Timing.CircuitBreakerCooldownSeconds);
        var tracker = CreateTracker(TimeSpan.FromMinutes(5), defaultCooldown);

        tracker.RecordFailure("device-1");
        tracker.RecordFailure("device-1");
        tracker.RecordFailure("device-1");
        Assert.True(tracker.IsCircuitOpen("device-1"));

        _utcNowTicks += defaultCooldown.Ticks;

        Assert.False(tracker.IsCircuitOpen("device-1"));
    }

    [Fact]
    public void CleanupExpiredStates_RemovesExpiredEntries()
    {
        var tracker = CreateTracker(TimeSpan.Zero, TimeSpan.FromMinutes(1));

        tracker.RecordFailure("device-1");
        _utcNowTicks += TimeSpan.TicksPerMillisecond * 10;

        int removed = tracker.CleanupExpiredStates();

        Assert.True(removed > 0);
        Assert.False(tracker.IsCircuitOpen("device-1"));
    }

    private VolumeRetryStateTracker CreateTracker(TimeSpan ttl, TimeSpan cooldown)
    {
        return new VolumeRetryStateTracker(ttl, cooldown, () => _utcNowTicks);
    }
}
