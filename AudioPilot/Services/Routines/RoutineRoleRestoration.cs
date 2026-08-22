using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Routines;

internal readonly record struct RoutineRoleSwitchResult(bool Success, string? DeviceName, IRoutineAudioChange? Change = null);

/// <summary>Restores only role assignments still owned by a routine, including protection against external A-B-A changes.</summary>
internal sealed class RoutineRoleRestoration : IRoutineAudioChange
{
    private static readonly Lock OwnersLock = new();
    private static readonly Dictionary<(bool Playback, Role Role), RoutineRoleRestoration> Owners = [];
    private readonly bool _playback;
    private readonly string _targetId;
    private readonly Dictionary<Role, string?> _previous;
    private readonly HashSet<Role> _invalid = [];
    private readonly Func<Role, string?> _read;
    private readonly Action<string, Role> _apply;
    private readonly Action<Action<DataFlow, Role, string?>> _unsubscribe;
    private readonly Logger _logger;
    private int _disposed;
    internal RoutineRoleRestoration(bool playback, string targetId, IEnumerable<Role> roles,
        Func<Role, string?> read, Action<string, Role> apply,
        Action<Action<DataFlow, Role, string?>> subscribe, Action<Action<DataFlow, Role, string?>> unsubscribe, Logger logger)
    {
        _playback = playback;
        _targetId = targetId;
        _read = read;
        _apply = apply;
        _unsubscribe = unsubscribe;
        _logger = logger;
        _previous = roles.ToDictionary(role => role, role => DeviceRoleSwitchEngine.ReadDefaultDeviceId(read, role));
        subscribe(OnChanged);
    }

    internal bool AlreadySelected => _previous.Values.All(id => string.Equals(id, _targetId, StringComparison.OrdinalIgnoreCase));

    internal void Claim()
    {
        lock (OwnersLock)
        {
            foreach (Role role in _previous.Keys)
            {
                var key = (_playback, role);
                if (Owners.TryGetValue(key, out var previous)) previous._invalid.Add(role);
                Owners[key] = this;
            }
        }
    }

    private void OnChanged(DataFlow flow, Role role, string? id)
    {
        if (flow != (_playback ? DataFlow.Render : DataFlow.Capture) || !_previous.ContainsKey(role)) return;
        if (!string.Equals(id, _targetId, StringComparison.OrdinalIgnoreCase))
            lock (OwnersLock) _invalid.Add(role);
    }

    public void Restore()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        ComThreadingHelper.RunOnCoreAudioThread(() =>
        {
            foreach (var (role, previousId) in _previous)
            {
                lock (OwnersLock)
                    if (_invalid.Contains(role) || !Owners.TryGetValue((_playback, role), out var owner) || !ReferenceEquals(owner, this)) continue;
                if (string.IsNullOrWhiteSpace(previousId) || string.Equals(previousId, _targetId, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!string.Equals(DeviceRoleSwitchEngine.ReadDefaultDeviceId(_read, role), _targetId, StringComparison.OrdinalIgnoreCase)) continue;
                    _apply(previousId, role);
                    _logger.Debug("RoutineRoles", () => $"routine-role-restored | playback={_playback} role={role} verified={string.Equals(DeviceRoleSwitchEngine.ReadDefaultDeviceId(_read, role), previousId, StringComparison.OrdinalIgnoreCase)}");
                }
                catch (Exception ex) { _logger.Warning("RoutineRoles", $"routine-role-restore-failed | playback={_playback} role={role}", nameof(Restore), ex); }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _unsubscribe(OnChanged); }
        finally
        {
            lock (OwnersLock)
                foreach (Role role in _previous.Keys)
                    if (Owners.TryGetValue((_playback, role), out var owner) && ReferenceEquals(owner, this)) Owners.Remove((_playback, role));
        }
    }
}
