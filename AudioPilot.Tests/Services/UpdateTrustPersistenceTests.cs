using AudioPilot.Updater;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

public sealed class UpdateTrustPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-trust-state-" + Guid.NewGuid().ToString("N"));
    private const string Pin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private OwnedWorkspace Workspace => new(_root);

    [Fact]
    public void StableReleaseFloorSurvivesUnavailableVariantsAndCannotMoveBackward()
    {
        var store = new TrustApplicationPolicyStore(Workspace, Pin);
        _ = store.Read();
        store.Observe("1.0.9");
        store.Advance(new(1, Pin, "1.0.8", new string('B', 64)));
        var restored = new TrustApplicationPolicyStore(Workspace, Pin);
        Assert.Equal("1.0.9", restored.Read().LatestVersion);
        Assert.Throws<InvalidDataException>(() => restored.Observe("1.0.8"));
        restored.Observe("1.0.10");
        Assert.Equal("1.0.10", restored.Read().LatestVersion);
    }

    [Fact]
    public void FlushedPendingFloorIsPromotedBeforeAnotherAuthorization()
    {
        var store = new TrustApplicationPolicyStore(Workspace, Pin);
        store.Advance(new(1, Pin, "1.0.1", new string('1', 64)));
        using (var locked = new FileStream(Workspace.Resolve("tuf-application-policy.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<System.ComponentModel.Win32Exception>(() => store.Advance(new(1, Pin, "1.0.2", new string('2', 64))));
        Assert.Equal("1.0.2", new TrustApplicationPolicyStore(Workspace, Pin).Read().Version);
        Assert.Throws<InvalidDataException>(() => store.Advance(new(1, Pin, "1.0.1", new string('1', 64))));
        Assert.False(File.Exists(Workspace.Resolve("tuf-application-policy.json.new")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptInitializedFloorFailsClosed(bool corrupt)
    {
        var store = new TrustApplicationPolicyStore(Workspace, Pin);
        store.Advance(new(1, Pin, "1.0.1", new string('1', 64)));
        string path = Workspace.Resolve("tuf-application-policy.json");
        if (corrupt) File.WriteAllText(path, "{broken"); else File.Delete(path);
        if (corrupt) Assert.Throws<System.Text.Json.JsonException>(() => store.Read());
        else Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void CorruptPendingFloorCannotFallBackToOlderCommittedVersion()
    {
        var store = new TrustApplicationPolicyStore(Workspace, Pin);
        store.Advance(new(1, Pin, "1.0.1", new string('1', 64)));
        File.WriteAllText(Workspace.Resolve("tuf-application-policy.json.new"), "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => store.Read());
    }

    [Fact]
    public void SyntacticallyValidFloorCorruptionIsDetected()
    {
        var store = new TrustApplicationPolicyStore(Workspace, Pin);
        store.Advance(new(1, Pin, "1.0.2", new string('2', 64)));
        string path = Workspace.Resolve("tuf-application-policy.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("1.0.2", "1.0.1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void RecoveryRetriesDoNotOverwriteEditsMadeAfterConfigurationWasRestored()
    {
        var workspace = Workspace;
        string data = workspace.Resolve("data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "original");
        var recovery = new DeploymentRecovery(workspace);
        recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("application/AudioPilot.exe"), false);
        File.WriteAllText(Path.Combine(data, "settings.json"), "candidate migration");
        recovery.RestoreConfiguration(data);
        Assert.Equal("original", File.ReadAllText(Path.Combine(data, "settings.json")));
        File.WriteAllText(Path.Combine(data, "settings.json"), "new user edit after recovery");
        new DeploymentRecovery(workspace).RestoreConfiguration(data);
        Assert.Equal("new user edit after recovery", File.ReadAllText(Path.Combine(data, "settings.json")));
        recovery.Complete();
        Assert.False(new DeploymentRecovery(workspace).IsPending);
        Assert.True(new DeploymentRecovery(workspace).Read().Completed);
    }

    [Fact]
    public void SettledRecoveryTrimsOnlySupersededOwnedSnapshots()
    {
        var workspace = Workspace;
        string data = workspace.Resolve("data"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "saved");
        var recovery = new DeploymentRecovery(workspace);
        recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("app/AudioPilot.exe"), false);
        string previous = recovery.Read().Snapshot;
        recovery.Complete();
        recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("app/AudioPilot.exe"), false);
        string current = recovery.Read().Snapshot;
        string foreign = workspace.Resolve("snapshots/user-folder"); Directory.CreateDirectory(foreign);
        Assert.True(Directory.Exists(workspace.Resolve("snapshots/" + previous)));
        Assert.True(Directory.Exists(workspace.Resolve("snapshots/" + current)));
        recovery.Complete();
        Assert.False(Directory.Exists(workspace.Resolve("snapshots/" + previous)));
        Assert.True(Directory.Exists(workspace.Resolve("snapshots/" + current)));
        Assert.True(Directory.Exists(foreign));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
