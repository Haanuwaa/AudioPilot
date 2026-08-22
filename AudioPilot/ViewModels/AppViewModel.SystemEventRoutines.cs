using AudioPilot.Coordinators;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private readonly Lock _routineSystemEventLock = new();
    private RoutineSystemEventCoordinator? _routineSystemEvents;

    internal RoutineSystemEventCoordinator RoutineSystemEvents
    {
        get
        {
            lock (_routineSystemEventLock)
                return _routineSystemEvents ??= new RoutineSystemEventCoordinator(GetPersistedRoutineSnapshot, GetRoutineAvailabilityDevices,
                    (routine, token) => ExecuteRoutineForResolvedProcessAsync(routine, 0, showOverlay: true,
                        executionSource: "system-event", trackLifetime: false, cancellationToken: token), _logger, ShutdownToken);
        }
    }
}
