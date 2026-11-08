using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AudioPilot.Tests.Helpers;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed partial class UpdaterDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-updater-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("timeout")]
    [InlineData("pause")]
    [InlineData("cancel")]
    public async Task BlockedHeadersRespectDeadlinePauseAndCancel_AndPreserveSafeResume(string stop)
    {
        byte[] data = [1, 2, 3, 4];
        var package = Identity(data);
        var workspace = new OwnedWorkspace(_root);
        File.WriteAllBytes(workspace.Resolve("package.partial"), data[..2]);
        OwnedWorkspace.WriteJson(workspace.Resolve("download.json"), new DownloadCheckpoint(package, "\"fixture-1\"", true));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var time = new ManualTimeProvider();
        using var pause = new CancellationTokenSource();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var handler = new BlockedHeaders(entered);
        using var client = new HttpClient(handler);
        var work = new ResumableDownload(client, workspace, timeProvider: time).RunAsync(package, pause.Token, null, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (stop == "timeout")
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAsync<TimeoutException>(() => work.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
        else
        {
            if (stop == "pause") pause.Cancel();
            else cancel.Cancel();
            Assert.Equal(stop == "pause" ? DownloadOutcome.Paused : DownloadOutcome.Cancelled,
                await work.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
        Assert.False(File.Exists(workspace.Resolve("package.verified")));
        if (stop == "cancel")
        {
            Assert.False(File.Exists(workspace.Resolve("package.partial")));
            Assert.False(File.Exists(workspace.Resolve("download.json")));
            return;
        }
        Assert.Equal(data[..2], File.ReadAllBytes(workspace.Resolve("package.partial")));
        var checkpoint = OwnedWorkspace.ReadJson<DownloadCheckpoint>(workspace.Resolve("download.json"));
        Assert.Equal(package, checkpoint.Package);
        Assert.Equal("\"fixture-1\"", checkpoint.ETag);
        using var resumedHandler = new Handler(request =>
        {
            Assert.Equal(2, Assert.Single(request.Headers.Range!.Ranges).From);
            Assert.Equal(checkpoint.ETag, request.Headers.IfRange!.EntityTag!.ToString());
            var response = Response(request, new MemoryStream(data[2..]));
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, data.Length - 1, data.Length);
            return response;
        });
        using var resumedClient = new HttpClient(resumedHandler);
        Assert.Equal(DownloadOutcome.Verified, await new ResumableDownload(resumedClient, workspace).RunAsync(package, CancellationToken.None, null, Token));
        Assert.Equal(data, File.ReadAllBytes(workspace.Resolve("package.verified")));
    }

    [Fact]
    public async Task StalledBodyTimesOutAndRetainsVerifiedResumeIdentity()
    {
        byte[] data = RandomNumberGenerator.GetBytes(150000);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var time = new ManualTimeProvider();
        using var handler = new Handler(request => Response(request, new BlockedStream(data, reached)));
        using var client = new HttpClient(handler);
        var workspace = new OwnedWorkspace(_root);
        var package = Identity(data);
        var work = new ResumableDownload(client, workspace, timeProvider: time).RunAsync(package, CancellationToken.None, null, Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        time.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => work.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(65536, new FileInfo(workspace.Resolve("package.partial")).Length);
        Assert.False(File.Exists(workspace.Resolve("package.verified")));
        using var resumedHandler = new Handler(request =>
        {
            Assert.Equal(65536, Assert.Single(request.Headers.Range!.Ranges).From);
            Assert.Equal("\"fixture-1\"", request.Headers.IfRange!.EntityTag!.ToString());
            var response = Response(request, new MemoryStream(data[65536..]));
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(65536, data.Length - 1, data.Length);
            return response;
        });
        using var resumed = new HttpClient(resumedHandler);
        Assert.Equal(DownloadOutcome.Verified, await new ResumableDownload(resumed, workspace).RunAsync(package, CancellationToken.None, null, Token));
        Assert.Equal(data, await File.ReadAllBytesAsync(workspace.Resolve("package.verified"), Token));
    }

    [Fact]
    public async Task ProgressingTransferDoesNotHaveAnAbsoluteBodyDeadline()
    {
        byte[] data = RandomNumberGenerator.GetBytes(250000);
        using var time = new ManualTimeProvider();
        using var handler = new Handler(request => Response(request, new MemoryStream(data)));
        using var client = new HttpClient(handler);
        var workspace = new OwnedWorkspace(_root);
        var download = new ResumableDownload(client, workspace, (_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        }, timeProvider: time);
        Assert.Equal(DownloadOutcome.Verified, await download.RunAsync(Identity(data), CancellationToken.None, null, Token));
        Assert.True(time.GetUtcNow() > DateTimeOffset.UnixEpoch.AddSeconds(30));
        Assert.Equal(data, await File.ReadAllBytesAsync(workspace.Resolve("package.verified"), Token));
    }

    [Fact]
    public async Task PauseDuringBlockedRead_ResumesFromDiskWithRangeAndValidator()
    {
        var data = new byte[150000];
        RandomNumberGenerator.Fill(data);
        var firstChunkRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pause = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new Handler(request => Response(request, new BlockedStream(data, firstChunkRead)));
        using var client = new HttpClient(handler);
        var workspace = new OwnedWorkspace(_root);
        var package = Identity(data);
        var downloading = new ResumableDownload(client, workspace).RunAsync(package, pause.Token, null, Token);
        await firstChunkRead.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        pause.Cancel();
        Assert.Equal(DownloadOutcome.Paused, await downloading.WaitAsync(TimeSpan.FromSeconds(5), Token));
        long saved = new FileInfo(workspace.Resolve("package.partial")).Length;
        Assert.Equal(65536, saved);

        using var resumedHandler = new Handler(request =>
        {
            Assert.Equal(saved, Assert.Single(request.Headers.Range!.Ranges).From);
            Assert.Equal("\"fixture-1\"", request.Headers.IfRange!.EntityTag!.ToString());
            var response = Response(request, new MemoryStream(data[(int)saved..]));
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(saved, data.Length - 1, data.Length);
            return response;
        });
        using var resumed = new HttpClient(resumedHandler);
        Assert.Equal(DownloadOutcome.Verified, await new ResumableDownload(resumed, new OwnedWorkspace(_root)).RunAsync(package, TestContext.Current.CancellationToken, null, TestContext.Current.CancellationToken));
        Assert.Equal(data, await File.ReadAllBytesAsync(workspace.Resolve("package.verified"), Token));
        Assert.False(File.Exists(workspace.Resolve("package.partial")));
    }

    [Fact]
    public async Task CancelDuringBlockedRead_DiscardsPartialAndCheckpoint()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] data = new byte[150000];
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var handler = new Handler(request => Response(request, new BlockedStream(data, reached)));
        using var client = new HttpClient(handler);
        var workspace = new OwnedWorkspace(_root);
        var task = new ResumableDownload(client, workspace).RunAsync(Identity(data), TestContext.Current.CancellationToken, null, cancel.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        cancel.Cancel();
        Assert.Equal(DownloadOutcome.Cancelled, await task.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.False(File.Exists(workspace.Resolve("package.partial")));
        Assert.False(File.Exists(workspace.Resolve("download.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_RejectsWrongRangeOrChangedValidator(bool wrongValidator)
    {
        byte[] data = [1, 2, 3, 4];
        var package = Identity(data);
        var workspace = new OwnedWorkspace(_root);
        File.WriteAllBytes(workspace.Resolve("package.partial"), [1, 2]);
        OwnedWorkspace.WriteJson(workspace.Resolve("download.json"), new DownloadCheckpoint(package, "\"fixture-1\"", true));
        using var handler = new Handler(request =>
        {
            var response = Response(request, new MemoryStream([3, 4]));
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(wrongValidator ? 2 : 1, 3, 4);
            if (wrongValidator) response.Headers.ETag = new EntityTagHeaderValue("\"changed\"");
            return response;
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ResumableDownload(client, workspace).RunAsync(package, TestContext.Current.CancellationToken, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(workspace.Resolve("package.verified")));
        Assert.False(File.Exists(workspace.Resolve("package.partial")));
    }

    [Fact]
    public async Task ServerIgnoresRange_RestartsInsteadOfAppending()
    {
        byte[] data = [1, 2, 3, 4];
        var package = Identity(data);
        var workspace = new OwnedWorkspace(_root);
        File.WriteAllBytes(workspace.Resolve("package.partial"), [9, 9]);
        OwnedWorkspace.WriteJson(workspace.Resolve("download.json"), new DownloadCheckpoint(package, null, true));
        using var handler = new Handler(request => Response(request, new MemoryStream(data)));
        using var client = new HttpClient(handler);
        Assert.Equal(DownloadOutcome.Verified, await new ResumableDownload(client, workspace).RunAsync(package, TestContext.Current.CancellationToken, null, TestContext.Current.CancellationToken));
        Assert.Equal(data, File.ReadAllBytes(workspace.Resolve("package.verified")));
    }

    [Fact]
    public async Task HashMismatch_IsNeverPromotedToVerified()
    {
        byte[] data = [1, 2, 3, 4];
        var workspace = new OwnedWorkspace(_root);
        File.WriteAllText(workspace.Resolve("package.verified"), "old package must not look like the new verified download");
        using var handler = new Handler(request => Response(request, new MemoryStream([4, 3, 2, 1])));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ResumableDownload(client, workspace).RunAsync(Identity(data), TestContext.Current.CancellationToken, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(workspace.Resolve("package.verified")));
    }

    [Fact]
    public async Task FailedHealthAndInterruptedHelper_RestorePreviousGenerationWithoutTouchingData()
    {
        var workspace = new OwnedWorkspace(_root);
        var deployment = new PortableDeployment(workspace);
        Directory.CreateDirectory(workspace.Resolve("data"));
        File.WriteAllText(workspace.Resolve("data/settings.json"), "unchanged");
        string zip = MakeArchive(workspace, "app.txt", "payload");
        long length = new FileInfo(zip).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        var first = await deployment.ApplyAsync(zip, length, hash, "1.0.0", (_, _) => Task.FromResult(true), Token);
        var failed = await deployment.ApplyAsync(zip, length, hash, "1.0.1", (_, _) => Task.FromResult(false), Token);
        Assert.True(first.Committed);
        Assert.True(failed.Recovered);
        Assert.Equal(first.Current, failed.Current);
        await Assert.ThrowsAsync<IOException>(() => deployment.ApplyAsync(zip, length, hash, "1.0.2", (_, _) => Task.FromResult(true), Token,
            () => throw new IOException("simulated helper termination")));
        Assert.True(deployment.ReadState().PendingHealth);
        var restored = new PortableDeployment(new OwnedWorkspace(_root)).Recover();
        Assert.Equal(first.Current, restored.Current);
        Assert.False(restored.PendingHealth);
        Assert.Equal("unchanged", File.ReadAllText(workspace.Resolve("data/settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialReleaseArchive_StripsOneRootAndRejectsMixedLayouts(bool mixed)
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = MakeArchive(workspace, "AudioPilot/AudioPilot.exe", "payload");
        if (mixed)
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
            using var writer = new StreamWriter(archive.CreateEntry("outside.txt").Open());
            writer.Write("must not be extracted");
        }
        var deployment = new PortableDeployment(workspace);
        bool launched = false;
        Task<DeploymentResult> Apply() => deployment.ApplyAsync(zip, new FileInfo(zip).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))), "1.0.1", (directory, _) =>
            {
                launched = true;
                Assert.Equal("payload", File.ReadAllText(Path.Combine(directory, "AudioPilot.exe")));
                Assert.False(Directory.Exists(Path.Combine(directory, "AudioPilot")));
                return Task.FromResult(true);
            }, Token);
        if (mixed)
        {
            await Assert.ThrowsAsync<InvalidDataException>(Apply);
            Assert.False(launched);
            Assert.Null(deployment.ReadState().Current);
        }
        else
        {
            Assert.True((await Apply()).Committed);
            Assert.True(launched);
        }
    }

    [Theory]
    [InlineData("../data/settings.json")]
    [InlineData("C:/outside.txt")]
    [InlineData("app.txt:alternate")]
    [InlineData("NUL.txt")]
    public async Task UnsafeArchive_NeverChangesCurrentOrUserData(string name)
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = MakeArchive(workspace, name, "malicious");
        await Assert.ThrowsAsync<InvalidDataException>(() => new PortableDeployment(workspace).ApplyAsync(zip, new FileInfo(zip).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))), "1.0.0", (_, _) => throw new InvalidOperationException("Must not launch"), Token));
        Assert.Null(new PortableDeployment(workspace).ReadState().Current);
    }

    [Fact]
    public async Task RestartBarrier_RejectsFailedDrainAndNewMicrophoneOwnership()
    {
        bool owned = false;
        Assert.Equal(RestartReadiness.SaveOrDrainFailed, await RestartBarrier.PrepareAsync(() => owned, _ => Task.FromResult(false), Token));
        Assert.Equal(RestartReadiness.MicrophoneOwned, await RestartBarrier.PrepareAsync(() => owned, _ => { owned = true; return Task.FromResult(true); }, Token));
    }

    [Fact]
    public async Task PortableRetentionKeepsRollbackAndPendingRecoveryButReclaimsFailedAndSupersededPayloads()
    {
        var workspace = new OwnedWorkspace(_root);
        string zip = MakeArchive(workspace, "app.txt", "payload");
        long length = new FileInfo(zip).Length;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        var deployment = new PortableDeployment(workspace);
        string? oldest = null;
        for (int version = 1; version <= 4; version++)
        {
            var installed = await deployment.ApplyAsync(zip, length, hash, $"1.0.{version}", (_, _) => Task.FromResult(true), Token);
            oldest ??= installed.Current;
        }
        Assert.Equal(2, Directory.EnumerateDirectories(workspace.Resolve("versions")).Count());
        string foreign = workspace.Resolve("versions/unowned-folder");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "unrelated");
        for (int attempt = 0; attempt < 3; attempt++)
            await deployment.ApplyAsync(zip, length, hash, "1.0.5", (_, _) => Task.FromResult(false), Token);
        Assert.Equal(3, Directory.EnumerateDirectories(workspace.Resolve("versions")).Count());
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        Assert.False(Directory.Exists(workspace.Resolve("versions/" + oldest)));

        string data = workspace.Resolve("data"); Directory.CreateDirectory(data);
        var recovery = new DeploymentRecovery(workspace);
        recovery.Begin(Guid.NewGuid().ToString("N"), data, workspace.Resolve("versions/" + deployment.ReadState().Current + "/AudioPilot.exe"), false);
        string held = UpdateStorage.CreateDirectory(workspace, "versions/1.0.5_" + Guid.NewGuid().ToString("N"), "generation");
        recovery.SetCandidate(new CandidateOwner(1, 1, Path.Combine(held, "AudioPilot.exe")));
        deployment.Trim();
        Assert.True(Directory.Exists(held));
        recovery.Complete();
        Assert.False(Directory.Exists(held));
    }

    [Fact]
    public async Task StorageCleanupDefersRedirectedTreesWithoutTouchingTheTarget()
    {
        var workspace = new OwnedWorkspace(_root);
        string owned = UpdateStorage.CreateDirectory(workspace, "versions/owned", "generation");
        string target = workspace.Resolve("user-data"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        string junction = Path.Combine(owned, "redirected");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", junction, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(Token);
        Assert.Equal(0, process.ExitCode);
        try
        {
            UpdateStorage.Trim(workspace, "versions", "generation", []);
            Assert.True(Directory.Exists(owned));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void BusyCleanupRetainsOwnershipSoTheNextAttemptCanReclaimTheDirectory()
    {
        var workspace = new OwnedWorkspace(_root);
        string owned = UpdateStorage.CreateDirectory(workspace, "versions/owned", "generation");
        string payload = Path.Combine(owned, "payload.dll"); File.WriteAllText(payload, "busy");
        using (var busy = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            UpdateStorage.Trim(workspace, "versions", "generation", []);
            Assert.True(File.Exists(Path.Combine(owned, UpdateStorage.Marker)));
            Assert.True(File.Exists(payload));
        }
        UpdateStorage.Trim(workspace, "versions", "generation", []);
        Assert.False(Directory.Exists(owned));
    }

    private static string MakeArchive(OwnedWorkspace workspace, string name, string content)
    {
        string path = workspace.Resolve("fixture.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
        return path;
    }

    private static PackageIdentity Identity(byte[] data) => new(new Uri("https://updates.example.test/package.zip"), data.Length, Convert.ToHexString(SHA256.HashData(data)));
    private static HttpResponseMessage Response(HttpRequestMessage request, Stream content) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StreamContent(content),
        Headers = { ETag = new EntityTagHeaderValue("\"fixture-1\"") },
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class BlockedHeaders(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The blocked response must be canceled.");
        }
    }

    private sealed class BlockedStream(byte[] bytes, TaskCompletionSource reached) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..Math.Min(65536, buffer.Length)], cancellationToken);
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
