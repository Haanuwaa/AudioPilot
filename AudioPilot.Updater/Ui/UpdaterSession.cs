using System.IO;
using System.Net.Http;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater.Ui;

/// <summary>Owns UI operations and safe closing without giving the window ownership of installer cancellation.</summary>
internal sealed class UpdaterSession(LocalUpdateJob job, IAsyncDisposable runtime, UpdateSelection installed, IUpdateFeed? feed = null, HttpClient? downloadClient = null) : IAsyncDisposable
{
    private readonly LocalUpdateJob _job = job;
    internal IUpdateLog? Diagnostics { get; } = job.Diagnostics;
    private int _logClosed;
    private readonly IAsyncDisposable _runtime = runtime;
    private readonly HttpClient? _downloadClient = feed == null ? null : downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));
    public IUpdateFeed? Feed { get; } = feed;
    private Task<UpdateJobState>? _active;
    private Task? _closing;
    private Task? _release;
    private readonly Lock _releaseLock = new();
    private bool _closed;
    private readonly Lock _approvalLock = new();
    private CancellationTokenSource? _approvedFlow;
    private bool _discardApprovedPackage;
    public UpdateJobState Snapshot => _job.Snapshot;
    public bool IsBusy => _active is { IsCompleted: false };
    public bool IsClosed => _closed;
    public bool IsClosing => _closing is { IsCompleted: false };
    public UpdateSelection Installed { get; private set; } = installed;
    public Models.AppTheme Theme { get; init; } = Models.AppTheme.System;
    internal static UpdaterSession CreateForFeed(OwnedWorkspace workspace, ILocalUpdateTrust trust, InstalledUpdateRuntime runtime, IUpdateFeed feed, ILocalUpdateRuntime? operations = null, Models.AppTheme theme = Models.AppTheme.System,
        Func<long, CancellationToken, Task>? checkpoint = null)
    {
        bool ownsLog = UpdaterLog.Attach(workspace);
        HttpClient? client = null;
        LocalUpdateJob? job = null;
        try
        {
            job = new LocalUpdateJob(workspace, trust, operations ?? runtime, checkpoint);
            var installed = runtime.InspectForSession();
            if (feed is HttpsStagingFeed { IsPilot: true } && (installed.Architecture != "win-x64" || installed.Runtime != "self-contained"))
                throw new InvalidOperationException("This pilot supports x64 self-contained MSI and portable packages.");
            client = feed.CreateClient();
            return new(job, runtime, installed, feed, client) { Theme = theme };
        }
        catch (Exception error)
        {
            UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.StartupFailed, Error: error));
            try { client?.Dispose(); }
            catch (Exception cleanup) { UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.CleanupFailed, Error: cleanup)); }
            try { job?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception cleanup) { UpdateLog.Write(workspace.Diagnostics, new(UpdateLogKind.CleanupFailed, Error: cleanup)); }
            if (ownsLog) { try { workspace.Diagnostics?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { } }
            throw;
        }
    }

    public Task<UpdateJobState> VerifyAsync(string source) => Run(() => Feed == null ? _job.StageAsync(source) : _job.DownloadAsync(Feed, _downloadClient!));
    public Task<UpdateJobState> CheckAsync(CancellationToken token = default) => Run(() => _job.CheckAsync(Feed ?? throw new InvalidOperationException("No feed is connected."), token));
    public Task<UpdateJobState> InstallAsync() => Run(() => _job.ApplyAsync());
    public Task<UpdateJobState> RecoverAsync() => Run(() => _job.RecoverAsync());
    public bool Pause() { StopApprovedFlow(); return !_closed && _job.Pause(); }
    public bool Cancel()
    {
        bool revoked = StopApprovedFlow(discard: true);
        return !_closed && (_job.Cancel() || revoked && Snapshot.Phase is UpdateJobPhase.Staging or UpdateJobPhase.Verifying or UpdateJobPhase.Ready);
    }

    internal Task<UpdateJobState> UpdateAndRestartAsync(UpdateJobState approved, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Snapshot != approved || approved.Artifact == null || approved.Version == null ||
            approved.Phase is not (UpdateJobPhase.Available or UpdateJobPhase.Paused or UpdateJobPhase.Failed or UpdateJobPhase.Ready or UpdateJobPhase.Deferred))
            throw new InvalidOperationException("The update changed while confirmation was open.");
        return Run(async () =>
        {
            using var consent = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (_approvalLock) { _approvedFlow = consent; _discardApprovedPackage = false; }
            try
            {
                var ready = approved;
                if (ready.Phase is not (UpdateJobPhase.Ready or UpdateJobPhase.Deferred))
                    ready = await _job.DownloadAsync(Feed ?? throw new InvalidOperationException("No feed is connected."), _downloadClient!, token).ConfigureAwait(false);
                if (ready.Artifact != approved.Artifact || ready.Version != approved.Version)
                    throw new InvalidDataException("The approved release changed.");
                if (consent.IsCancellationRequested || IsClosing || ready.Phase is not (UpdateJobPhase.Ready or UpdateJobPhase.Deferred)) return SettleRevokedApproval(ready);
                return await _job.ApplyAsync(consent.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (consent.IsCancellationRequested) { return SettleRevokedApproval(Snapshot); }
            finally { lock (_approvalLock) _approvedFlow = null; }
        });
    }

    private bool StopApprovedFlow(bool discard = false)
    {
        lock (_approvalLock)
        {
            if (_approvedFlow == null) return false;
            _discardApprovedPackage |= discard;
            _approvedFlow.Cancel();
            return true;
        }
    }

    private UpdateJobState SettleRevokedApproval(UpdateJobState state)
    {
        bool discard;
        lock (_approvalLock) discard = _discardApprovedPackage;
        if (discard && state.Phase is UpdateJobPhase.Available or UpdateJobPhase.Paused or UpdateJobPhase.Ready or UpdateJobPhase.Deferred or UpdateJobPhase.Failed)
        {
            _job.Cancel();
            return Snapshot;
        }
        return state;
    }

    private Task<UpdateJobState> Run(Func<Task<UpdateJobState>> action)
    {
        ObjectDisposedException.ThrowIf(_closed || IsClosing, this);
        if (IsBusy) throw new InvalidOperationException("An updater action is already running.");
        _active = FinishAsync(action());
        return _active;
    }
    private async Task<UpdateJobState> FinishAsync(Task<UpdateJobState> action)
    {
        var result = await action.ConfigureAwait(false);
        if (result.Phase == UpdateJobPhase.RecoveryRequired)
            result = await _job.RecoverAsync().ConfigureAwait(false);
        if (result.Phase == UpdateJobPhase.Completed && result.Version is { } version)
            Installed = Installed with { InstalledVersion = version };
        return result;
    }

    public Task CloseSafelyAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (_closing is { IsCompleted: false }) return _closing;
        _closing = CloseCoreAsync();
        return _closing;
    }
    private async Task CloseCoreAsync()
    {
        StopApprovedFlow();
        bool active = IsBusy;
        if (active)
        {
            if (Snapshot.Phase is UpdateJobPhase.Staging or UpdateJobPhase.Verifying) _job.Pause();
            else _job.Cancel();
            await _active!.ConfigureAwait(false);
        }
        if (active && Snapshot.Phase == UpdateJobPhase.RecoveryRequired)
            throw new InvalidOperationException("Recovery needs attention. Reopen this local workspace to retry safely.");
        await ReleaseResourcesAsync().ConfigureAwait(false);
        UpdateLog.Write(Diagnostics, new(UpdateLogKind.SessionDrained, Snapshot.Phase, Version: Snapshot.Version));
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) { await CloseLogAsync().ConfigureAwait(false); return; }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        try { await CloseSafelyAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        finally
        {
            try
            {
                await ReleaseResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            finally { await CloseLogAsync().ConfigureAwait(false); }
        }
        failure?.Throw();
    }

    private Task ReleaseResourcesAsync()
    {
        lock (_releaseLock) return _release ??= ReleaseCoreAsync();
    }

    private async Task ReleaseCoreAsync()
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        try { await _job.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Failed(error); }
        try { await _runtime.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Failed(error); }
        try { _downloadClient?.Dispose(); }
        catch (Exception error) { Failed(error); }
        _closed = true;
        failure?.Throw();

        void Failed(Exception error)
        {
            failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
            UpdateLog.Write(Diagnostics, new(UpdateLogKind.CleanupFailed, Snapshot.Phase, Error: error));
        }
    }

    private async Task CloseLogAsync()
    {
        if (Interlocked.Exchange(ref _logClosed, 1) == 0)
            UpdateLog.Write(Diagnostics, new(UpdateLogKind.SessionClosed, Snapshot.Phase, Version: Snapshot.Version));
        if (Diagnostics == null) return;
        try { await Diagnostics.DisposeAsync().ConfigureAwait(false); }
        catch { } // Diagnostic cleanup cannot turn a settled update into a failure.
    }
}
