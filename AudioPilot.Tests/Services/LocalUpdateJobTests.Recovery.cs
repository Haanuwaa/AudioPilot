using System.Net;
using System.Security.Cryptography;
using AudioPilot.Updater;
using AudioPilot.Updater.Validation;

namespace AudioPilot.Tests.Services;

public sealed partial class LocalUpdateJobTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryReplacesCorruptStagingButDoesNotDiscardValidBytesAfterTrustFailure(bool trustFailure)
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime();
        byte[] data = File.ReadAllBytes(Source);
        int requests = 0;
        using var client = new HttpClient(new HttpReplies(_ => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; }));
        var feed = new HttpsTestFeed("https://127.0.0.1:9443/", "unused", "unused", "candidate.zip");
        await using var job = new LocalUpdateJob(Workspace, trust, runtime);
        Assert.Equal(UpdateJobPhase.Ready, (await job.DownloadAsync(feed, client, Token)).Phase);
        string package = Workspace.Resolve("job-stage/candidate.zip");
        if (trustFailure) trust.OpenFailure = new CryptographicException("publisher rejected");
        else { using var stream = File.OpenWrite(package); stream.WriteByte((byte)(data[0] ^ 0xff)); }
        Assert.Equal(UpdateJobPhase.Failed, (await job.ApplyAsync(Token)).Phase);
        Assert.Equal(0, runtime.Prepares);
        Assert.Equal(trustFailure ? UpdateJobPhase.Failed : UpdateJobPhase.Ready, (await job.DownloadAsync(feed, client, Token)).Phase);
        Assert.Equal(trustFailure ? 1 : 2, requests);
        Assert.Equal(data, File.ReadAllBytes(package));
        trust.OpenFailure = null;
        if (trustFailure) Assert.Equal(UpdateJobPhase.Ready, (await job.DownloadAsync(feed, client, Token)).Phase);
        Assert.Equal(UpdateJobPhase.Completed, (await job.ApplyAsync(Token)).Phase);
        Assert.Equal(1, runtime.Prepares);
    }

    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    [InlineData("failed")]
    [InlineData("invalid")]
    [InlineData("short")]
    [InlineData("network")]
    [InlineData("root")]
    public async Task MsiCleanupPreservesRepairReferencesAndUnknownOwnership(string query)
    {
        var trust = await CreatePackageAsync();
        var workspace = Workspace;
        string needed = UpdateStorage.CreateDirectory(workspace, "job-stage/needed", "msi-source");
        string obsolete = UpdateStorage.CreateDirectory(workspace, "job-stage/obsolete", "msi-source");
        string unknown = workspace.Resolve("job-stage/unknown");
        Directory.CreateDirectory(unknown);
        foreach (string directory in new[] { needed, obsolete, unknown }) File.WriteAllText(Path.Combine(directory, "package.msi"), "retain or prune");
        var runtime = new Runtime
        {
            RepairSources = query switch { "known" => [needed], "invalid" => ["relative"], "short" => [Path.Combine(_root, "NEEDED~1")], "network" => [@"\\unavailable.invalid\share"], "root" => [Path.GetPathRoot(_root)!], _ => null },
            ThrowRepairQuery = query == "failed"
        };
        await using var job = new LocalUpdateJob(workspace, trust, runtime);
        Assert.True(File.Exists(Path.Combine(needed, "package.msi")));
        Assert.True(File.Exists(Path.Combine(unknown, "package.msi")));
        Assert.Equal(query != "known", Directory.Exists(obsolete));
    }

    [Fact]
    public async Task PendingRecoveryRetainsSourcesAndBusyDeploymentReturnsTheAppToNormal()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime { ThrowDeploy = true, RepairSources = [] };
        var workspace = Workspace;
        await using (var job = new LocalUpdateJob(workspace, trust, runtime))
        {
            await job.StageAsync(Source, Token);
            Assert.Equal(UpdateJobPhase.RecoveryRequired, (await job.ApplyAsync(Token)).Phase);
        }
        string pending = UpdateStorage.CreateDirectory(workspace, "job-stage/pending", "msi-source");
        await using (var recovered = new LocalUpdateJob(workspace, trust, runtime))
        {
            Assert.True(Directory.Exists(pending));
            Assert.Equal(UpdateJobPhase.RolledBack, (await recovered.RecoverAsync(Token)).Phase);
            Assert.False(Directory.Exists(pending));
        }
        runtime.ThrowDeploy = false;
        runtime.DeferDeployment = true;
        await using var next = new LocalUpdateJob(workspace, trust, runtime);
        await next.StageAsync(Source, Token);
        Assert.Equal(UpdateJobPhase.Deferred, (await next.ApplyAsync(Token)).Phase);
        Assert.Equal("installer-busy", next.Snapshot.Reason);
        Assert.Equal(1, runtime.Cancels);
        Assert.False(runtime.RecoveryPending);
        Assert.True(next.Cancel());
    }
}
