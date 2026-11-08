using System.IO.Compression;
using System.Security.Cryptography;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed partial class UpdaterDeploymentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractionJoinsBothWorkersBeforeCancellingOrReportingFailure(bool failWorker)
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = MakeParallelArchive(workspace);
        long length = new FileInfo(zip).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        var first = await new PortableDeployment(workspace).ApplyAsync(zip, length, hash, "1.0.0", (_, _) => Task.FromResult(true), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("injected extraction failure");
        int active = 0, released = 0;
        var deployment = new PortableDeployment(workspace, async (name, _, token) =>
        {
            if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
            try
            {
                if (failWorker && name == "file0.bin") { await fail.Task.WaitAsync(token); throw expected; }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { Interlocked.Increment(ref released); }
        });
        bool checkedHealth = false;
        var work = deployment.ApplyAsync(zip, length, hash, "1.0.1", (_, _) => { checkedHealth = true; return Task.FromResult(true); }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(work.IsCompleted);
            if (failWorker)
            {
                fail.TrySetResult();
                Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => work.WaitAsync(TimeSpan.FromSeconds(10), Token)));
            }
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(10), Token));
            }
            Assert.Equal(2, released);
            Assert.False(checkedHealth);
            Assert.Equal(first.Current, deployment.ReadState().Current);
            Assert.False(deployment.ReadState().PendingHealth);
            Assert.Single(Directory.GetDirectories(workspace.Resolve("versions")));
        }
        finally
        {
            cancellation.Cancel(); fail.TrySetResult();
            try { await work; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ParallelExtractionVerifiesEveryFileBeforeHealthAndRejectsCorruptedArchiveBytes()
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = MakeParallelArchive(workspace);
        long length = new FileInfo(zip).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        int checks = 0;
        var deployment = new PortableDeployment(workspace);
        var result = await deployment.ApplyAsync(zip, length, hash, "1.0.0", (directory, _) =>
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                using var original = entry.Open();
                using var extracted = File.OpenRead(Path.Combine(directory, entry.FullName));
                Assert.Equal(SHA256.HashData(original), SHA256.HashData(extracted));
            }
            checks++;
            return Task.FromResult(true);
        }, Token);
        Assert.True(result.Committed);
        using (var damaged = File.Open(zip, FileMode.Open, FileAccess.Write)) damaged.WriteByte(0);
        await Assert.ThrowsAsync<InvalidDataException>(() => deployment.ApplyAsync(zip, length, hash, "1.0.1", (_, _) => { checks++; return Task.FromResult(true); }, Token));
        Assert.Equal(1, checks);
        Assert.Equal(result.Current, deployment.ReadState().Current);
        Assert.Single(Directory.GetDirectories(workspace.Resolve("versions")));
    }

    private static string MakeParallelArchive(OwnedWorkspace workspace)
    {
        string path = workspace.Resolve("parallel.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (int index = 0; index < 8; index++)
        {
            using var entry = zip.CreateEntry($"file{index}.bin").Open();
            entry.Write(RandomNumberGenerator.GetBytes(128 * 1024));
        }
        return path;
    }
}
