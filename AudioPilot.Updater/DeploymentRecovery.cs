using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioPilot.Updater;

internal sealed record RecoveryRecord(int Schema, string DiagnosticId, string DataRoot, string Snapshot,
    string PreviousApp, bool Visible, CandidateOwner? Candidate, bool DeploymentCommitted = false, MsiOptions? InstallerOptions = null,
    CandidateOwner? Original = null, long Revision = 1, bool ConfigurationRestored = false, bool Completed = false,
    bool Managed = false, string? PreviousVersion = null, string? TargetVersion = null, string? LocalStateRoot = null, string? LocalSnapshot = null,
    Trust.UpdateSelection? PreviousInstallation = null);

/// <summary>Records configuration recovery before replacement and child identity before readiness is awaited.</summary>
internal sealed class DeploymentRecovery(OwnedWorkspace workspace)
{
    private string Path => workspace.Resolve("recovery.json");
    private string PendingPath => workspace.Resolve("recovery.json.new");
    public bool IsPending => (File.Exists(Path) || File.Exists(PendingPath)) && !Read().Completed;
    public RecoveryRecord Read() => Read(out _);

    private RecoveryRecord Read(out bool interrupted)
    {
        interrupted = false;
        var current = File.Exists(Path) ? Validate(OwnedWorkspace.ReadJson<RecoveryRecord>(Path)) : null;
        string pendingPath = PendingPath;
        if (File.Exists(pendingPath))
        {
            RecoveryRecord pending;
            try { pending = Validate(OwnedWorkspace.ReadJson<RecoveryRecord>(pendingPath)); }
            catch (JsonException) when (current is { Completed: false } && IsIncompleteRecord(pendingPath))
            {
                interrupted = true;
                return current;
            }
            if (current != null && pending.Revision <= current.Revision) throw new InvalidDataException("Recovery state did not advance its revision.");
            OwnedWorkspace.Promote(pendingPath, Path);
            current = pending;
        }
        return current ?? throw new InvalidDataException("The recovery record is missing.");
    }

    private static bool IsIncompleteRecord(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > OwnedWorkspace.MaximumJsonBytes) return false;
        Span<byte> bytes = stackalloc byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (bytes.IsEmpty) return true;

        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) return false;
        }
        return true;
    }

    private static RecoveryRecord Validate(RecoveryRecord state)
    {
        if (state.Schema != 1 || !Guid.TryParseExact(state.DiagnosticId, "N", out _) ||
            state.Revision <= 0 || !System.IO.Path.IsPathFullyQualified(state.DataRoot) || !System.IO.Path.IsPathFullyQualified(state.PreviousApp) ||
            !Guid.TryParseExact(state.Snapshot, "N", out _) || state.LocalSnapshot != null && !Guid.TryParseExact(state.LocalSnapshot, "N", out _))
            throw new InvalidDataException("Invalid deployment recovery state.");
        if (state.Candidate is { } owner && (owner.Pid <= 0 || owner.StartedUtcTicks <= 0 || !System.IO.Path.IsPathFullyQualified(owner.Path)))
            throw new InvalidDataException("Invalid recovery process identity.");
        if (state.Original is { } original && (original.Pid <= 0 || original.StartedUtcTicks <= 0 || !System.IO.Path.IsPathFullyQualified(original.Path) || original.Path != state.PreviousApp))
            throw new InvalidDataException("Invalid original process identity.");
        if (state.PreviousInstallation is { } installation)
        {
            Trust.UpdateTrustMetadata.ValidateSelection(installation);
            if (installation.InstalledVersion != state.PreviousVersion) throw new InvalidDataException("Recovery installation versions disagree.");
        }
        return state;
    }
    public void Begin(string diagnosticId, string dataRoot, string previousApp, bool visible, MsiOptions? installerOptions = null, CandidateOwner? original = null,
        bool managed = false, string? previousVersion = null, string? targetVersion = null, string? localStateRoot = null, Trust.UpdateSelection? previousInstallation = null)
    {
        if (IsPending) throw new InvalidOperationException("Recover the interrupted deployment first.");
        long revision = File.Exists(Path) ? checked(Read().Revision + 1) : 1;
        string? snapshot = null, local = null;
        try
        {
            snapshot = new ConfigurationSnapshot(workspace).Capture(dataRoot);
            local = localStateRoot != null && !localStateRoot.Equals(dataRoot, StringComparison.OrdinalIgnoreCase) ? new ConfigurationSnapshot(workspace).Capture(localStateRoot, placementOnly: true) : null;
            File.Delete(workspace.Resolve(".updater-recovery-settled"));
            Write(new RecoveryRecord(1, diagnosticId, dataRoot, snapshot, previousApp, visible, null, InstallerOptions: installerOptions, Original: original, Revision: revision,
                Managed: managed, PreviousVersion: previousVersion, TargetVersion: targetVersion, LocalStateRoot: localStateRoot, LocalSnapshot: local, PreviousInstallation: previousInstallation));
        }
        catch
        {
            if (snapshot != null) UpdateStorage.Remove(workspace, "snapshots/" + snapshot, "snapshot");
            if (local != null) UpdateStorage.Remove(workspace, "snapshots/" + local, "snapshot");
            throw;
        }
    }

    // Scratch data has no authority until the durable rename succeeds.
    private void Write(RecoveryRecord record) => OwnedWorkspace.WriteJson(Path, record, temporarySuffix: ".writing");
    private void Update(RecoveryRecord record) => Write(record with { Revision = checked(record.Revision + 1) });
    public void SetCandidate(CandidateOwner owner) => Update(ReadForWrite() with { Candidate = owner });
    public void MarkCommitted() => Update(ReadForWrite() with { DeploymentCommitted = true });

    private RecoveryRecord ReadForWrite()
    {
        var record = Read(out bool interrupted);
        if (interrupted) throw new InvalidDataException("Verify that the interrupted update process has exited before changing recovery state.");
        return record;
    }

    public async Task WaitForCandidateExitAsync(string? installedApplication = null)
    {
        var record = Read(out bool interrupted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (interrupted && installedApplication == null)
            throw new InvalidDataException("The interrupted recovery record has no verified candidate path.");
        if (record.Candidate is { } candidate)
            await candidate.WaitForExitAsync(timeout.Token, installedApplication != null ? path => MatchesPath(path, installedApplication, record.PreviousApp) : null);
        if (installedApplication == null) return;

        // An interrupted launch may have started a candidate before recording its identity.
        await WaitForApplicationExitAsync(installedApplication, record.PreviousApp, timeout.Token);
        if (interrupted) File.Delete(PendingPath);
    }

    private static bool MatchesPath(string path, string installedApplication, string previousApplication) =>
        path.Equals(installedApplication, StringComparison.OrdinalIgnoreCase) || path.Equals(previousApplication, StringComparison.OrdinalIgnoreCase);

    private static async Task WaitForApplicationExitAsync(string installedApplication, string previousApplication, CancellationToken token)
    {
        string installedName = System.IO.Path.GetFileNameWithoutExtension(installedApplication);
        string previousName = System.IO.Path.GetFileNameWithoutExtension(previousApplication);
        string[] names = installedName.Equals(previousName, StringComparison.OrdinalIgnoreCase) ? [installedName] : [installedName, previousName];
        foreach (string name in names)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        string path = process.MainModule?.FileName ?? throw new InvalidOperationException("The running process image could not be verified.");
                        if (MatchesPath(path, installedApplication, previousApplication))
                            await process.WaitForExitAsync(token);
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                    {
                        if (!process.HasExited) throw;
                    }
                }
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }
    }
    public void RestoreConfiguration(string expectedRoot)
    {
        var record = ReadForWrite();
        if (record.ConfigurationRestored) return;
        if (record.Completed || record.DeploymentCommitted) throw new InvalidOperationException("A settled deployment cannot restore its previous configuration.");
        new ConfigurationSnapshot(workspace).Restore(record.Snapshot, expectedRoot);
        if (record.LocalSnapshot != null && record.LocalStateRoot != null) new ConfigurationSnapshot(workspace).Restore(record.LocalSnapshot, record.LocalStateRoot);
        Update(record with { ConfigurationRestored = true });
    }
    public void Complete()
    {
        var record = ReadForWrite();
        if (!record.Completed) Update(record with { Completed = true });
        using (var journal = File.OpenRead(Path))
            OwnedWorkspace.WriteText(workspace.Resolve(".updater-recovery-settled"), "AudioPilot settled recovery 1\n" + Convert.ToHexString(SHA256.HashData(journal)) + "\n");
        TrimUnreferencedSnapshots();
        if (File.Exists(workspace.Resolve("deployment.json"))) new PortableDeployment(workspace).Trim();
    }

    internal void TrimUnreferencedSnapshots()
    {
        try
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(Path) || File.Exists(PendingPath))
            {
                var record = Read();
                if (!record.Completed || !IsDurablySettled())
                {
                    retained.Add(record.Snapshot);
                    if (record.LocalSnapshot != null) retained.Add(record.LocalSnapshot);
                }
            }
            UpdateStorage.Trim(workspace, "snapshots", "snapshot", retained);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { UpdateStorage.ReportDeferred(error); }
    }

    private bool IsDurablySettled()
    {
        string receipt = workspace.Resolve(".updater-recovery-settled");
        if (File.Exists(PendingPath) || !File.Exists(receipt) || new FileInfo(receipt).Length > 128) return false;
        using var journal = File.OpenRead(Path);
        return File.ReadAllText(receipt) == "AudioPilot settled recovery 1\n" + Convert.ToHexString(SHA256.HashData(journal)) + "\n";
    }
}
