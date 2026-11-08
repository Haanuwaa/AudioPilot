using System.Net.Http;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater.Ui;

/// <summary>Owns UI operations and safe closing without giving the window ownership of installer cancellation.</summary>
internal sealed class UpdaterSession(LocalUpdateJob job, IAsyncDisposable runtime, UpdateSelection installed, IUpdateFeed? feed = null, HttpClient? downloadClient = null) : IAsyncDisposable
{
    private readonly LocalUpdateJob _job = job;
    private readonly IAsyncDisposable _runtime = runtime;
    private readonly HttpClient? _downloadClient = feed == null ? null : downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));
    public IUpdateFeed? Feed { get; } = feed;
    private Task<UpdateJobState>? _active;
    private Task? _closing;
    private bool _closed;
    public UpdateJobState Snapshot => _job.Snapshot;
    public bool IsBusy => _active is { IsCompleted: false };
    public bool IsClosing => _closing is { IsCompleted: false };
    public UpdateSelection Installed { get; private set; } = installed;
    public Models.AppTheme Theme { get; init; } = Models.AppTheme.System;
    internal static UpdaterSession CreateForFeed(OwnedWorkspace workspace, ILocalUpdateTrust trust, InstalledUpdateRuntime runtime, IUpdateFeed feed, ILocalUpdateRuntime? operations = null, Models.AppTheme theme = Models.AppTheme.System,
        Func<long, CancellationToken, Task>? checkpoint = null)
    {
        var installed = runtime.Inspect();
        var client = feed.CreateClient();
        try { return new(new LocalUpdateJob(workspace, trust, operations ?? runtime, checkpoint), runtime, installed, feed, client) { Theme = theme }; }
        catch { client.Dispose(); throw; }
    }

    public Task<UpdateJobState> VerifyAsync(string source) => Run(() => Feed == null ? _job.StageAsync(source) : _job.DownloadAsync(Feed, _downloadClient!));
    public Task<UpdateJobState> CheckAsync() => Run(() => _job.CheckAsync(Feed ?? throw new InvalidOperationException("No feed is connected.")));
    public void ApproveDownload(UpdateJobState shown) => _job.ApproveDownload(shown);
    public Task<UpdateJobState> InstallAsync() => Run(() => _job.ApplyAsync());
    public Task<UpdateJobState> RecoverAsync() => Run(() => _job.RecoverAsync());
    public bool Pause() => !_closed && _job.Pause();
    public bool Cancel() => !_closed && _job.Cancel();

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
        bool active = IsBusy;
        if (active)
        {
            if (Snapshot.Phase is UpdateJobPhase.Staging or UpdateJobPhase.Verifying) _job.Pause();
            else _job.Cancel();
            await _active!.ConfigureAwait(false);
        }
        if (active && Snapshot.Phase == UpdateJobPhase.RecoveryRequired)
            throw new InvalidOperationException("Recovery needs attention. Reopen this local workspace to retry safely.");
        await _job.DisposeAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
        _downloadClient?.Dispose();
        _closed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        try { await CloseSafelyAsync().ConfigureAwait(false); }
        finally
        {
            await _job.DisposeAsync().ConfigureAwait(false);
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _downloadClient?.Dispose();
            _closed = true;
        }
    }
}
