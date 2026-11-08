using System.IO.Compression;
using System.Security.Cryptography;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed class UpdateDeploymentRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-update-recovery-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
        var snapshots = new ConfigurationSnapshot(workspace);
        string id = snapshots.Capture(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "candidate migration");
        File.Delete(Path.Combine(data, "window-placement.json"));
        File.Delete(Path.Combine(data, "backups", "settings.json.bak"));
        File.WriteAllText(Path.Combine(data, "backups", "settings.json.candidate"), "candidate backup");
        File.WriteAllText(Path.Combine(data, "AudioPilot.log"), "failure evidence");
        File.WriteAllText(Path.Combine(data, "other-user-file"), "leave alone");

        snapshots.Restore(id, data);
        snapshots.Restore(id, data);

        Assert.Equal("original settings", File.ReadAllText(Path.Combine(data, "settings.json")));
        Assert.Equal("original placement", File.ReadAllText(Path.Combine(data, "window-placement.json")));
        Assert.Equal("original backup", File.ReadAllText(Path.Combine(data, "backups", "settings.json.bak")));
        Assert.False(File.Exists(Path.Combine(data, "backups", "settings.json.candidate")));
        Assert.Equal("failure evidence", File.ReadAllText(Path.Combine(data, "AudioPilot.log")));
        Assert.Equal("leave alone", File.ReadAllText(Path.Combine(data, "other-user-file")));
        Assert.Throws<InvalidDataException>(() => snapshots.Restore(id, workspace.Resolve("another-data-root")));
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
