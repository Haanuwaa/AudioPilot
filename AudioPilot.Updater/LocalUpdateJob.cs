using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal enum UpdateJobPhase { Idle, Checking, Staging, Paused, Verifying, Ready, Preparing, Deferred, Installing, Recovering, Completed, RolledBack, Cancelled, Failed, RecoveryRequired, Available, Current, Unavailable }
internal sealed record UpdateJobState(int Schema, long Revision, string Id, string Context, UpdateJobPhase Phase,
    string? Source = null, UpdateArtifact? Artifact = null, string? Version = null, long Bytes = 0, string? Reason = null, UpdateJobPhase? FailurePhase = null);
internal sealed record UpdateJobPreparation(bool Ready, string? Reason = null);

internal interface ILocalUpdateTrust
{
    Task<AuthorizedUpdate> AuthorizeAsync(string target, UpdateSelection selection, CancellationToken token);
    Task<VerifiedLocalUpdate> OpenPackageAsync(string path, UpdateSelection selection, CancellationToken token);
}
internal interface ILocalUpdateRuntime
{
    string Context { get; }
    bool RecoveryPending { get; }
    UpdateSelection Inspect();
    Task<UpdateJobPreparation> PrepareAsync(CancellationToken token);
    Task CancelPreparationAsync(CancellationToken token);
    Task<bool> DeployAsync(VerifiedLocalUpdate package);
    Task<bool> RecoverAsync();
}

/// <summary>One verified local or HTTPS-test operation at a time. Cancellation ends before deployment; interrupted mutation requires recovery.</summary>
internal sealed class LocalUpdateJob : IAsyncDisposable
{
    private readonly OwnedWorkspace _workspace;
    private readonly ILocalUpdateTrust _trust;
    private readonly ILocalUpdateRuntime _runtime;
    private readonly Func<long, CancellationToken, Task>? _stagingCheckpoint;
    private readonly FileStream _ownership;
    private readonly Lock _sync = new();
    private UpdateJobState _state;
    private Task<UpdateJobState>? _operation;
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _pause;
    private Task? _dispose;
    private bool _disposed;
    private bool _mutationArmed;
    private bool _finishingStage;
    private string StatePath => _workspace.Resolve("job.json");
    private string Partial => _workspace.Resolve("job-package.partial");
    private string DownloadPartial => _workspace.Resolve("job-download/package.partial");
    private string Package => _workspace.Resolve("job-stage/" + (_state.Artifact?.Kind == "msi" ? _state.Artifact.Sha256 + "/" : "") + (_state.Artifact?.FileName ?? "unassigned"));
    internal IUpdateLog? Diagnostics => _workspace.Diagnostics;
    public UpdateJobState Snapshot => Volatile.Read(ref _state);

    public LocalUpdateJob(OwnedWorkspace workspace, ILocalUpdateTrust trust, ILocalUpdateRuntime runtime,
        Func<long, CancellationToken, Task>? stagingCheckpoint = null)
    {
        _workspace = workspace; _trust = trust; _runtime = runtime;
        _stagingCheckpoint = stagingCheckpoint;
        _ownership = new FileStream(workspace.Resolve("job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _state = ReadState();
            new DeploymentRecovery(workspace).TrimUnreferencedSnapshots();
            if (_runtime.RecoveryPending || _state.Phase is UpdateJobPhase.Installing or UpdateJobPhase.Recovering or UpdateJobPhase.Preparing)
                _state = _state with { Phase = UpdateJobPhase.RecoveryRequired, Reason = "interrupted-operation" };
            else if (_state.Phase is UpdateJobPhase.Checking or UpdateJobPhase.Staging or UpdateJobPhase.Verifying)
                _state = _state with { Phase = UpdateJobPhase.Paused, Bytes = File.Exists(IsRemote(_state.Source) ? DownloadPartial : Partial) ? new FileInfo(IsRemote(_state.Source) ? DownloadPartial : Partial).Length : 0 };
        }
        catch { _ownership.Dispose(); throw; }
        UpdateLog.Write(Diagnostics, new(UpdateLogKind.SessionOpened, Snapshot.Phase, Version: Snapshot.Version, JobId: Snapshot.Id));
    }

    public Task<UpdateJobState> StageAsync(string source, CancellationToken token = default)
    {
        source = Path.GetFullPath(source);
        if (source.Length > 2048 || !File.Exists(source)) throw new InvalidDataException("Select a local package file.");
        if (source.Equals(Partial, StringComparison.OrdinalIgnoreCase) || source.StartsWith(_workspace.Resolve("job-stage") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the original package outside managed staging.");
        OwnedWorkspace.CheckAncestors(source);
        return Begin(async operationToken =>
        {
            var selection = _runtime.Inspect();
            var authorized = await _trust.AuthorizeAsync(Path.GetFileName(source), selection, operationToken);
            if (Snapshot.Artifact is { } prior && (prior != authorized.Artifact || Snapshot.Version != authorized.Release.Version))
                throw new InvalidDataException("The pending local package changed; cancel it before selecting another release.");
            Set(Snapshot with { Phase = UpdateJobPhase.Staging, Artifact = authorized.Artifact, Version = authorized.Release.Version });
            Directory.CreateDirectory(Path.GetDirectoryName(Package)!);
            if (File.Exists(Package))
            {
                using var ready = await _trust.OpenPackageAsync(Package, selection, operationToken);
                FinishStaging(authorized.Artifact.Length, operationToken);
                return;
            }
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(operationToken, _pause!.Token);
            try
            {
                await CopyLocalAsync(source, authorized.Artifact, transfer.Token);
                Set(Snapshot with { Phase = UpdateJobPhase.Verifying });
                await ResumableDownload.VerifyAsync(Partial, authorized.Artifact.Length, authorized.Artifact.Sha256, transfer.Token);
                transfer.Token.ThrowIfCancellationRequested();
                OwnedWorkspace.Promote(Partial, Package);
                using var verified = await _trust.OpenPackageAsync(Package, selection, transfer.Token);
                FinishStaging(authorized.Artifact.Length, transfer.Token);
            }
            catch (OperationCanceledException) when (!operationToken.IsCancellationRequested && _pause.IsCancellationRequested)
            {
                Set(Snapshot with { Phase = UpdateJobPhase.Paused, Bytes = File.Exists(Partial) ? new FileInfo(Partial).Length : 0 });
            }
        }, UpdateJobPhase.Checking, state =>
        {
            if (state.Phase is not (UpdateJobPhase.Idle or UpdateJobPhase.Paused or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack)) throw new InvalidOperationException("This job cannot stage another package.");
            if (state.Phase == UpdateJobPhase.Paused && !string.Equals(state.Source, source, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Resume the same local package, or cancel first.");
            if (state.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack) DeleteStaging();
            return state.Phase == UpdateJobPhase.Paused ? state : new(1, state.Revision, Guid.NewGuid().ToString("N"), _runtime.Context, UpdateJobPhase.Idle, source);
        }, token);
    }

    public Task<UpdateJobState> CheckAsync(IUpdateFeed feed, CancellationToken token = default) => Begin(async operationToken =>
    {
        try
        {
            var authorized = await _trust.AuthorizeAsync(feed.Target, _runtime.Inspect(), operationToken);
            operationToken.ThrowIfCancellationRequested();
            Set(Snapshot with { Phase = UpdateJobPhase.Available, Artifact = authorized.Artifact, Version = authorized.Release.Version });
        }
        catch (UpdateSelectionException selection)
        {
            operationToken.ThrowIfCancellationRequested();
            Set(Snapshot with { Phase = selection.Status is "current" or "no-release" ? UpdateJobPhase.Current : UpdateJobPhase.Unavailable, Reason = selection.Status });
        }
    }, UpdateJobPhase.Checking, state =>
    {
        if (state.Phase is not (UpdateJobPhase.Idle or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack or UpdateJobPhase.Available or UpdateJobPhase.Failed or UpdateJobPhase.Current or UpdateJobPhase.Unavailable) ||
            state.Phase == UpdateJobPhase.Failed && (state.Artifact != null || state.Bytes != 0))
            throw new InvalidOperationException("Finish or cancel the current attempt before checking another update.");
        if (state.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack) DeleteStaging();
        return new(1, state.Revision, Guid.NewGuid().ToString("N"), _runtime.Context, UpdateJobPhase.Idle, feed.PackageUri.AbsoluteUri);
    }, token);

    public Task<UpdateJobState> DownloadAsync(IUpdateFeed feed, HttpClient client, CancellationToken token = default)
    {
        string source = feed.PackageUri.AbsoluteUri;
        return Begin(async operationToken =>
        {
            var selection = _runtime.Inspect();
            var authorized = await _trust.AuthorizeAsync(Snapshot.Artifact?.FileName ?? feed.Target, selection, operationToken);
            if (Snapshot.Artifact is { } prior && (prior != authorized.Artifact || Snapshot.Version != authorized.Release.Version))
                throw new InvalidDataException("The pending release changed; cancel this attempt before downloading another release.");
            Set(Snapshot with { Phase = UpdateJobPhase.Staging, Artifact = authorized.Artifact, Version = authorized.Release.Version });
            Directory.CreateDirectory(Path.GetDirectoryName(Package)!);
            if (!File.Exists(Package))
            {
                var download = new OwnedWorkspace(_workspace.Resolve("job-download"));
                var result = await new ResumableDownload(client, download, _stagingCheckpoint).RunAsync(new(feed.GetPackageUri(authorized.Artifact), authorized.Artifact.Length, authorized.Artifact.Sha256),
                    _pause!.Token, new TransferProgress(bytes => Volatile.Write(ref _state, Snapshot with { Bytes = bytes })), operationToken);
                operationToken.ThrowIfCancellationRequested();
                if (result == DownloadOutcome.Paused)
                {
                    Set(Snapshot with { Phase = UpdateJobPhase.Paused, Bytes = File.Exists(DownloadPartial) ? new FileInfo(DownloadPartial).Length : 0 });
                    return;
                }
                Set(Snapshot with { Phase = UpdateJobPhase.Verifying });
                OwnedWorkspace.Promote(download.Resolve("package.verified"), Package);
            }
            using var verification = CancellationTokenSource.CreateLinkedTokenSource(operationToken, _pause!.Token);
            try
            {
                using var verified = await _trust.OpenPackageAsync(Package, selection, verification.Token);
                FinishStaging(authorized.Artifact.Length, verification.Token);
            }
            catch (OperationCanceledException) when (!operationToken.IsCancellationRequested && _pause.IsCancellationRequested)
            { Set(Snapshot with { Phase = UpdateJobPhase.Paused }); }
        }, UpdateJobPhase.Checking, state =>
        {
            if (state.Phase is not (UpdateJobPhase.Idle or UpdateJobPhase.Available or UpdateJobPhase.Paused or UpdateJobPhase.Failed or UpdateJobPhase.Cancelled or UpdateJobPhase.Completed or UpdateJobPhase.RolledBack))
                throw new InvalidOperationException("The job cannot download another package.");
            if (state.Phase is UpdateJobPhase.Paused or UpdateJobPhase.Failed or UpdateJobPhase.Available && state.Source != source)
                throw new InvalidOperationException("Resume the same feed, or cancel first.");
            if (state.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack) DeleteStaging();
            return state.Phase is UpdateJobPhase.Paused or UpdateJobPhase.Failed or UpdateJobPhase.Available ? state : new(1, state.Revision, Guid.NewGuid().ToString("N"), _runtime.Context, UpdateJobPhase.Idle, source);
        }, token);
    }
    private sealed class TransferProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
    private static bool IsRemote(string? source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public Task<UpdateJobState> ApplyAsync(CancellationToken token = default) => Begin(async operationToken =>
    {
        using var verified = await _trust.OpenPackageAsync(Package, _runtime.Inspect(), operationToken);
        if (Snapshot.Artifact != verified.Authorization.Artifact || Snapshot.Version != verified.Authorization.Release.Version)
            throw new InvalidDataException("The saved job does not match the freshly authorized package.");
        await verified.PrepareForDeploymentAsync(operationToken);
        var preparation = await _runtime.PrepareAsync(operationToken);
        if (!preparation.Ready)
        {
            await _runtime.CancelPreparationAsync(CancellationToken.None);
            Set(Snapshot with { Phase = UpdateJobPhase.Deferred, Reason = preparation.Reason });
            return;
        }
        lock (_sync) { operationToken.ThrowIfCancellationRequested(); _mutationArmed = true; }
        Set(Snapshot with { Phase = UpdateJobPhase.Installing });
        bool committed = await _runtime.DeployAsync(verified);
        if (!committed)
        {
            Set(Snapshot with { Phase = UpdateJobPhase.Recovering });
            committed = await _runtime.RecoverAsync();
        }
        Set(Snapshot with { Phase = committed ? UpdateJobPhase.Completed : UpdateJobPhase.RolledBack });
    }, UpdateJobPhase.Preparing, state => state.Phase is UpdateJobPhase.Ready or UpdateJobPhase.Deferred ? state : throw new InvalidOperationException("The job is not ready to apply."), token);

    public Task<UpdateJobState> RecoverAsync(CancellationToken token = default) => Begin(async _ =>
    {
        lock (_sync) _mutationArmed = true;
        bool committed = await _runtime.RecoverAsync();
        Set(Snapshot with { Phase = committed ? UpdateJobPhase.Completed : UpdateJobPhase.RolledBack });
    }, UpdateJobPhase.Recovering, state => state.Phase == UpdateJobPhase.RecoveryRequired ? state : throw new InvalidOperationException("Recovery is not pending."), token);

    public bool Pause()
    {
        lock (_sync)
        {
            if (_disposed || _finishingStage || Snapshot.Phase is not (UpdateJobPhase.Staging or UpdateJobPhase.Verifying) || _pause == null) return false;
            UpdateLog.Write(Diagnostics, new(UpdateLogKind.PauseRequested, Snapshot.Phase, JobId: Snapshot.Id));
            _pause.Cancel(); return true;
        }
    }
    public bool Cancel()
    {
        lock (_sync)
        {
            if (_disposed || _mutationArmed || _finishingStage || Snapshot.Phase == UpdateJobPhase.RecoveryRequired) return false;
            if (_cancel != null)
            {
                UpdateLog.Write(Diagnostics, new(UpdateLogKind.CancelRequested, Snapshot.Phase, JobId: Snapshot.Id));
                _cancel.Cancel(); return true;
            }
            if (Snapshot.Phase is not (UpdateJobPhase.Available or UpdateJobPhase.Ready or UpdateJobPhase.Deferred or UpdateJobPhase.Paused or UpdateJobPhase.Failed)) return false;
            DeleteStaging();
            Set(Snapshot with { Phase = UpdateJobPhase.Cancelled, Bytes = 0, Reason = null });
            return true;
        }
    }

    private Task<UpdateJobState> Begin(Func<CancellationToken, Task> action, UpdateJobPhase phase,
        Func<UpdateJobState, UpdateJobState> validate, CancellationToken token,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operation != null) throw new InvalidOperationException("An update operation is already active.");
            _state = validate(Snapshot);
            _cancel = new(); _pause = new(); _mutationArmed = false; _finishingStage = false;
            var cancellation = _cancel;
            _operation = Task.Run(async () =>
            {
                UpdateJobState completed;
                using var registration = token.Register(() =>
                {
                    lock (_sync) { if (ReferenceEquals(_cancel, cancellation) && !_mutationArmed && !_finishingStage) cancellation.Cancel(); }
                });
                try
                {
                    UpdateLog.Write(Diagnostics, new(UpdateLogKind.OperationStarted, phase, operation, Snapshot.Version, JobId: Snapshot.Id));
                    _cancel.Token.ThrowIfCancellationRequested();
                    Set(Snapshot with { Phase = phase, Reason = null, FailurePhase = null });
                    await action(_cancel.Token);
                    lock (_sync) { if (!_mutationArmed && !_finishingStage) _cancel.Token.ThrowIfCancellationRequested(); }
                }
                catch (Exception error)
                {
                    bool recovery = _mutationArmed;
                    if (phase == UpdateJobPhase.Preparing && !recovery)
                    {
                        try { await _runtime.CancelPreparationAsync(CancellationToken.None); }
                        catch (Exception cleanup)
                        {
                            UpdateLog.Write(Diagnostics, new(UpdateLogKind.CleanupFailed, Snapshot.Phase, operation, Error: cleanup, JobId: Snapshot.Id));
                            recovery = true;
                        }
                    }
                    bool cancelled = error is OperationCanceledException && _cancel.IsCancellationRequested;
                    UpdateLog.Write(Diagnostics, new(cancelled ? UpdateLogKind.OperationCancelled : UpdateLogKind.OperationFailed,
                        Snapshot.Phase, operation, Snapshot.Version, Error: error, JobId: Snapshot.Id));
                    if (cancelled && !recovery)
                    {
                        try { DeleteStaging(); }
                        catch { cancelled = false; }
                    }
                    var failure = Snapshot with
                    {
                        FailurePhase = Snapshot.Phase,
                        Phase = recovery ? UpdateJobPhase.RecoveryRequired : cancelled ? UpdateJobPhase.Cancelled : UpdateJobPhase.Failed,
                        Reason = error.GetType().Name,
                        Bytes = cancelled && !recovery ? 0 : Snapshot.Bytes
                    };
                    try { Set(failure); }
                    catch (Exception persistence)
                    {
                        UpdateLog.Write(Diagnostics, new(UpdateLogKind.CleanupFailed, failure.Phase, operation, Error: persistence, JobId: failure.Id));
                        Volatile.Write(ref _state, failure with { Reason = "state-persistence-failed" });
                    }
                }
                finally
                {
                    completed = Snapshot;
                    lock (_sync) { _cancel.Dispose(); _pause.Dispose(); _cancel = null; _pause = null; _operation = null; _mutationArmed = false; _finishingStage = false; }
                }
                return completed;
            });
            return _operation;
        }
    }

    private async Task CopyLocalAsync(string source, UpdateArtifact artifact, CancellationToken token)
    {
        long offset = File.Exists(Partial) ? new FileInfo(Partial).Length : 0;
        if (offset > artifact.Length) throw new InvalidDataException("Oversized local staging file.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != artifact.Length) throw new InvalidDataException("The local source length changed.");
        input.Position = offset;
        await using var output = new FileStream(Partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        output.Position = offset;
        byte[] buffer = new byte[65536];
        try
        {
            while (true)
            {
                int count = await input.ReadAsync(buffer, token);
                if (count == 0) break;
                if (offset + count > artifact.Length) throw new InvalidDataException("The local source grew during staging.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                offset += count;
                Volatile.Write(ref _state, Snapshot with { Bytes = offset });
                if (_stagingCheckpoint != null) await _stagingCheckpoint(offset, token);
            }
            if (offset != artifact.Length) throw new EndOfStreamException("The local source was truncated.");
        }
        finally { output.Flush(flushToDisk: true); }
    }

    private void Set(UpdateJobState state)
    {
        state = state with { Revision = checked(Snapshot.Revision + 1) };
        OwnedWorkspace.WriteJson(StatePath, state);
        Volatile.Write(ref _state, state);
        UpdateLog.Write(Diagnostics, new(UpdateLogKind.PhaseChanged, state.Phase, Version: state.Version, Reason: state.Reason, JobId: state.Id));
    }
    private void FinishStaging(long length, CancellationToken token)
    {
        lock (_sync) { token.ThrowIfCancellationRequested(); _finishingStage = true; }
        Set(Snapshot with { Phase = UpdateJobPhase.Ready, Bytes = length });
    }
    private UpdateJobState ReadState()
    {
        string anchorPath = _workspace.Resolve("job.anchor");
        bool initialized = File.Exists(anchorPath);
        if (initialized && OwnedWorkspace.ReadJson<string>(anchorPath) != _runtime.Context) throw new InvalidDataException("The job belongs to another installation.");
        UpdateJobState Validate(UpdateJobState state)
        {
            if (state.Schema != 1 || state.Revision < 0 || state.Context != _runtime.Context || !Enum.IsDefined(state.Phase) ||
                state.FailurePhase is { } failed && !Enum.IsDefined(failed) || !Guid.TryParseExact(state.Id, "N", out _) ||
                state.Source is { } source && ((!Path.IsPathFullyQualified(source) && !IsRemote(source)) || source.Length > 2048) || state.Bytes < 0 ||
                state.Artifact is { } artifact && (artifact.Length <= 0 || artifact.Length > 1024L * 1024 * 1024 || !UpdateTrustMetadata.IsDigest(artifact.Sha256) ||
                artifact.FileName != Path.GetFileName(artifact.FileName) || artifact.FileName.Length > 160 || artifact.Kind is not ("portable" or "msi")))
                throw new InvalidDataException("Invalid local update job state.");
            return state;
        }
        var current = File.Exists(StatePath) ? Validate(OwnedWorkspace.ReadJson<UpdateJobState>(StatePath)) : null;
        if (File.Exists(StatePath + ".new"))
        {
            try
            {
                var pending = Validate(OwnedWorkspace.ReadJson<UpdateJobState>(StatePath + ".new"));
                if (current != null && pending.Revision <= current.Revision) throw new InvalidDataException("The pending job record does not advance its revision.");
                OwnedWorkspace.Promote(StatePath + ".new", StatePath); current = pending;
            }
            catch (System.Text.Json.JsonException) when (current != null)
            {
                // Job intent is recoverable from the actual deployment journal; TUF/version-floor records never use this fallback.
                OwnedWorkspace.Promote(StatePath + ".new", _workspace.Resolve("job.interrupted"));
                current = current with { Revision = checked(current.Revision + 1), Phase = UpdateJobPhase.RecoveryRequired, Reason = "incomplete-job-record" };
                OwnedWorkspace.WriteJson(StatePath, current);
            }
        }
        if (current == null)
        {
            if (initialized) throw new InvalidDataException("The initialized job record is missing.");
            current = new(1, 0, Guid.NewGuid().ToString("N"), _runtime.Context, UpdateJobPhase.Idle);
            OwnedWorkspace.WriteJson(StatePath, current);
        }
        if (!initialized) OwnedWorkspace.WriteJson(anchorPath, _runtime.Context);
        return current;
    }
    private void DeleteStaging()
    {
        File.Delete(Partial);
        if (Snapshot.Artifact != null && !(Snapshot.Artifact.Kind == "msi" && Snapshot.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack))
        {
            try { File.Delete(Package); }
            catch (DirectoryNotFoundException) { }
        }
        foreach (string name in new[] { "package.partial", "package.verified", "download.json" })
        {
            try { File.Delete(_workspace.Resolve("job-download/" + name)); }
            catch (DirectoryNotFoundException) { }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose != null) return new(_dispose);
            _disposed = true;
            if (!_mutationArmed) _cancel?.Cancel();
            _dispose = FinishAsync(_operation);
            return new(_dispose);
        }
    }
    private async Task FinishAsync(Task? operation)
    {
        try
        {
            if (operation != null) await operation.ConfigureAwait(false);
            if (Snapshot.Phase is UpdateJobPhase.Completed or UpdateJobPhase.RolledBack && Snapshot.Artifact?.Kind == "portable" && !_runtime.RecoveryPending)
            {
                try { DeleteStaging(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { UpdateLog.Write(Diagnostics, new(UpdateLogKind.CleanupFailed, Snapshot.Phase, Error: error, JobId: Snapshot.Id)); }
            }
        }
        finally { _ownership.Dispose(); }
    }
}
