using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using AudioPilot.Logging;

namespace AudioPilot.Updater.Ui;

/// <summary>Owns one rotating workspace log. Only allowlisted fields and forced-redacted exception messages reach the writer.</summary>
internal sealed class UpdaterLog : IUpdateLog
{
    internal const long RotationBytes = 1024 * 1024;
    internal const int ArchiveCount = 2;
    private readonly Logger _writer;
    private readonly string _run = Guid.NewGuid().ToString("N");
    private Task? _dispose;
    internal bool IsDisposed => _dispose != null;

    private UpdaterLog(OwnedWorkspace workspace)
    {
        _ = workspace.Resolve("updater.log");
        _ = workspace.Resolve("backups");
        _ = workspace.Resolve("backups/updater.log.bak");
        _ = workspace.Resolve("backups/updater.log.bak.1");
        _writer = new Logger(workspace.Root, "updater.log", Logger.LoggerWriteMode.FileBacked,
            disposeWaitMs: 1000, maxLogFileBytes: RotationBytes, logBackupRetentionCount: ArchiveCount);
    }

    internal static bool Attach(OwnedWorkspace workspace)
    {
        if (workspace.Diagnostics is UpdaterLog { IsDisposed: true }) workspace.Diagnostics = null;
        if (workspace.Diagnostics != null) return false;
        try { workspace.Diagnostics = new UpdaterLog(workspace); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // No fallback folder: a logging failure must not redirect updater data or prevent recovery.
            try { Console.Error.WriteLine(Format(new(UpdateLogKind.StartupFailed, Operation: "log-open", Error: error))); }
            catch { }
            return false;
        }
    }

    public void Write(UpdateLogEvent entry)
    {
        var level = entry.Kind == UpdateLogKind.OperationCancelled ? LogLevel.Info
            : entry.Error != null || entry.Kind is UpdateLogKind.ControlRejected ? LogLevel.Warning : LogLevel.Info;
        _writer.Log(level, "Updater", "run=" + _run + " " + Format(entry));
    }

    internal static string Format(UpdateLogEvent entry)
    {
        var text = new StringBuilder(256);
        text.Append("event=").Append(entry.Kind);
        if (entry.Phase is { } phase) text.Append(" phase=").Append(phase);
        if (entry.Operation is { } operation)
            text.Append(" operation=").Append(operation is "StageAsync" or "CheckAsync" or "DownloadAsync" or "ApplyAsync" or "RecoverAsync" or
                "apply" or "resume" or "pause" or "cancel" or "status" or "show" or "window" or "log-open" or
                "OpenSession" or "OpenFeedSession" or "OpenStagingSession" or "OpenPilotSession" or "OpenPilotRecoverySession" ? operation : LogPrivacy.RedactedLabel(operation));
        if (entry.Version is { } version)
            text.Append(" version=").Append(Version.TryParse(version, out var parsed) && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == version ? version : LogPrivacy.RedactedLabel(version));
        if (entry.JobId is { } job) text.Append(" job=").Append(Guid.TryParseExact(job, "N", out var id) ? id.ToString("N") : LogPrivacy.RedactedLabel(job));
        if (entry.Reason is { } reason)
            text.Append(" reason=").Append(reason is "microphone-owned" or "microphone-control-active" or "microphone-hold-active" or
                "routine-work-active" or "settings-busy" or "unsaved-changes" or "dialog-active" or "interaction-active" or "resume-in-progress" or
                "current" or "no-release" or "variant-unavailable" or "bootstrap-required" or "requested-version-unavailable" or
                "update-check-failed" or "update-package-unavailable" or "update-cancelled" or "update-command-failed" or
                "interrupted-operation" or "state-persistence-failed" or "incomplete-job-record" or
                "installer-busy" or "installer-reboot-required" or "installer-failed" || Trust.UpdateFeedException.IsKnown(reason) ? reason : LogPrivacy.RedactedLabel(reason));
        int depth = 0;
        for (Exception? error = entry.Error; error != null && depth < 4; error = error.InnerException, depth++)
        {
            text.Append(depth == 0 ? " error=" : " inner=").Append(error.GetType().Name)
                .Append(" hresult=0x").Append(error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            if (error is Win32Exception native) text.Append(" nativeCode=").Append(native.NativeErrorCode);
            if (error is MsiUpdateException msi) text.Append(" msiStep=").Append(msi.Operation);
            if (error is HttpRequestException http) text.Append(" httpStatus=").Append((int?)http.StatusCode);
            text.Append(" message[").Append(LogPrivacy.RedactedLabel(error.Message)).Append(']');
            if (error.TargetSite is { } method)
            {
                string origin = method.DeclaringType?.FullName + "." + method.Name;
                text.Append(" origin=").Append(origin.AsSpan(0, Math.Min(256, origin.Length)));
            }
        }
        return text.Length <= 4096 ? text.ToString() : text.ToString(0, 4096);
    }

    public ValueTask DisposeAsync() => new(_dispose ??= _writer.DisposeAsync().AsTask());
}
