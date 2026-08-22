using AudioPilot.Logging;
using AudioPilot.Services.Routines;

namespace AudioPilot.Tests.Services.Routines;

public sealed class RoutineVolumeTests
{
    [Fact]
    public void Restore_RemainsBoundToOriginalEndpointAndPreservesMute()
    {
        var original = new Endpoint { Volume = .8f, Mute = true };
        var other = new Endpoint { Volume = .9f };
        Endpoint currentDefault = original;
        var result = RoutineEndpointVolumeService.Apply(() => currentDefault, Logger.Instance, 30, true, "volume");
        using var change = result.Change!;
        currentDefault = other;
        change.Restore();
        Assert.Equal(.8f, original.Volume);
        Assert.Equal(.9f, other.Volume);
        Assert.True(original.Mute);
        change.Dispose();
        Assert.Equal(0, original.Subscribers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalVolumeChange_PreventsRestorationEvenWhenLevelReturns(bool returnToApplied)
    {
        var endpoint = new Endpoint { Volume = .8f };
        var result = RoutineEndpointVolumeService.Apply(() => endpoint, Logger.Instance, 30, true, "external");
        using var change = result.Change!;
        endpoint.ExternalVolume(.6f);
        if (returnToApplied) endpoint.ExternalVolume(.3f);
        change.Restore();
        Assert.Equal(returnToApplied ? .3f : .6f, endpoint.Volume);
    }

    [Fact]
    public void NewerNoOpVolumeAction_InvalidatesPreviousOwner()
    {
        var endpoint = new Endpoint { Volume = .8f };
        var older = RoutineEndpointVolumeService.Apply(() => endpoint, Logger.Instance, 30, true, "older");
        using var change = older.Change!;
        Assert.True(RoutineEndpointVolumeService.Apply(() => endpoint, Logger.Instance, 30, false, "newer").Success);
        change.Restore();
        Assert.Equal(.3f, endpoint.Volume);
    }

    [Fact]
    public void CombinedVolumeAndMute_RestoreInReverseOrderWithoutInvalidatingEachOther()
    {
        var endpoint = new Endpoint { Volume = .8f };
        var volume = RoutineEndpointVolumeService.Apply(() => endpoint, Logger.Instance, 30, true, "volume");
        var mute = RoutineEndpointMuteService.Apply(() => endpoint, Logger.Instance, true, true, true, "mute");
        using var restoration = new RoutineAudioRestoration([volume.Change!, mute.Change!], Logger.Instance);
        Assert.True(endpoint.Mute);
        restoration.Complete(true);
        Assert.Equal(.8f, endpoint.Volume);
        Assert.False(endpoint.Mute);
        Assert.Equal(0, endpoint.Subscribers);
    }

    [Fact]
    public async Task CancellationAfterVolumeWrite_RestoresAndReleasesOwnedEndpoint()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var endpoint = new Endpoint { Volume = .8f };
        var operations = new RoutineExecutionOperations(_ => [], (_, target) => target,
            (_, _, _, _, _) => Task.FromResult(default(BluetoothReconnectAttemptResult)),
            (_, _, _, _, _) => Task.CompletedTask,
            _ => ValueTask.FromResult((true, (string?)null)),
            (_, _, _, _) => throw new InvalidOperationException("No routing expected"),
            (_, _, _, _) => throw new InvalidOperationException("Owned volume must be used"))
        {
            ApplyOwnedVolume = (_, _, percent, capture, id) =>
            {
                var applied = RoutineEndpointVolumeService.Apply(() => endpoint, Logger.Instance, percent, capture, id);
                cancellation.Cancel();
                return applied;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RoutineExecutionService(operations, Logger.Instance)
            .ExecuteAsync(new() { MasterVolumePercent = 30 }, new(CaptureAudioRestoration: true), cancellationToken: cancellation.Token));
        Assert.Equal(.8f, endpoint.Volume);
        Assert.Equal(0, endpoint.Subscribers);
    }

    private sealed class Endpoint : IRoutineVolumeEndpoint, IRoutineMuteEndpoint
    {
        private float _volume;
        private bool _mute;
        private event Action<Guid, float>? VolumeChanged;
        private event Action<Guid>? MuteChanged;
        public string Id { get; } = Guid.NewGuid().ToString();
        public Guid NotificationGuid { get; set; }
        public float Volume { get => _volume; set { _volume = value; Notify(NotificationGuid); } }
        public bool Mute { get => _mute; set { _mute = value; Notify(NotificationGuid); } }
        public int Subscribers => (VolumeChanged?.GetInvocationList().Length ?? 0) + (MuteChanged?.GetInvocationList().Length ?? 0);
        event Action<Guid, float>? IRoutineVolumeEndpoint.Changed { add => VolumeChanged += value; remove => VolumeChanged -= value; }
        event Action<Guid>? IRoutineMuteEndpoint.Changed { add => MuteChanged += value; remove => MuteChanged -= value; }
        internal void ExternalVolume(float value) { _volume = value; Notify(Guid.Empty); }
        private void Notify(Guid context) { VolumeChanged?.Invoke(context, _volume); MuteChanged?.Invoke(context); }
        public void Dispose() { }
    }
}
