using System.IO.Compression;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal sealed record PortableState(int Schema, string? Current, string? Previous, bool PendingHealth);
internal sealed record PortableRollbackState(int Schema, string Candidate, string? Previous);
internal sealed record DeploymentResult(bool Committed, bool Recovered, string? Current);

/// <summary>Versioned portable payloads with data outside the replaceable tree and a durable recovery pointer.</summary>
internal sealed class PortableDeployment(OwnedWorkspace workspace)
{
    public Task<DeploymentResult> ApplyAsync(VerifiedLocalUpdate package,
        Func<string, CancellationToken, Task<bool>> healthCheck, CancellationToken token,
        Action? afterPointerSwitch = null, Func<Task>? stopCandidateBeforeRollback = null)
    {
        package.Revalidate();
        if (package.Authorization.Artifact.Kind != "portable") throw new InvalidDataException("A portable deployment requires a signed portable artifact.");
        return ApplyAsync(package.Path, package.Authorization.Artifact.Length, package.Authorization.Artifact.Sha256,
            package.Authorization.Release.Version, healthCheck, token, afterPointerSwitch, stopCandidateBeforeRollback);
    }

    private string StatePath => workspace.Resolve("deployment.json");

    public PortableState ReadState()
    {
        if (!File.Exists(StatePath)) return new(1, null, null, false);
        var state = OwnedWorkspace.ReadJson<PortableState>(StatePath);
        if (state.Schema != 1) throw new InvalidDataException("Unsupported deployment state.");
        ValidateGeneration(state.Current);
        ValidateGeneration(state.Previous);
        return state;
    }

    public async Task<DeploymentResult> ApplyAsync(string archive, long length, string sha256, string version,
        Func<string, CancellationToken, Task<bool>> healthCheck, CancellationToken token,
        Action? afterPointerSwitch = null, Func<Task>? stopCandidateBeforeRollback = null)
    {
        if (!Version.TryParse(version, out _)) throw new InvalidDataException("Invalid version.");
        using var ownership = workspace.Lock();
        PortableState prior = RecoverCore();
        if (prior.Current != null && Version.Parse(prior.Current.Split('_')[0]) >= Version.Parse(version))
            throw new InvalidOperationException("Portable updates must advance the version.");
        string generation = version + "_" + Guid.NewGuid().ToString("N");
        string target = UpdateStorage.CreateDirectory(workspace, Path.Combine("versions", generation), "generation");
        try
        {
            OwnedWorkspace.CheckAncestors(archive);
            // Keep the verified archive open without write sharing until extraction finishes.
            await using (var package = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (package.Length != length || !Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(package, token).ConfigureAwait(false)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Portable package verification failed.");
                package.Position = 0;
                using var zip = new ZipArchive(package, ZipArchiveMode.Read);
                long total = 0;
                byte[] buffer = new byte[65536];
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (zip.Entries.Count is 0 or > 4096) throw new InvalidDataException("Invalid archive entry count.");
                string prefix = zip.GetEntry("AudioPilot/AudioPilot.exe") == null ? "" : "AudioPilot/";
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Mixed portable archive layouts.");
                    string name = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                    if (name.EndsWith(Path.DirectorySeparatorChar) || name.Contains(':') || Path.IsPathRooted(name) ||
                        name.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')) ||
                        name.Split(Path.DirectorySeparatorChar).Any(IsDeviceName) ||
                        ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !names.Add(name))
                        throw new InvalidDataException("Unsafe or duplicate archive entry.");
                    total = checked(total + entry.Length);
                    if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Expanded package is too large.");
                    string destination = workspace.Resolve(Path.Combine("versions", generation, name));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    long written = 0;
                    while (true)
                    {
                        int count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (count == 0) break;
                        written += count;
                        if (written > entry.Length) throw new InvalidDataException("Expanded entry exceeds its declared size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                    if (written != entry.Length) throw new InvalidDataException("Expanded entry is truncated.");
                    output.Flush(true);
                }
            }
            token.ThrowIfCancellationRequested();
            OwnedWorkspace.WriteJson(workspace.Resolve("rollback-generation.json"), new PortableRollbackState(1, generation, prior.Previous));
            OwnedWorkspace.WriteJson(StatePath, new PortableState(1, generation, prior.Current, true));
            afterPointerSwitch?.Invoke();
            bool healthy;
            try
            {
                healthy = await healthCheck(target, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                if (stopCandidateBeforeRollback != null) await stopCandidateBeforeRollback();
                RecoverCore();
                throw;
            }
            if (!healthy)
            {
                if (stopCandidateBeforeRollback != null) await stopCandidateBeforeRollback();
                var recovered = RecoverCore();
                return new(false, true, recovered.Current);
            }
            OwnedWorkspace.WriteJson(StatePath, new PortableState(1, generation, prior.Current, false));
            return new(true, false, generation);
        }
        finally { TrimCore(); }
    }

    public PortableState Recover()
    {
        using var ownership = workspace.Lock();
        var restored = RecoverCore();
        TrimCore();
        return restored;
    }

    internal void Trim()
    {
        try { using var ownership = workspace.Lock(); TrimCore(); }
        catch (IOException error) { UpdateStorage.ReportDeferred(error); }
    }

    private void TrimCore()
    {
        try
        {
            var state = ReadState();
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (state.Current != null) retained.Add(state.Current);
            if (state.Previous != null) retained.Add(state.Previous);
            if (state.PendingHealth && ReadRollbackPrevious(state) is { } rollback) retained.Add(rollback);
            var recovery = new DeploymentRecovery(workspace);
            if (recovery.IsPending)
            {
                var record = recovery.Read();
                RetainApp(record.PreviousApp);
                if (record.Candidate != null) RetainApp(record.Candidate.Path);
            }
            UpdateStorage.Trim(workspace, "versions", "generation", retained);

            void RetainApp(string app)
            {
                string? directory = Path.GetDirectoryName(app);
                if (directory != null && Path.GetDirectoryName(directory)?.Equals(workspace.Resolve("versions"), StringComparison.OrdinalIgnoreCase) == true)
                    retained.Add(Path.GetFileName(directory));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { UpdateStorage.ReportDeferred(error); }
    }

    private PortableState RecoverCore()
    {
        PortableState state = ReadState();
        if (!state.PendingHealth) return state;
        var restored = new PortableState(1, state.Previous, ReadRollbackPrevious(state), false);
        OwnedWorkspace.WriteJson(StatePath, restored);
        return restored;
    }

    private string? ReadRollbackPrevious(PortableState state)
    {
        var rollback = OwnedWorkspace.ReadJson<PortableRollbackState>(workspace.Resolve("rollback-generation.json"));
        if (rollback.Schema != 1 || rollback.Candidate != state.Current) throw new InvalidDataException("The portable rollback record does not match its candidate.");
        ValidateGeneration(rollback.Previous);
        return rollback.Previous;
    }

    private static void ValidateGeneration(string? generation)
    {
        if (generation != null && (generation.Length > 100 || generation.Any(ch => !char.IsAsciiDigit(ch) && !char.IsAsciiHexDigit(ch) && ch is not '.' and not '_')))
            throw new InvalidDataException("Invalid generation pointer.");
    }

    private static bool IsDeviceName(string part)
    {
        string name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(name[3]));
    }
}
