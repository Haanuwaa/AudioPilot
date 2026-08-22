namespace AudioPilot.Logging;

/// <summary>On-demand runtime state without input values, device identities, or routine names.</summary>
public sealed record RuntimeHealthSnapshot(
    DateTimeOffset CapturedUtc,
    string HostMode,
    string ProcessMonitorMode,
    string? ProcessMonitorFallbackReason,
    HotkeyRuntimeSnapshot? Hotkeys,
    RoutineWorkSnapshot? RoutineWork,
    long DroppedLogMessages,
    AudioHealthSnapshot? Audio = null,
    SettingsHealthSnapshot? Settings = null,
    LoggingHealthSnapshot? Logging = null,
    RecoveryHealthSnapshot? Recovery = null);

public sealed record AudioHealthSnapshot(string OutputDefault, string InputDefault, bool? OutputRolesSplit, bool? InputRolesSplit, bool RefreshPending,
    DateTimeOffset? LastRefreshedUtc = null, bool Stale = false);

public sealed record SettingsHealthSnapshot(bool AutoSaveEnabled, bool SavePending, bool UsingFallback,
    DateTimeOffset? LastSaveAttemptUtc, bool? LastSaveSucceeded, string? LastSaveError, bool RecoveryWarning);

public sealed record LoggingHealthSnapshot(string Level, bool RedactionEnabled, int QueuedMessages,
    DateTimeOffset? LastWriteAttemptUtc, bool? LastWriteSucceeded, string? LastWriteError);

public sealed record RecoveryHealthSnapshot(DateTimeOffset CompletedUtc, bool Succeeded, int HotkeyAttempts, int HotkeyFailedCount);

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
    int DeferredOperations, IReadOnlyList<string> DebounceSlots, bool AutomaticPaused = false,
    bool? HasAutomaticRoutines = null, DateTimeOffset? AutomaticResumeUtc = null);
