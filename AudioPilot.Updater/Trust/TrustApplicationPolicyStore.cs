namespace AudioPilot.Updater.Trust;

internal sealed record TrustPolicyAnchor(int Schema, string BootstrapHash);

/// <summary>A pending flushed floor is recovered before authorizing another package; it is never silently discarded.</summary>
internal sealed class TrustApplicationPolicyStore(OwnedWorkspace workspace, string bootstrapHash)
{
    private string Path => workspace.Resolve("tuf-application-policy.json");
    private string Anchor => workspace.Resolve("tuf-application-policy.anchor");

    public TufApplicationPolicy Read()
    {
        bool initialized = File.Exists(Anchor);
        if (initialized)
        {
            var anchor = OwnedWorkspace.ReadJson<TrustPolicyAnchor>(Anchor);
            if (anchor.Schema != 1 || anchor.BootstrapHash != bootstrapHash) throw new InvalidDataException("Invalid application trust anchor.");
        }
        TufApplicationPolicy? current = File.Exists(Path) ? Validate(OwnedWorkspace.ReadJson<TufApplicationPolicy>(Path)) : null;
        if (File.Exists(Path + ".new"))
        {
            var pending = Validate(OwnedWorkspace.ReadJson<TufApplicationPolicy>(Path + ".new"));
            if (current != null) RequireAdvance(current, pending);
            OwnedWorkspace.Promote(Path + ".new", Path);
            current = pending;
        }
        if (current == null)
        {
            if (initialized || Directory.Exists(workspace.Resolve("tuf-cache"))) throw new InvalidDataException("Application trust state is missing; it cannot be reset automatically.");
            current = Seal(new(1, bootstrapHash, "0.0.0", new string('0', 64)));
            OwnedWorkspace.WriteJson(Path, current);
        }
        if (!initialized) OwnedWorkspace.WriteJson(Anchor, new TrustPolicyAnchor(1, bootstrapHash));
        return current;
    }

    public void Advance(TufApplicationPolicy next)
    {
        var current = Read();
        next = Validate(Seal(next with { LatestVersion = current.LatestVersion }));
        RequireAdvance(current, next);
        if (next != current) OwnedWorkspace.WriteJson(Path, next);
    }

    public void Observe(string version)
    {
        _ = UpdateTrustMetadata.ParseVersion(version);
        var current = Read();
        var next = Seal(current with { LatestVersion = version });
        RequireAdvance(current, next);
        if (next != current) OwnedWorkspace.WriteJson(Path, next);
    }

    private TufApplicationPolicy Validate(TufApplicationPolicy state)
    {
        if (state.Schema != 1 || state.BootstrapHash != bootstrapHash || !UpdateTrustMetadata.IsDigest(state.TargetHash) ||
            !UpdateTrustMetadata.IsDigest(state.Checksum) || Seal(state).Checksum != state.Checksum) throw new InvalidDataException("Invalid application trust state.");
        _ = UpdateTrustMetadata.ParseVersion(state.Version);
        if (state.LatestVersion != null) _ = UpdateTrustMetadata.ParseVersion(state.LatestVersion);
        return state;
    }

    private static TufApplicationPolicy Seal(TufApplicationPolicy state) => state with
    {
        Checksum = UpdateTrustMetadata.Digest(UpdateTrustMetadata.Encode(state with { Checksum = "" }))
    };

    private static void RequireAdvance(TufApplicationPolicy current, TufApplicationPolicy next)
    {
        if (UpdateTrustMetadata.ParseVersion(next.LatestVersion ?? next.Version) < UpdateTrustMetadata.ParseVersion(current.LatestVersion ?? current.Version))
            throw new InvalidDataException("The latest stable release moved backward.");
        int comparison = UpdateTrustMetadata.ParseVersion(next.Version).CompareTo(UpdateTrustMetadata.ParseVersion(current.Version));
        if (comparison < 0 || comparison == 0 && next.TargetHash != current.TargetHash) throw new InvalidDataException("A published application version changed or moved backward.");
    }
}
