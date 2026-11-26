namespace AudioPilot.Updater.Trust;

internal sealed record AuthorizedUpdate(string Version, UpdateArtifact Artifact, WindowsPublisherPolicy? WindowsPublisher = null, UpdateArtifact? Delta = null);

/// <summary>Holds verified bytes against replacement while the caller prepares shutdown and deploys.</summary>
internal sealed class VerifiedLocalUpdate : IDisposable
{
    private readonly Func<AuthorizedUpdate> _reauthorize;
    private readonly Func<CancellationToken, Task<AuthorizedUpdate>>? _reauthorizeAsync;
    private readonly IDisposable _packageLease;
    private readonly TimeProvider _time;
    private long? _deploymentAuthorizedAt;
    private bool _disposed;
    public AuthorizedUpdate Authorization { get; }
    public string Path { get; }
    internal string? PreparedGeneration { get; }
    internal string? BaseVersion { get; }
    internal VerifiedLocalUpdate(Func<AuthorizedUpdate> reauthorize, AuthorizedUpdate authorization, string path, IDisposable packageLease,
        TimeProvider? timeProvider = null, Func<CancellationToken, Task<AuthorizedUpdate>>? reauthorizeAsync = null,
        string? preparedGeneration = null, string? baseVersion = null)
    {
        _reauthorize = reauthorize; _reauthorizeAsync = reauthorizeAsync; Authorization = authorization; Path = path;
        _packageLease = packageLease; _time = timeProvider ?? TimeProvider.System;
        PreparedGeneration = preparedGeneration; BaseVersion = baseVersion;
    }

    internal async Task PrepareForDeploymentAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_deploymentAuthorizedAt == null)
        {
            RequireSame(_reauthorizeAsync == null ? _reauthorize() : await _reauthorizeAsync(token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            OwnedWorkspace.CheckAncestors(Path);
            _deploymentAuthorizedAt = _time.GetTimestamp();
        }
        else Revalidate();
    }

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deploymentAuthorizedAt is long authorized)
        {
            if (_time.GetElapsedTime(authorized) > TimeSpan.FromMinutes(2))
                throw new InvalidDataException("The authorized deployment handoff expired; retry while AudioPilot is running.");
        }
        else
        {
            RequireSame(_reauthorize());
        }
        OwnedWorkspace.CheckAncestors(Path);
    }
    private void RequireSame(AuthorizedUpdate current)
    {
        if (current.Artifact != Authorization.Artifact || current.Version != Authorization.Version || current.WindowsPublisher != Authorization.WindowsPublisher ||
            PreparedGeneration != null && current.Delta != Authorization.Delta)
            throw new InvalidDataException("The verified package authorization changed.");
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _packageLease.Dispose(); }
}
