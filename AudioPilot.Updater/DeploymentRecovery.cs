namespace AudioPilot.Updater;

internal sealed record RecoveryRecord(int Schema, string DiagnosticId, string DataRoot, string Snapshot,
    string PreviousApp, bool Visible, CandidateOwner? Candidate, bool DeploymentCommitted = false, MsiOptions? InstallerOptions = null,
    CandidateOwner? Original = null, long Revision = 1, bool ConfigurationRestored = false, bool Completed = false,
    bool Managed = false, string? PreviousVersion = null, string? TargetVersion = null, string? LocalStateRoot = null, string? LocalSnapshot = null);

/// <summary>Records configuration recovery before replacement and child identity before readiness is awaited.</summary>
internal sealed class DeploymentRecovery(OwnedWorkspace workspace)
{
    private string Path => workspace.Resolve("recovery.json");
    public bool IsPending => (File.Exists(Path) || File.Exists(Path + ".new")) && !Read().Completed;
    public RecoveryRecord Read()
    {
        var current = File.Exists(Path) ? Validate(OwnedWorkspace.ReadJson<RecoveryRecord>(Path)) : null;
        if (File.Exists(Path + ".new"))
        {
            var pending = Validate(OwnedWorkspace.ReadJson<RecoveryRecord>(Path + ".new"));
            if (current != null && pending.Revision <= current.Revision) throw new InvalidDataException("Recovery state did not advance its revision.");
            OwnedWorkspace.Promote(Path + ".new", Path);
            current = pending;
        }
        return current ?? throw new InvalidDataException("The recovery record is missing.");
    }

    private static RecoveryRecord Validate(RecoveryRecord state)
    {
        if (state.Schema != 1 || !Guid.TryParseExact(state.DiagnosticId, "N", out _) ||
            state.Revision <= 0 || !System.IO.Path.IsPathFullyQualified(state.DataRoot) || !System.IO.Path.IsPathFullyQualified(state.PreviousApp))
            throw new InvalidDataException("Invalid deployment recovery state.");
        if (state.Candidate is { } owner && (owner.Pid <= 0 || owner.StartedUtcTicks <= 0 || !System.IO.Path.IsPathFullyQualified(owner.Path)))
            throw new InvalidDataException("Invalid recovery process identity.");
        if (state.Original is { } original && (original.Pid <= 0 || original.StartedUtcTicks <= 0 || !System.IO.Path.IsPathFullyQualified(original.Path) || original.Path != state.PreviousApp))
            throw new InvalidDataException("Invalid original process identity.");
        return state;
    }
    public void Begin(string diagnosticId, string dataRoot, string previousApp, bool visible, MsiOptions? installerOptions = null, CandidateOwner? original = null,
        bool managed = false, string? previousVersion = null, string? targetVersion = null, string? localStateRoot = null)
    {
        if (IsPending) throw new InvalidOperationException("Recover the interrupted deployment first.");
        long revision = File.Exists(Path) ? checked(Read().Revision + 1) : 1;
        string snapshot = new ConfigurationSnapshot(workspace).Capture(dataRoot);
        string? local = localStateRoot != null && !localStateRoot.Equals(dataRoot, StringComparison.OrdinalIgnoreCase) ? new ConfigurationSnapshot(workspace).Capture(localStateRoot, placementOnly: true) : null;
        OwnedWorkspace.WriteJson(Path, new RecoveryRecord(1, diagnosticId, dataRoot, snapshot, previousApp, visible, null, InstallerOptions: installerOptions, Original: original, Revision: revision,
            Managed: managed, PreviousVersion: previousVersion, TargetVersion: targetVersion, LocalStateRoot: localStateRoot, LocalSnapshot: local));
    }
    private void Update(RecoveryRecord record) => OwnedWorkspace.WriteJson(Path, record with { Revision = checked(record.Revision + 1) });
    public void SetCandidate(CandidateOwner owner) => Update(Read() with { Candidate = owner });
    public void MarkCommitted() => Update(Read() with { DeploymentCommitted = true });
    public async Task WaitForCandidateExitAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (Read().Candidate is { } candidate) await candidate.WaitForExitAsync(timeout.Token);
    }
    public void RestoreConfiguration(string expectedRoot)
    {
        var record = Read();
        if (record.ConfigurationRestored) return;
        if (record.Completed || record.DeploymentCommitted) throw new InvalidOperationException("A settled deployment cannot restore its previous configuration.");
        new ConfigurationSnapshot(workspace).Restore(record.Snapshot, expectedRoot);
        if (record.LocalSnapshot != null && record.LocalStateRoot != null) new ConfigurationSnapshot(workspace).Restore(record.LocalSnapshot, record.LocalStateRoot);
        Update(record with { ConfigurationRestored = true });
    }
    public void Complete()
    {
        var record = Read();
        if (!record.Completed) Update(record with { Completed = true });
    }
}
