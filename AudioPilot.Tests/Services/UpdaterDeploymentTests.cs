using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AudioPilot.Updater;

namespace AudioPilot.Tests.Services;

public sealed class UpdaterDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AudioPilot-updater-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
