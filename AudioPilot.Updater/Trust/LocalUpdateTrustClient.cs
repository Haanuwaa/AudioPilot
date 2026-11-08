namespace AudioPilot.Updater.Trust;

internal sealed record UpdateTrustState(int Schema, string AnchorHash, string[] Roots, long Epoch, long Sequence,
    string? ReleaseHash, string? Version, string? ArtifactSetHash, DateTimeOffset SeenUtc);
internal sealed record AuthorizedUpdate(UpdateRelease Release, UpdateArtifact Artifact);

/// <summary>Local-only trust policy with an external bootstrap pin, sequential root rotation and durable replay state.</summary>
internal sealed class LocalUpdateTrustClient(OwnedWorkspace workspace, string bootstrapHash, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string StatePath => workspace.Resolve("trust-state.json");
    private FileStream Lock() => new(workspace.Resolve("trust.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public void Initialize(byte[] bootstrap)
    {
        using var ownership = Lock();
        if (File.Exists(StatePath)) throw new InvalidOperationException("Update trust has already been initialized.");
        if (!UpdateTrustMetadata.IsDigest(bootstrapHash) || UpdateTrustMetadata.Digest(bootstrap) != bootstrapHash)
            throw new System.Security.Cryptography.CryptographicException("Update bootstrap root does not match its external pin.");
        var root = ReadBootstrap(bootstrap);
        DateTimeOffset now = _time.GetUtcNow();
        if (root.ExpiresUtc <= now) throw new InvalidDataException("The update root has expired.");
        Save(new(1, bootstrapHash, [Convert.ToBase64String(bootstrap)], root.Epoch, 0, null, null, null, now));
    }

    public void RotateRoot(byte[] nextDocument)
    {
        using var ownership = Lock();
        var (state, previous) = Load();
        DateTimeOffset now = EffectiveTime(state);
        var next = ReadRotation(nextDocument, previous);
        if (next.ExpiresUtc <= now) throw new InvalidDataException("The replacement update root has expired.");
        if (state.Roots.Length >= 32) throw new InvalidDataException("The local updater root-chain bound was reached.");
        Save(state with
        {
            Roots = [.. state.Roots, Convert.ToBase64String(nextDocument)],
            Epoch = next.Epoch,
            Sequence = next.Epoch == state.Epoch ? state.Sequence : 0,
            ReleaseHash = next.Epoch == state.Epoch ? state.ReleaseHash : null,
            Version = next.Epoch == state.Epoch ? state.Version : null,
            ArtifactSetHash = next.Epoch == state.Epoch ? state.ArtifactSetHash : null,
            SeenUtc = now,
        });
    }

    public AuthorizedUpdate Authorize(byte[] releaseDocument, UpdateSelection selection)
    {
        UpdateTrustMetadata.ValidateSelection(selection);
        using var ownership = Lock();
        var (state, root) = Load();
        DateTimeOffset now = EffectiveTime(state);
        try
        {
            if (root.ExpiresUtc <= now) throw new InvalidDataException("The update root has expired.");
            var envelope = UpdateTrustMetadata.Decode<SignedUpdateDocument>(releaseDocument);
            byte[] payload = UpdateTrustMetadata.Payload(envelope);
            UpdateTrustMetadata.VerifyThreshold("release", envelope, payload, root.ReleaseKeys, root.ReleaseThreshold);
            var release = UpdateTrustMetadata.Decode<UpdateRelease>(payload);
            ValidateRelease(release, root, now);
            string releaseHash = UpdateTrustMetadata.Digest(payload);
            if (release.Sequence < state.Sequence || (release.Sequence == state.Sequence && releaseHash != state.ReleaseHash))
                throw new InvalidDataException("Update metadata was replayed or changed at an existing sequence.");
            Version version = UpdateTrustMetadata.ParseVersion(release.Version);
            string artifactHash = ArtifactSetHash(release.Artifacts);
            if (state.Version != null)
            {
                int comparison = version.CompareTo(UpdateTrustMetadata.ParseVersion(state.Version));
                if (comparison < 0 || (comparison == 0 && artifactHash != state.ArtifactSetHash))
                    throw new InvalidDataException("Published update versions are immutable and cannot move backward.");
            }
            // An authenticated catalog advances replay protection even when this machine cannot use its packages.
            var observed = state with
            {
                Epoch = root.Epoch,
                Sequence = release.Sequence,
                ReleaseHash = releaseHash,
                Version = release.Version,
                ArtifactSetHash = artifactHash,
                SeenUtc = now
            };
            Save(observed);
            state = observed;
            if (version <= UpdateTrustMetadata.ParseVersion(selection.InstalledVersion) ||
                UpdateTrustMetadata.ParseVersion(selection.HelperVersion) < UpdateTrustMetadata.ParseVersion(release.MinimumHelperVersion))
                throw new InvalidDataException("The update is not newer or requires a newer helper.");
            var artifact = release.Artifacts.SingleOrDefault(candidate => candidate.Kind == selection.Kind &&
                candidate.Architecture == selection.Architecture && candidate.Runtime == selection.Runtime)
                ?? throw new InvalidDataException("The signed release has no matching package variant.");
            return new(release, artifact);
        }
        catch
        {
            if (now > state.SeenUtc) Save(state with { SeenUtc = now });
            throw;
        }
    }

    public async Task<VerifiedLocalUpdate> OpenPackageAsync(byte[] releaseDocument, UpdateSelection selection, string path, CancellationToken token)
    {
        releaseDocument = [.. releaseDocument];
        var authorized = Authorize(releaseDocument, selection);
        path = Path.GetFullPath(path);
        OwnedWorkspace.CheckAncestors(path);
        if (Path.GetFileName(path) != authorized.Artifact.FileName) throw new InvalidDataException("The local package name does not match signed metadata.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != authorized.Artifact.Length)
                throw new InvalidDataException("The local update length does not match signed metadata.");
            stream.Position = 0;
            string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token));
            if (actual != authorized.Artifact.Sha256) throw new System.Security.Cryptography.CryptographicException("The local package hash does not match signed metadata.");
            stream.Position = 0;
            return new(() => Authorize(releaseDocument, selection), authorized, path, stream);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private (UpdateTrustState State, UpdateRoot Root) Load()
    {
        var state = UpdateTrustMetadata.Decode<UpdateTrustState>(UpdateTrustMetadata.ReadFile(StatePath, 3 * 1024 * 1024), 3 * 1024 * 1024);
        if (state.Schema != 1 || state.AnchorHash != bootstrapHash || !UpdateTrustMetadata.IsDigest(bootstrapHash) ||
            state.Roots.Length is < 1 or > 32 || state.Sequence is < 0 or > 9007199254740991 || state.SeenUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid persisted update trust state.");
        byte[] initial = Convert.FromBase64String(state.Roots[0]);
        if (UpdateTrustMetadata.Digest(initial) != bootstrapHash) throw new System.Security.Cryptography.CryptographicException("Stored bootstrap root was replaced.");
        var root = ReadBootstrap(initial);
        foreach (string item in state.Roots.Skip(1)) root = ReadRotation(Convert.FromBase64String(item), root);
        if (state.Epoch != root.Epoch || (state.Sequence == 0 ? state.ReleaseHash != null : !UpdateTrustMetadata.IsDigest(state.ReleaseHash ?? "")) ||
            (state.Version == null ? state.ArtifactSetHash != null : !UpdateTrustMetadata.IsDigest(state.ArtifactSetHash ?? "")))
            throw new InvalidDataException("Inconsistent persisted update trust state.");
        if (state.Version != null) _ = UpdateTrustMetadata.ParseVersion(state.Version);
        return (state, root);
    }
    private static UpdateRoot ReadBootstrap(byte[] bytes)
    {
        var envelope = UpdateTrustMetadata.Decode<SignedUpdateDocument>(bytes);
        byte[] payload = UpdateTrustMetadata.Payload(envelope);
        var root = UpdateTrustMetadata.Decode<UpdateRoot>(payload);
        UpdateTrustMetadata.ValidateRoot(root);
        if (root.Version != 1 || root.Epoch != 1) throw new InvalidDataException("A bootstrap root must start at version and epoch 1.");
        UpdateTrustMetadata.VerifyThreshold("root", envelope, payload, root.RootKeys, root.RootThreshold);
        return root;
    }
    private static UpdateRoot ReadRotation(byte[] bytes, UpdateRoot previous)
    {
        var envelope = UpdateTrustMetadata.Decode<SignedUpdateDocument>(bytes);
        byte[] payload = UpdateTrustMetadata.Payload(envelope);
        UpdateTrustMetadata.VerifyThreshold("root", envelope, payload, previous.RootKeys, previous.RootThreshold);
        var next = UpdateTrustMetadata.Decode<UpdateRoot>(payload);
        UpdateTrustMetadata.ValidateRoot(next);
        if (next.Version != previous.Version + 1 || next.Epoch < previous.Epoch || next.Epoch > previous.Epoch + 1)
            throw new InvalidDataException("Update root rotation must be sequential.");
        bool releaseKeysChanged = next.ReleaseThreshold != previous.ReleaseThreshold ||
            !next.ReleaseKeys.Select(key => key.Id).Order(StringComparer.Ordinal).SequenceEqual(previous.ReleaseKeys.Select(key => key.Id).Order(StringComparer.Ordinal));
        if (releaseKeysChanged && next.Epoch == previous.Epoch) throw new InvalidDataException("Release-key rotation requires a new epoch.");
        UpdateTrustMetadata.VerifyThreshold("root", envelope, payload, next.RootKeys, next.RootThreshold);
        return next;
    }
    private DateTimeOffset EffectiveTime(UpdateTrustState state)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now < state.SeenUtc - TimeSpan.FromMinutes(5)) throw new InvalidDataException("The clock moved backward; update trust cannot be established.");
        return now > state.SeenUtc ? now : state.SeenUtc;
    }
    private static void ValidateRelease(UpdateRelease release, UpdateRoot root, DateTimeOffset now)
    {
        if (release.Schema != 1 || release.Product != "AudioPilot" || release.Channel != "local-test" || release.Epoch != root.Epoch ||
            release.Sequence is < 1 or > 9007199254740991 || release.IssuedUtc.Offset != TimeSpan.Zero || release.ExpiresUtc.Offset != TimeSpan.Zero ||
            release.IssuedUtc > now + TimeSpan.FromMinutes(5) || release.ExpiresUtc <= now || release.ExpiresUtc <= release.IssuedUtc ||
            release.ExpiresUtc - release.IssuedUtc > TimeSpan.FromDays(7) || release.Artifacts.Length is < 1 or > 12 || release.Artifacts.Any(artifact => artifact is null))
            throw new InvalidDataException("Invalid, expired or incompatible signed update release.");
        _ = UpdateTrustMetadata.ParseVersion(release.Version);
        _ = UpdateTrustMetadata.ParseVersion(release.MinimumHelperVersion);
        var variants = new HashSet<(string, string, string)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in release.Artifacts)
        {
            UpdateTrustMetadata.ValidateVariant(artifact.Kind, artifact.Architecture, artifact.Runtime);
            if (artifact.Length is <= 0 or > 1024L * 1024 * 1024 || !UpdateTrustMetadata.IsDigest(artifact.Sha256) ||
                artifact.FileName.Length is < 1 or > 160 || artifact.FileName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '-' and not '_') ||
                artifact.FileName.StartsWith('.') || artifact.FileName.Contains("..", StringComparison.Ordinal) ||
                IsDeviceName(artifact.FileName) ||
                !artifact.FileName.EndsWith(artifact.Kind == "msi" ? ".msi" : ".zip", StringComparison.Ordinal) ||
                !variants.Add((artifact.Kind, artifact.Architecture, artifact.Runtime)) || !names.Add(artifact.FileName))
                throw new InvalidDataException("Invalid or ambiguous signed update artifact.");
        }
    }
    private static bool IsDeviceName(string fileName)
    {
        string stem = fileName.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
    private static string ArtifactSetHash(UpdateArtifact[] artifacts) => UpdateTrustMetadata.Digest(UpdateTrustMetadata.Encode(
        artifacts.OrderBy(artifact => artifact.Kind, StringComparer.Ordinal).ThenBy(artifact => artifact.Architecture, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Runtime, StringComparer.Ordinal).ToArray()));
    private void Save(UpdateTrustState state) => OwnedWorkspace.WriteJson(StatePath, state);
}

/// <summary>Holds verified bytes against replacement while the caller prepares shutdown and deploys.</summary>
internal sealed class VerifiedLocalUpdate : IDisposable
{
    private readonly Func<AuthorizedUpdate> _reauthorize;
    private readonly FileStream _stream;
    private bool _disposed;
    public AuthorizedUpdate Authorization { get; }
    public string Path { get; }
    internal VerifiedLocalUpdate(Func<AuthorizedUpdate> reauthorize, AuthorizedUpdate authorization, string path, FileStream stream)
    { _reauthorize = reauthorize; Authorization = authorization; Path = path; _stream = stream; }
    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = _reauthorize();
        if (current.Artifact != Authorization.Artifact) throw new InvalidDataException("The verified package authorization changed.");
        OwnedWorkspace.CheckAncestors(Path);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _stream.Dispose(); }
}
