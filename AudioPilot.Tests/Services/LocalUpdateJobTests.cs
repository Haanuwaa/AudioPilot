using System.Security.Cryptography;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Tests.Services;

public sealed partial class LocalUpdateJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-job-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private OwnedWorkspace Workspace => new(Path.Combine(_root, "work"));
    private string Source => Path.Combine(_root, "candidate.zip");

    private async Task<Trust> CreatePackageAsync()
    {
        Directory.CreateDirectory(_root);
        byte[] data = RandomNumberGenerator.GetBytes(200000);
        await File.WriteAllBytesAsync(Source, data, Token);
        return new(data);
    }

    [Fact]
    public async Task PauseAfterAWrite_ResumesAcrossControllerRestartAndVerifiesBytes()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime();
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime, async (_, token) => { copied.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }))
        {
            var work = job.StageAsync(Source, Token);
            await copied.Task.WaitAsync(Token);
            Assert.True(job.Pause());
            var paused = await work;
            Assert.Equal(UpdateJobPhase.Paused, paused.Phase);
            Assert.Equal(65536, paused.Bytes);
        }
        await using var resumed = new LocalUpdateJob(Workspace, trust, runtime);
        var ready = await resumed.StageAsync(Source, Token);
        Assert.Equal(UpdateJobPhase.Ready, ready.Phase);
        Assert.Equal(200000, ready.Bytes);
        await using var staged = File.OpenRead(Workspace.Resolve("job-stage/candidate.zip"));
        Assert.Equal(trust.Authorization.Artifact.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(staged, Token)));
    }

    [Fact]
    public async Task CancellationDuringStaging_DeletesOnlyManagedCopiesAndDoesNotPrepare()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime();
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var job = new LocalUpdateJob(Workspace, trust, runtime, async (_, token) => { copied.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        var work = job.StageAsync(Source, Token);
        await copied.Task.WaitAsync(Token);
        Assert.True(job.Cancel());
        Assert.Equal(UpdateJobPhase.Cancelled, (await work).Phase);
        Assert.False(File.Exists(Workspace.Resolve("job-package.partial")));
        Assert.True(File.Exists(Source));
        Assert.Equal(0, runtime.Prepares);
    }

    [Fact]
    public async Task ChangedSourceAfterPause_CannotReachReadyOrDeploy()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime();
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime, async (_, token) => { copied.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }))
        {
            var work = job.StageAsync(Source, Token); await copied.Task.WaitAsync(Token); Assert.True(job.Pause()); await work;
        }
        var changed = await File.ReadAllBytesAsync(Source, Token); changed[^1] ^= 1; await File.WriteAllBytesAsync(Source, changed, Token);
        await using var resumed = new LocalUpdateJob(Workspace, trust, runtime);
        Assert.Equal(UpdateJobPhase.Failed, (await resumed.StageAsync(Source, Token)).Phase);
        Assert.Equal(0, runtime.Deploys);
    }

    [Fact]
    public async Task MicrophoneDeferral_ResumesOriginalAndKeepsPackageReadyForRetry()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime { Preparation = new(false, "microphone-owned") };
        await using var job = new LocalUpdateJob(Workspace, trust, runtime);
        await job.StageAsync(Source, Token);
        var result = await job.ApplyAsync(Token);
        Assert.Equal(UpdateJobPhase.Deferred, result.Phase);
        Assert.Equal("microphone-owned", result.Reason);
        Assert.Equal(1, runtime.Cancels);
        Assert.Equal(0, runtime.Deploys);
        runtime.Preparation = new(true);
        Assert.Equal(UpdateJobPhase.Completed, (await job.ApplyAsync(Token)).Phase);
    }

    [Fact]
    public async Task PreparingCancellation_ReleasesAdmissionAndRejectsConcurrentApply()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime { BlockPrepare = true };
        await using var job = new LocalUpdateJob(Workspace, trust, runtime);
        await job.StageAsync(Source, Token);
        var work = job.ApplyAsync(Token);
        await runtime.PrepareEntered.Task.WaitAsync(Token);
        Assert.Throws<InvalidOperationException>(() => { _ = job.ApplyAsync(Token); });
        Assert.True(job.Cancel());
        Assert.Equal(UpdateJobPhase.Cancelled, (await work).Phase);
        Assert.Equal(1, runtime.Cancels);
        Assert.Equal(0, runtime.Deploys);
    }

    [Fact]
    public async Task InstallingCannotBeCancelled_AndDisposeJoinsCommit()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime { BlockDeploy = true };
        var job = new LocalUpdateJob(Workspace, trust, runtime);
        await job.StageAsync(Source, Token);
        var work = job.ApplyAsync(Token);
        await runtime.DeployEntered.Task.WaitAsync(Token);
        Assert.False(job.Cancel());
        var disposal = job.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        runtime.ReleaseDeploy.TrySetResult();
        Assert.Equal(UpdateJobPhase.Completed, (await work).Phase);
        await disposal;
        Assert.Equal(0, runtime.Cancels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedDeploymentRequiresExplicitRecoveryAndUsesActualCommitOutcome(bool committed)
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime { ThrowDeploy = true, RecoveredCommitted = committed };
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime))
        {
            await job.StageAsync(Source, Token);
            Assert.Equal(UpdateJobPhase.RecoveryRequired, (await job.ApplyAsync(Token)).Phase);
        }
        await using var recovered = new LocalUpdateJob(Workspace, trust, runtime);
        Assert.Equal(UpdateJobPhase.RecoveryRequired, recovered.Snapshot.Phase);
        Assert.Throws<InvalidOperationException>(() => { _ = recovered.StageAsync(Source, Token); });
        Assert.False(recovered.Cancel());
        Assert.Equal(committed ? UpdateJobPhase.Completed : UpdateJobPhase.RolledBack, (await recovered.RecoverAsync(Token)).Phase);
        Assert.Equal(1, runtime.Recovers);
    }

    [Fact]
    public async Task FailedStatePersistenceCannotStartDeployment()
    {
        var trust = await CreatePackageAsync();
        var runtime = new Runtime { BlockPrepare = true };
        await using var job = new LocalUpdateJob(Workspace, trust, runtime);
        await job.StageAsync(Source, Token);
        var work = job.ApplyAsync(Token);
        await runtime.PrepareEntered.Task.WaitAsync(Token);
        using var locked = new FileStream(Workspace.Resolve("job.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        runtime.ReleasePrepare.TrySetResult();
        Assert.Equal(UpdateJobPhase.RecoveryRequired, (await work).Phase);
        Assert.Equal(0, runtime.Deploys);
    }

    [Fact]
    public async Task InitializedJobCannotSilentlyResetAfterStateLoss()
    {
        var trust = await CreatePackageAsync(); var runtime = new Runtime();
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime)) { await job.StageAsync(Source, Token); }
        File.Delete(Workspace.Resolve("job.json"));
        Assert.Throws<InvalidDataException>(() => new LocalUpdateJob(Workspace, trust, runtime));
    }

    [Fact]
    public async Task TornPendingJobRecordRequiresInspectionInsteadOfAssumingNoDeploymentOccurred()
    {
        var trust = await CreatePackageAsync(); var runtime = new Runtime();
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime)) { await job.StageAsync(Source, Token); }
        File.WriteAllText(Workspace.Resolve("job.json.new"), "{incomplete");
        await using var recovered = new LocalUpdateJob(Workspace, trust, runtime);
        Assert.Equal(UpdateJobPhase.RecoveryRequired, recovered.Snapshot.Phase);
        Assert.True(File.Exists(Workspace.Resolve("job.interrupted")));
        Assert.Equal(0, runtime.Deploys);
        Assert.Equal(UpdateJobPhase.RolledBack, (await recovered.RecoverAsync(Token)).Phase);
        Assert.Equal(1, runtime.Recovers);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Trust(byte[] data) : ILocalUpdateTrust
    {
        public AuthorizedUpdate Authorization { get; } = CreateAuthorization(data);
        public Task<AuthorizedUpdate> AuthorizeAsync(string target, UpdateSelection selection, CancellationToken token) => Task.FromResult(Authorization);
        public async Task<VerifiedLocalUpdate> OpenPackageAsync(string path, UpdateSelection selection, CancellationToken token)
        {
            await ResumableDownload.VerifyAsync(path, Authorization.Artifact.Length, Authorization.Artifact.Sha256, token);
            return new(() => Authorization, Authorization, path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        private static AuthorizedUpdate CreateAuthorization(byte[] bytes)
        {
            var artifact = new UpdateArtifact("candidate.zip", "portable", "win-x64", "self-contained", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            return new(new(1, "AudioPilot", "local-test", 1, 1, "1.0.1", "1.0.0", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, [artifact]), artifact);
        }
    }
    private sealed class Runtime : ILocalUpdateRuntime
    {
        public string Context => "isolated-test-installation";
        public bool RecoveryPending { get; private set; }
        public int Prepares, Cancels, Deploys, Recovers;
        public bool BlockPrepare, BlockDeploy, ThrowDeploy, RecoveredCommitted;
        public UpdateJobPreparation Preparation = new(true);
        public TaskCompletionSource PrepareEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePrepare { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeployEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDeploy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UpdateSelection Inspect() => new("portable", "win-x64", "self-contained", "1.0.0", "1.0.0");
        public async Task<UpdateJobPreparation> PrepareAsync(CancellationToken token)
        { Prepares++; PrepareEntered.TrySetResult(); if (BlockPrepare) await ReleasePrepare.Task.WaitAsync(token); return Preparation; }
        public Task CancelPreparationAsync(CancellationToken token) { Cancels++; return Task.CompletedTask; }
        public async Task<bool> DeployAsync(VerifiedLocalUpdate package)
        {
            Deploys++; RecoveryPending = true; DeployEntered.TrySetResult();
            if (BlockDeploy) await ReleaseDeploy.Task;
            if (ThrowDeploy) throw new IOException("injected interrupted mutation");
            RecoveryPending = false; return true;
        }
        public Task<bool> RecoverAsync() { Recovers++; RecoveryPending = false; return Task.FromResult(RecoveredCommitted); }
    }
}
