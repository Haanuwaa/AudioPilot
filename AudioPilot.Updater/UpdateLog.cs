namespace AudioPilot.Updater;

internal enum UpdateLogKind
{
    SessionOpened, SessionDrained, SessionClosed, StartupFailed, OperationStarted, PhaseChanged, OperationFailed, OperationCancelled,
    PauseRequested, CancelRequested, ConfigurationCaptured, ConfigurationRestored, AppPrepared, AppRestarted,
    CandidateFailed, RecoveryCompleted, ControlAccepted, ControlRejected, ApprovalGranted, ApprovalDeclined,
    WindowActionFailed, CleanupFailed
}

internal sealed record UpdateLogEvent(UpdateLogKind Kind, UpdateJobPhase? Phase = null, string? Operation = null,
    string? Version = null, string? Reason = null, Exception? Error = null, string? JobId = null);

/// <summary>Structured diagnostics supplied by the executable; the engine has no UI or file-logger dependency.</summary>
internal interface IUpdateLog : IAsyncDisposable
{
    void Write(UpdateLogEvent entry);
}

internal static class UpdateLog
{
    internal static void Write(IUpdateLog? log, UpdateLogEvent entry)
    {
        try { log?.Write(entry); }
        catch { } // Diagnostics must never change deployment or recovery outcomes.
    }
}
