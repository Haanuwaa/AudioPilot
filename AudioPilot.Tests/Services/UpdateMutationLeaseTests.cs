using System.Text.Json;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Tests.Services;

public sealed class UpdateMutationLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot.MutationLease." + Guid.NewGuid().ToString("N"));
    private readonly string _identity = Guid.NewGuid().ToString("N");
    private readonly string _generation = "1.0.0_" + Guid.NewGuid().ToString("N");
    private string Workspace => Path.Combine(_root, "app");
    private string Application => Path.Combine(Workspace, "versions", _generation);
    public UpdateMutationLeaseTests()
    {
        Directory.CreateDirectory(Application);
        File.WriteAllText(Path.Combine(Workspace, ".updater"), "AudioPilot updater workspace 1");
        Write(PortableLayout.FileName, new PortableLayoutDescriptor(1, _identity, true));
        Write("deployment.json", new { Schema = 1, Current = _generation, Previous = (string?)null, PendingHealth = false });
    }

    [Theory]
    [InlineData("locked", "update-in-progress")]
    [InlineData("journal", "update-recovery-required")]
    [InlineData("torn", "update-recovery-required")]
    [InlineData("pending-health", "update-recovery-required")]
    [InlineData("stale", "update-generation-changed")]
    [InlineData("duplicate", "update-state-unavailable")]
    [InlineData("layout-missing", "update-state-unavailable")]
    [InlineData("layout-unmanaged", "update-state-unavailable")]
    public void MutationsRejectUnsettledDeploymentAndReleaseFailedAdmission(string phase, string code)
    {
        using FileStream? held = phase == "locked" ? Exclusive() : null;
        if (phase == "layout-missing") File.Delete(Path.Combine(Workspace, PortableLayout.FileName));
        if (phase == "layout-unmanaged") Write(PortableLayout.FileName, new PortableLayoutDescriptor(1, _identity, false));
        if (phase == "journal") Write("recovery.json", new { Schema = 1, DiagnosticId = _identity, Completed = false });
        if (phase == "torn") Write("recovery.json.new", new { Schema = 1, DiagnosticId = _identity, Completed = true });
        if (phase == "pending-health") Write("deployment.json", new { Schema = 1, Current = _generation, Previous = _generation, PendingHealth = true });
        if (phase == "stale") Write("deployment.json", new { Schema = 1, Current = "1.0.1_" + Guid.NewGuid().ToString("N"), Previous = _generation, PendingHealth = false });
        if (phase == "duplicate") File.WriteAllText(Path.Combine(Workspace, "recovery.json"), "{\"Schema\":1,\"DiagnosticId\":\"" + _identity + "\",\"Completed\":true,\"Completed\":false}");
        Assert.Equal(code, Assert.Throws<UpdateMutationBlockedException>(() => UpdateMutationLease.Acquire(Application)).ErrorCode);
        if (held == null) using (Exclusive()) { }
    }

    [Fact]
    public void HealthyCommandHoldsTheDeploymentBarrierUntilDisposed()
    {
        Write("recovery.json", new { Schema = 1, DiagnosticId = _identity, Completed = true });
        using (var lease = UpdateMutationLease.Acquire(Application))
        {
            Assert.NotNull(lease);
            Assert.Throws<IOException>(() => Exclusive());
        }
        using (Exclusive()) { }
    }

    [Fact]
    public void InstalledAdmissionRequiresTheExactOwnerAndChecksItsRecoveryJournal()
    {
        string directory = Path.Combine(_root, "installed");
        Directory.CreateDirectory(directory);
        string owner = Path.Combine(Workspace, ".updater-installation");
        File.WriteAllText(owner, "AudioPilot installed updater workspace 1\n" + Path.Combine(_root, "other", "AudioPilot.exe") + "\n");
        using (Exclusive()) Assert.Null(UpdateMutationLease.AcquireWorkspace(directory, Workspace, portable: false));
        File.WriteAllText(owner, "AudioPilot installed updater workspace 1\n" + Path.Combine(directory, "AudioPilot.exe").ToUpperInvariant() + "\n");
        Write("installation.json", new { Schema = 1, Kind = "msi", Root = directory, Identity = _identity });
        Write("recovery.json", new { Schema = 1, DiagnosticId = _identity, Completed = false });
        Assert.Equal("update-recovery-required", Assert.Throws<UpdateMutationBlockedException>(() => UpdateMutationLease.AcquireWorkspace(directory, Workspace, portable: false)).ErrorCode);
        Write("recovery.json", new { Schema = 1, DiagnosticId = _identity, Completed = true });
        using var admitted = UpdateMutationLease.AcquireWorkspace(directory, Workspace, portable: false);
        Assert.NotNull(admitted);
    }

    [Theory]
    [InlineData(32, 73)]
    [InlineData(33, 73)]
    [InlineData(5, 75)]
    [InlineData(2, 74)]
    public void LaunchFailuresUseStableCodesWithoutExceptionText(int nativeCode, int expected)
    {
        Exception error = nativeCode == 5 ? new UnauthorizedAccessException("private path") : new IOException("private path", unchecked((int)0x80070000) | nativeCode);
        Assert.Equal(expected, PortableLaunchFailure.ExitCode(error));
        Assert.Equal(74, PortableLaunchFailure.ExitCode(new InvalidDataException("private path")));
    }

    private FileStream Exclusive() => new(Path.Combine(Workspace, "lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private void Write<T>(string file, T value) => File.WriteAllText(Path.Combine(Workspace, file), JsonSerializer.Serialize(value));
    public void Dispose() => Directory.Delete(_root, true);
}
