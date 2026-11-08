using System.IO.Compression;
using System.Security.Cryptography;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

public sealed class UpdateDeploymentRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-update-recovery-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("portable")]
    [InlineData("msi")]
    public void RecoveryOpeningUsesConfirmedFactsWithoutReadingTheIncompleteInstallation(string kind)
    {
        var workspace = new OwnedWorkspace(_root);
        string id = Guid.NewGuid().ToString("N"), previous = "1.0.0_" + Guid.NewGuid().ToString("N"), candidate = "1.0.1_" + Guid.NewGuid().ToString("N");
        string install = kind == "portable" ? workspace.Root : workspace.Resolve("application");
        string previousApp = kind == "portable" ? workspace.Resolve("versions/" + previous + "/AudioPilot.exe") : Path.Combine(install, "AudioPilot.exe");
        string data = workspace.Resolve("data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "original settings");
        OwnedWorkspace.WriteJson(workspace.Resolve("deployment.json"), new PortableState(1, candidate, previous, true));
        MsiOptions? options = kind == "msi" ? new(install, data, true, false, true) : null;
        var binding = new InstalledUpdateBinding(1, kind, install, id, options);
        var confirmed = new UpdateSelection(kind, "win-arm64", "self-contained", "1.0.0", "1.0.0");
        var recovery = new DeploymentRecovery(workspace);
        recovery.Begin(id, data, previousApp, false, options, managed: true, previousVersion: "1.0.0", targetVersion: "1.0.1", previousInstallation: confirmed);

        var selection = InstalledUpdateRuntime.ReadRecoveryInstallation(workspace, binding, "1.0.2");
        Assert.Equal(confirmed with { HelperVersion = "1.0.2" }, selection);
        Assert.True(recovery.IsPending);
        Assert.Equal(candidate, new PortableDeployment(workspace).ReadState().Current);
        Assert.False(File.Exists(previousApp));
        Assert.Throws<InvalidDataException>(() => InstalledUpdateRuntime.ReadRecoveryInstallation(workspace, binding with { Identity = Guid.NewGuid().ToString("N") }, "1.0.2"));
        Assert.Throws<InvalidDataException>(() => InstalledUpdateRuntime.ReadRecoveryInstallation(workspace, binding with { Root = workspace.Resolve("another-copy") }, "1.0.2"));
    }

    [Fact]
    public void SeparateLocalStateRollback_RestoresOnlyPlacement()
    {
        var workspace = new OwnedWorkspace(_root);
        string local = workspace.Resolve("local"); Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "window-placement.json"), "original placement");
        File.WriteAllText(Path.Combine(local, "settings.json"), "another authoritative configuration");
        var snapshots = new ConfigurationSnapshot(workspace);
        string snapshot = snapshots.Capture(local, placementOnly: true);
        File.WriteAllText(Path.Combine(local, "window-placement.json"), "candidate placement");
        File.WriteAllText(Path.Combine(local, "settings.json"), "newer unrelated settings");
        snapshots.Restore(snapshot, local);
        Assert.Equal("original placement", File.ReadAllText(Path.Combine(local, "window-placement.json")));
        Assert.Equal("newer unrelated settings", File.ReadAllText(Path.Combine(local, "settings.json")));
    }

    [Fact]
    public void ConfigurationRollback_RestoresSettingsPlacementAndBackupsWithoutTouchingLogsOrOtherData()
    {
        var workspace = new OwnedWorkspace(_root);
        string data = workspace.Resolve("data");
        Directory.CreateDirectory(Path.Combine(data, "backups"));
        File.WriteAllText(Path.Combine(data, "settings.json"), "original settings");
        File.WriteAllText(Path.Combine(data, "window-placement.json"), "original placement");
        File.WriteAllText(Path.Combine(data, "backups", "settings.json.bak"), "original backup");
        File.WriteAllText(Path.Combine(data, "settings.json.0123456789ABCDEF.location"), "original location");
        File.WriteAllText(Path.Combine(data, "settings.json.notes"), "original private notes");
        var snapshots = new ConfigurationSnapshot(workspace);
        string id = snapshots.Capture(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "candidate migration");
        File.Delete(Path.Combine(data, "window-placement.json"));
        File.Delete(Path.Combine(data, "backups", "settings.json.bak"));
        File.WriteAllText(Path.Combine(data, "backups", "settings.json.bak.1"), "candidate backup");
        File.WriteAllText(Path.Combine(data, "settings.json.notes"), "newer private notes");
        File.WriteAllText(Path.Combine(data, "settings.json.personal"), "new private file");
        File.WriteAllText(Path.Combine(data, "settings.json.0123456789ABCDEF.location"), "candidate location");
        File.WriteAllText(Path.Combine(data, "backups", "settings.json.bak.notes"), "unrelated backup notes");
        File.WriteAllText(Path.Combine(data, "AudioPilot.log"), "failure evidence");
        File.WriteAllText(Path.Combine(data, "other-user-file"), "leave alone");

        snapshots.Restore(id, data);
        snapshots.Restore(id, data);

        Assert.Equal("original settings", File.ReadAllText(Path.Combine(data, "settings.json")));
        Assert.Equal("original placement", File.ReadAllText(Path.Combine(data, "window-placement.json")));
        Assert.Equal("original backup", File.ReadAllText(Path.Combine(data, "backups", "settings.json.bak")));
        Assert.False(File.Exists(Path.Combine(data, "backups", "settings.json.bak.1")));
        Assert.Equal("original location", File.ReadAllText(Path.Combine(data, "settings.json.0123456789ABCDEF.location")));
        Assert.Equal("newer private notes", File.ReadAllText(Path.Combine(data, "settings.json.notes")));
        Assert.Equal("new private file", File.ReadAllText(Path.Combine(data, "settings.json.personal")));
        Assert.Equal("unrelated backup notes", File.ReadAllText(Path.Combine(data, "backups", "settings.json.bak.notes")));
        Assert.Equal("failure evidence", File.ReadAllText(Path.Combine(data, "AudioPilot.log")));
        Assert.Equal("leave alone", File.ReadAllText(Path.Combine(data, "other-user-file")));
        Assert.Throws<InvalidDataException>(() => snapshots.Restore(id, workspace.Resolve("another-data-root")));
    }

    [Fact]
    public void FailedCapturesAndUnjournaledPairsAreRemoved_WhileRecoveryReferencesRemain()
    {
        var workspace = new OwnedWorkspace(_root);
        string data = workspace.Resolve("data"), local = workspace.Resolve("local");
        Directory.CreateDirectory(data); Directory.CreateDirectory(local);
        string settings = Path.Combine(data, "settings.json");
        File.WriteAllText(settings, "original");
        var snapshots = new ConfigurationSnapshot(workspace);
        using (File.Open(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            for (int attempt = 0; attempt < 3; attempt++) Assert.Throws<IOException>(() => snapshots.Capture(data));
        Assert.Empty(Directory.EnumerateDirectories(workspace.Resolve("snapshots")));
        string placement = Path.Combine(local, "window-placement.json");
        File.WriteAllText(placement, "original placement");
        var recovery = new DeploymentRecovery(workspace);
        using (File.Open(placement, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("AudioPilot.exe"), false, localStateRoot: local));
        Assert.Empty(Directory.EnumerateDirectories(workspace.Resolve("snapshots")));

        recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("AudioPilot.exe"), false, localStateRoot: local);
        var pending = recovery.Read();
        string orphan = snapshots.Capture(data);
        string unrelated = workspace.Resolve("snapshots/unowned"); Directory.CreateDirectory(unrelated);
        recovery.TrimUnreferencedSnapshots();
        Assert.True(recovery.IsPending);
        Assert.True(Directory.Exists(workspace.Resolve("snapshots/" + pending.Snapshot)));
        Assert.True(Directory.Exists(workspace.Resolve("snapshots/" + pending.LocalSnapshot)));
        Assert.False(Directory.Exists(workspace.Resolve("snapshots/" + orphan)));
        Assert.True(Directory.Exists(unrelated));
        recovery.Complete();
        string receipt = workspace.Resolve(".updater-recovery-settled");
        Assert.Equal("AudioPilot settled recovery 1\n" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(workspace.Resolve("recovery.json")))) + "\n", File.ReadAllText(receipt));
        recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("AudioPilot.exe"), false);
        Assert.False(File.Exists(receipt));
    }

    [Fact]
    public void CorruptedBackup_IsRejectedBeforeAnyConfigurationIsReplaced()
    {
        var workspace = new OwnedWorkspace(_root);
        string data = workspace.Resolve("data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "original settings");
        var snapshots = new ConfigurationSnapshot(workspace);
        string id = snapshots.Capture(data);
        File.WriteAllText(workspace.Resolve("snapshots/" + id + "/settings.json"), "corrupt backup");
        File.WriteAllText(Path.Combine(data, "settings.json"), "candidate settings");
        Assert.Throws<InvalidDataException>(() => snapshots.Restore(id, data));
        Assert.Equal("candidate settings", File.ReadAllText(Path.Combine(data, "settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableRollback_WaitsForCandidateExitBeforeChangingThePointer(bool healthThrows)
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = workspace.Resolve("fixture.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("app.txt").Open())) writer.Write("payload");
        long length = new FileInfo(zip).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        var deployment = new PortableDeployment(workspace);
        var previous = await deployment.ApplyAsync(zip, length, hash, "1.0.0", (_, _) => Task.FromResult(true), Token);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = deployment.ApplyAsync(zip, length, hash, "1.0.1",
            (_, _) => healthThrows ? Task.FromException<bool>(new IOException("health read failed")) : Task.FromResult(false), Token,
            stopCandidateBeforeRollback: async () => { stopping.SetResult(); await exited.Task.WaitAsync(Token); });
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(deployment.ReadState().PendingHealth);
            Assert.NotEqual(previous.Current, deployment.ReadState().Current);
            Assert.False(pending.IsCompleted);
        }
        finally { exited.TrySetResult(); }
        if (healthThrows) await Assert.ThrowsAsync<IOException>(() => pending);
        else Assert.True((await pending).Recovered);
        Assert.Equal(previous.Current, deployment.ReadState().Current);
        Assert.False(deployment.ReadState().PendingHealth);
    }

    [Fact]
    public void ManagedPortableShellAndStartupPathsUseTheStableLauncher()
    {
        var workspace = new OwnedWorkspace(_root);
        OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, Guid.NewGuid().ToString("N"), Managed: true));
        string generation = workspace.Resolve("versions/1.0.4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generation);
        string application = Path.Combine(generation, "AudioPilot.exe");
        Assert.Throws<FileNotFoundException>(() => PortableLayout.GetLaunchExecutable(application));
        string launcher = workspace.Resolve("AudioPilot.exe");
        File.WriteAllText(launcher, "test launcher, never executed");
        Assert.Equal(launcher, PortableLayout.GetLaunchExecutable(application));
        Assert.Equal(launcher, PortableLayout.GetLaunchExecutable(launcher));
        string flat = Path.Combine(Path.GetTempPath(), "flat-portable", "AudioPilot.exe");
        Assert.Equal(flat, PortableLayout.GetLaunchExecutable(flat));
    }

    [Fact]
    public void VersionedPortableLayout_UsesStableDataAndRejectsAnotherDiagnosticIdentity()
    {
        var workspace = new OwnedWorkspace(_root);
        string id = Guid.NewGuid().ToString("N");
        OwnedWorkspace.WriteJson(workspace.Resolve(PortableLayout.FileName), new PortableLayoutDescriptor(1, id));
        string baseline = workspace.Resolve("versions/1.0.0_generation");
        string candidate = workspace.Resolve("versions/1.0.1_generation");
        Directory.CreateDirectory(baseline);
        Directory.CreateDirectory(candidate);
        Assert.Equal(workspace.Resolve("data"), PortableLayout.GetDataRoot(baseline, id));
        Assert.Equal(workspace.Resolve("data"), PortableLayout.GetDataRoot(candidate, id));
        Assert.Null(PortableLayout.GetDataRoot(candidate, null));
        Assert.Null(PortableLayout.GetDataRoot(workspace.Root, id));
        Assert.Throws<InvalidDataException>(() => PortableLayout.GetDataRoot(candidate, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task CancellationAfterHealth_DoesNotCommitAndStillStopsTheCandidate()
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = workspace.Resolve("fixture.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("app.txt").Open())) writer.Write("payload");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var deployment = new PortableDeployment(workspace);
        var initial = await deployment.ApplyAsync(zip, new FileInfo(zip).Length, hash, "1.0.0", (_, _) => Task.FromResult(true), Token);
        bool stopped = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deployment.ApplyAsync(zip, new FileInfo(zip).Length, hash, "1.0.1",
            (_, _) => { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token,
            stopCandidateBeforeRollback: () => { stopped = true; return Task.CompletedTask; }));
        Assert.True(stopped);
        Assert.Equal(initial.Current, deployment.ReadState().Current);
        Assert.False(deployment.ReadState().PendingHealth);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
