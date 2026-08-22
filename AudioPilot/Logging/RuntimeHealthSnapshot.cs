namespace AudioPilot.Logging;

/// <summary>On-demand runtime state without input values, device identities, or routine names.</summary>
public sealed record RuntimeHealthSnapshot(
    DateTimeOffset CapturedUtc,
    string HostMode,
    string ProcessMonitorMode,
    string? ProcessMonitorFallbackReason,
    HotkeyRuntimeSnapshot? Hotkeys,
    RoutineWorkSnapshot? RoutineWork,
    long DroppedLogMessages);

public sealed record HotkeyRuntimeSnapshot(
    bool Initialized,
    bool ShuttingDown,
    bool CaptureSuspended,
    int BindingCount,
    bool KeyboardMessageHostRunning,
    bool KeyboardHookRunning,
    bool MouseHookRunning,
    IReadOnlyList<HotkeyRegistrationSnapshot> Registrations);

public sealed record HotkeyRegistrationSnapshot(int ActionId, string LastOutcome, string? Delivery, int Win32Error);

public sealed record RoutineWorkSnapshot(bool MonitoringEnabled, bool Stopping, int ActiveTasks,
    int DeferredOperations, IReadOnlyList<string> DebounceSlots);
