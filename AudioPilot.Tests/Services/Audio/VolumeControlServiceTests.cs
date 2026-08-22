using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Audio;

public sealed class VolumeControlServiceTests
{
    private sealed class DistinctItem(string id, List<string> disposedIds)
    {
        public string Id { get; } = id;
        public void Dispose() => disposedIds.Add(Id);
    }

    private sealed class ThrowingEnumerator : IAudioDeviceEnumerator
    {
        public MMDeviceCollection GetActivePlaybackDevices() => throw new NotSupportedException();
        public IReadOnlyList<MMDevice> GetPlaybackDevicesById(IReadOnlyCollection<string> deviceIds) => throw new NotSupportedException();
        public MMDevice GetDefaultPlaybackDevice() => throw new NotSupportedException();
        public MMDevice? GetDefaultRecordingDevice() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultPlaybackDevices() => throw new NotSupportedException();
        public List<MMDevice?> GetAllDefaultRecordingDevices() => throw new NotSupportedException();
    }

    private static VolumeControlService CreateService()
    {
        return new VolumeControlService(
            new ThrowingEnumerator(),
            lookupProcessInfo: _ => null,
            isCacheEntryExpired: _ => true);
    }

    [Fact]
    public void UpdateSessionVolume_RecordingChangesDoNotPopulateOrOverwritePlaybackCache()
    {
        using var service = CreateService();
        var playback = TestPrivateAccess.GetField<VolumeCacheStore>(service, "_cacheStore");
        service.UpdateSessionVolume("Voice app", "voiceapp", 7f, AudioMixerMode.Input);
        Assert.Null(playback.TryGetApplicationVolume("voiceapp", null, out _));

        service.UpdateSessionVolume("Voice app", "voiceapp", 23f);
        service.UpdateSessionVolume("Voice app", "voiceapp", 100f, AudioMixerMode.Input);
        Assert.Equal(23f, playback.TryGetApplicationVolume("voiceapp", null, out _));
    }

    [Fact]
    public void PlaybackRestore_UsesSessionEndpointAndDoesNotLeakSnapshotIntoGeneralCache()
    {
        using var service = CreateService();
        var snapshot = new SessionVolumeSnapshot { ByProcess = new(StringComparer.OrdinalIgnoreCase) { ["Player"] = 25f } };
        service.RegisterPostSwitchSnapshot(snapshot, "headphones");
        Assert.Null(service.GetPendingSnapshotForPlaybackDeviceId("speakers", DateTime.UtcNow));
        Assert.Null(service.ResolvePlaybackRestoreVolume(null, 10, "Player", "Window"));
        Assert.Equal(25f, service.ResolvePlaybackRestoreVolume(service.GetPendingSnapshotForPlaybackDeviceId("headphones", DateTime.UtcNow), 10, "Player", "Window"));
        Assert.Null(service.ResolvePlaybackRestoreVolume(null, 10, "Player", "Window"));
    }

    [Fact]
    public void PlaybackRestore_NewerChoiceSupersedesPendingSnapshotAndImmediatePlan()
    {
        using var service = CreateService();
        var snapshot = new SessionVolumeSnapshot { ByProcess = new(StringComparer.OrdinalIgnoreCase) { ["Player"] = 25f } };
        service.RegisterPostSwitchSnapshot(snapshot, "headphones");
        service.UpdateSessionVolume("Window", "Player", 65f);
        Assert.Equal(65f, service.ResolvePlaybackRestoreVolume(snapshot, 10, "Player", "Window"));
        var plan = service.BuildSessionVolumeApplicationPlan(snapshot, new(10, "Window", "Player", 65f));
        Assert.Equal(VolumeControlService.SessionVolumeApplicationAction.Skip, plan.Action);
        Assert.Equal("NewerChoice", plan.MatchMethod);
    }

    [Fact]
    public void PlaybackRestore_NewerSnapshotSupersedesOlderCachedChoice()
    {
        using var service = CreateService();
        service.UpdateSessionVolume("Window", "Player", 65f);
        var snapshot = new SessionVolumeSnapshot { ByProcess = new(StringComparer.OrdinalIgnoreCase) { ["Player"] = 25f } };
        Assert.Equal(25f, service.ResolvePlaybackRestoreVolume(snapshot, 10, "Player", "Window"));
    }

    [Fact]
    public void SnapshotMatching_RejectsKnownDifferentProcessDespiteMatchingTitleAndPid()
    {
        using var service = CreateService();
        var snapshot = new SessionVolumeSnapshot
        {
            ByProcess = new(StringComparer.OrdinalIgnoreCase) { ["OtherPlayer"] = 25f },
            ByPid = new() { [10] = 25f },
            ByName = new(StringComparer.OrdinalIgnoreCase) { ["Shared window"] = 25f },
        };
        Assert.False(service.TryResolveSnapshotTarget(snapshot, 10, "Shared window", "Player", DateTime.UtcNow, out _, out _));
    }

    [Fact]
    public void MuteOperationResult_Combine_PreservesEndpointOutcomeCounts()
    {
        var microphone = new MuteOperationResult(2, 1, 1);
        var playback = new MuteOperationResult(3, 3, 0);

        MuteOperationResult combined = MuteOperationResult.Combine(microphone, playback);

        Assert.Equal(5, combined.AttemptedEndpointCount);
        Assert.Equal(4, combined.SucceededEndpointCount);
        Assert.Equal(1, combined.FailedEndpointCount);
        Assert.True(combined.HasTargets);
        Assert.True(combined.HasFailures);
        Assert.False(combined.FullySucceeded);
    }

    [Fact]
    public void MuteOperationResult_WithNoEndpoints_IsNotReportedAsSuccessful()
    {
        MuteOperationResult result = default;

        Assert.False(result.HasTargets);
        Assert.True(result.HasFailures);
        Assert.False(result.FullySucceeded);
    }

    [Fact]
    public void GetDistinctItemsForOperation_RemovesDuplicateIds_AndDisposesDuplicates()
    {
        List<string> disposedIds = [];
        DistinctItem first = new("device-a", disposedIds);
        DistinctItem duplicate = new("device-a", disposedIds);
        DistinctItem second = new("device-b", disposedIds);

        List<DistinctItem> distinct = VolumeControlService.GetDistinctItemsForOperation(
            [first, duplicate, second, null],
            static item => item.Id,
            item => item.Dispose());

        Assert.Equal(2, distinct.Count);
        Assert.Same(first, distinct[0]);
        Assert.Same(second, distinct[1]);
        Assert.Equal(["device-a"], disposedIds);
    }

    [Fact]
    public void GetDistinctItemsForOperation_DoesNotDisposeRepeatedReferences()
    {
        List<string> disposedIds = [];
        DistinctItem shared = new("shared", disposedIds);

        List<DistinctItem> distinct = VolumeControlService.GetDistinctItemsForOperation(
            [shared, shared, shared],
            static item => item.Id,
            item => item.Dispose());

        Assert.Same(shared, Assert.Single(distinct));
        Assert.Empty(disposedIds);
        Assert.Equal("shared", shared.Id);
    }

    [Fact]
    public void TryResolveSnapshotTarget_PrefersExactProcessOverDisplayNameAndPid()
    {
        using var service = CreateService();
        var snapshot = new SessionVolumeSnapshot
        {
            SnapshotTime = DateTime.UtcNow,
            ByPid = new Dictionary<uint, float> { [42] = 80f },
            ByProcess = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Discord"] = 30f },
            ByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Discord"] = 95f }
        };

        bool resolved = service.TryResolveSnapshotTarget(
            snapshot,
            pid: 42,
            normalizedDisplayName: "Discord",
            normalizedProcessName: "Discord",
            nowUtc: DateTime.UtcNow,
            out float target,
            out string method);

        Assert.True(resolved);
        Assert.Equal(30f, target);
        Assert.Equal("ProcessName", method);
    }

    [Fact]
    public void TryResolveSnapshotTarget_BlocksStalePidFallback_WhenNameIdentityExists()
    {
        using var service = CreateService();
        DateTime nowUtc = DateTime.UtcNow;
        var snapshot = new SessionVolumeSnapshot
        {
            SnapshotTime = nowUtc - TimeSpan.FromSeconds(10),
            ByPid = new Dictionary<uint, float> { [77] = 65f }
        };

        bool resolved = service.TryResolveSnapshotTarget(
            snapshot,
            pid: 77,
            normalizedDisplayName: "Some App",
            normalizedProcessName: "Some App",
            nowUtc: nowUtc,
            out _,
            out _);

        Assert.False(resolved);
    }

    [Fact]
    public void ClockMovingBackward_DoesNotRestoreVolumeByPidWhenNameIdentityDiffers()
    {
        using var service = CreateService();
        DateTime nowUtc = DateTime.UtcNow;
        var snapshot = new SessionVolumeSnapshot
        {
            SnapshotTime = nowUtc.AddMinutes(5),
            ByPid = new Dictionary<uint, float> { [77] = 65f },
        };

        Assert.False(service.TryResolveSnapshotTarget(snapshot, 77, "Different app", "Different app", nowUtc, out _, out _));
    }

    [Fact]
    public void TryResolveSnapshotTarget_AllowsFreshPidFallback_WhenNameIdentityExists()
    {
        using var service = CreateService();
        DateTime nowUtc = DateTime.UtcNow;
        var snapshot = new SessionVolumeSnapshot
        {
            SnapshotTime = nowUtc - TimeSpan.FromSeconds(2),
            ByPid = new Dictionary<uint, float> { [77] = 65f }
        };

        bool resolved = service.TryResolveSnapshotTarget(
            snapshot,
            pid: 77,
            normalizedDisplayName: "Some App",
            normalizedProcessName: "Some App",
            nowUtc: nowUtc,
            out float target,
            out string method);

        Assert.True(resolved);
        Assert.Equal(65f, target);
        Assert.Equal("PID", method);
    }

    [Fact]
    public void TryResolveSnapshotTarget_RejectsStalePidEvenWithoutNameIdentity()
    {
        using var service = CreateService();
        DateTime nowUtc = DateTime.UtcNow;
        var snapshot = new SessionVolumeSnapshot
        {
            SnapshotTime = nowUtc - TimeSpan.FromMinutes(2),
            ByPid = new Dictionary<uint, float> { [91] = 25f }
        };

        bool resolved = service.TryResolveSnapshotTarget(
            snapshot,
            pid: 91,
            normalizedDisplayName: "",
            normalizedProcessName: "",
            nowUtc: nowUtc,
            out float target,
            out string method);

        Assert.False(resolved);
        Assert.Equal(0f, target);
        Assert.Empty(method);
    }

    [Fact]
    public void TryResolveSnapshotTarget_UsesSystemSounds_ForPidZero()
    {
        using var service = CreateService();
        var snapshot = new SessionVolumeSnapshot
        {
            SnapshotTime = DateTime.UtcNow,
            SystemSoundsVolumePercent = 15f
        };

        bool resolved = service.TryResolveSnapshotTarget(
            snapshot,
            pid: 0,
            normalizedDisplayName: "System Sounds",
            normalizedProcessName: "System",
            nowUtc: DateTime.UtcNow,
            out float target,
            out string method);

        Assert.True(resolved);
        Assert.Equal(15f, target);
        Assert.Equal("SystemSounds", method);
    }

    [Fact]
    public void GetPendingSnapshotForPlaybackDeviceId_ReturnsLatestRegisteredSnapshot()
    {
        using var service = CreateService();
        SessionVolumeSnapshot first = new() { ByPid = new Dictionary<uint, float> { [1] = 10f } };
        SessionVolumeSnapshot second = new() { ByPid = new Dictionary<uint, float> { [2] = 20f } };

        service.RegisterPostSwitchSnapshot(first, "device-a");
        service.RegisterPostSwitchSnapshot(second, "device-a");

        SessionVolumeSnapshot? resolved = service.GetPendingSnapshotForPlaybackDeviceId("device-a", DateTime.UtcNow);

        Assert.Same(second, resolved);
    }

    [Fact]
    public void ResolvePendingSnapshotForPlaybackDevice_ReturnsClearSignal_WhenExpired()
    {
        SessionVolumeSnapshot snapshot = new();
        VolumeControlService.PendingSessionRestore pending = new(
            snapshot,
            "device-a",
            DateTime.UtcNow.AddSeconds(-1));

        VolumeControlService.PendingSessionRestoreResolution resolved = VolumeControlService.ResolvePendingSnapshotForPlaybackDevice(
            pending,
            "device-a",
            DateTime.UtcNow);

        Assert.Null(resolved.Snapshot);
        Assert.True(resolved.ShouldClearPending);
    }

    [Fact]
    public void ResolvePendingSnapshotForPlaybackDevice_ReturnsSnapshot_WhenDeviceMatches()
    {
        SessionVolumeSnapshot snapshot = new();
        VolumeControlService.PendingSessionRestore pending = new(
            snapshot,
            "device-a",
            DateTime.UtcNow.AddMinutes(1));

        VolumeControlService.PendingSessionRestoreResolution resolved = VolumeControlService.ResolvePendingSnapshotForPlaybackDevice(
            pending,
            "device-a",
            DateTime.UtcNow);

        Assert.Same(snapshot, resolved.Snapshot);
        Assert.False(resolved.ShouldClearPending);
    }

    [Fact]
    public void BuildSessionApplyFailureDetail_IncludesRelevantContext()
    {
        string detail = VolumeControlService.BuildSessionApplyFailureDetail(
            "Discord",
            pid: 42,
            matchMethod: "Name",
            currentVolume: 25f,
            targetVolume: 60f,
            reason: "set-failed");

        Assert.Contains("pid=id[len=2 hash=", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pid=42", detail, StringComparison.Ordinal);
        Assert.Contains("method=Name", detail, StringComparison.Ordinal);
        Assert.Contains("current=25.0%", detail, StringComparison.Ordinal);
        Assert.Contains("target=60.0%", detail, StringComparison.Ordinal);
        Assert.Contains("reason=set-failed", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSessionApplyFailureSummary_ReportsDetailsAndSuppressedCount()
    {
        string summary = VolumeControlService.BuildSessionApplyFailureSummary(
            ["first failure", "second failure"],
            suppressedFailureCount: 2);

        Assert.Contains("first failure", summary, StringComparison.Ordinal);
        Assert.Contains("second failure", summary, StringComparison.Ordinal);
        Assert.Contains("suppressedFailureDetails=2", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSessionVolumeApplicationPlan_ReturnsApply_ForMatchedNamedSession()
    {
        using var service = CreateService();
        SessionVolumeSnapshot snapshot = new()
        {
            ByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["discord"] = 60f,
            },
        };

        VolumeControlService.SessionVolumeApplicationPlan plan = service.BuildSessionVolumeApplicationPlan(
            snapshot,
            new VolumeControlService.SessionVolumeApplicationCandidate(
                Pid: 42,
                DisplayName: "Discord",
                ProcessName: "Discord",
                CurrentVolumePercent: 25f));

        Assert.Equal(VolumeControlService.SessionVolumeApplicationAction.Apply, plan.Action);
        Assert.Equal("ProcessName", plan.MatchMethod);
        Assert.Equal(60f, plan.TargetVolumePercent);
    }

    [Fact]
    public void BuildSessionVolumeApplicationPlan_ReturnsSkip_WhenMatchedVolumeAlreadyInRange()
    {
        using var service = CreateService();
        SessionVolumeSnapshot snapshot = new()
        {
            ByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["discord"] = 60f,
            },
        };

        VolumeControlService.SessionVolumeApplicationPlan plan = service.BuildSessionVolumeApplicationPlan(
            snapshot,
            new VolumeControlService.SessionVolumeApplicationCandidate(
                Pid: 42,
                DisplayName: "Discord",
                ProcessName: "Discord",
                CurrentVolumePercent: 60.4f));

        Assert.Equal(VolumeControlService.SessionVolumeApplicationAction.Skip, plan.Action);
        Assert.Equal("ProcessName", plan.MatchMethod);
        Assert.Equal(60f, plan.TargetVolumePercent);
    }

    [Fact]
    public void BuildSessionVolumeApplicationPlan_ReturnsApply_ForSystemSounds()
    {
        using var service = CreateService();
        SessionVolumeSnapshot snapshot = new()
        {
            SystemSoundsVolumePercent = 15f,
        };

        VolumeControlService.SessionVolumeApplicationPlan plan = service.BuildSessionVolumeApplicationPlan(
            snapshot,
            new VolumeControlService.SessionVolumeApplicationCandidate(
                Pid: 0,
                DisplayName: "System Sounds",
                ProcessName: "System",
                CurrentVolumePercent: 40f,
                IsSystemSounds: true));

        Assert.Equal(VolumeControlService.SessionVolumeApplicationAction.Apply, plan.Action);
        Assert.Equal("SystemSounds", plan.MatchMethod);
        Assert.Equal(15f, plan.TargetVolumePercent);
    }

    [Fact]
    public void BuildSessionVolumeApplicationPlan_ReturnsNone_WhenSnapshotHasNoMatchingTarget()
    {
        using var service = CreateService();
        SessionVolumeSnapshot snapshot = new()
        {
            ByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["spotify"] = 25f,
            },
        };

        VolumeControlService.SessionVolumeApplicationPlan plan = service.BuildSessionVolumeApplicationPlan(
            snapshot,
            new VolumeControlService.SessionVolumeApplicationCandidate(
                Pid: 42,
                DisplayName: "Discord",
                ProcessName: "Discord",
                CurrentVolumePercent: 40f));

        Assert.Equal(VolumeControlService.SessionVolumeApplicationAction.None, plan.Action);
        Assert.Null(plan.TargetVolumePercent);
        Assert.Null(plan.MatchMethod);
    }

    [Fact]
    public void BuildSessionVolumeApplicationPlan_UsesProcessNameFallback_WhenDisplayNameMissing()
    {
        using var service = CreateService();
        SessionVolumeSnapshot snapshot = new()
        {
            ByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["discord"] = 35f,
            },
        };

        VolumeControlService.SessionVolumeApplicationPlan plan = service.BuildSessionVolumeApplicationPlan(
            snapshot,
            new VolumeControlService.SessionVolumeApplicationCandidate(
                Pid: 42,
                DisplayName: null,
                ProcessName: "Discord",
                CurrentVolumePercent: 10f));

        Assert.Equal(VolumeControlService.SessionVolumeApplicationAction.Apply, plan.Action);
        Assert.Equal("ProcessName", plan.MatchMethod);
        Assert.Equal(35f, plan.TargetVolumePercent);
        Assert.Equal("Discord", plan.SessionLabel);
    }
}

