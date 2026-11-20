using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AudioPilot.Updater;
using AudioPilot.Updater.Trust;
using AudioPilot.Updater.Validation;

namespace AudioPilot.Tests.Services;

public sealed partial class LocalUpdateJobTests
{
    private static readonly string[] SelectedTargets = ["latest.zip", "candidate.zip"];
    [Theory]
    [InlineData("current", nameof(UpdateJobPhase.Current))]
    [InlineData("no-release", nameof(UpdateJobPhase.Current))]
    [InlineData("variant-unavailable", nameof(UpdateJobPhase.Unavailable))]
    [InlineData("bootstrap-required", nameof(UpdateJobPhase.Unavailable))]
    public async Task StableDiscoveryOutcomesDoNotStartPreparationAndCanBeCheckedAgain(string status, string phase)
    {
        var runtime = new Runtime();
        await using var job = new LocalUpdateJob(Workspace, new SelectionTrust(status), runtime);
        var feed = new HttpsTestFeed("https://127.0.0.1:9443/", "unused", "unused", "candidate.zip");
        Assert.Equal(phase, (await job.CheckAsync(feed, Token)).Phase.ToString());
        Assert.Equal(status, job.Snapshot.Reason);
        Assert.Null(job.Snapshot.Artifact);
        Assert.Equal(phase, (await job.CheckAsync(feed, Token)).Phase.ToString());
        Assert.Equal(0, runtime.Prepares);
        Assert.Equal(0, runtime.Deploys);
    }

    [Fact]
    public async Task DownloadReauthorizesTheApprovedArtifactInsteadOfSelectingANewerRelease()
    {
        var trust = await CreatePackageAsync(); var runtime = new Runtime();
        var targets = new List<string>();
        var selecting = new TrackingTrust(trust, targets);
        await using var job = new LocalUpdateJob(Workspace, selecting, runtime);
        var feed = new HttpsTestFeed("https://127.0.0.1:9443/", "unused", "unused", "latest.zip");
        Assert.Equal(UpdateJobPhase.Available, (await job.CheckAsync(feed, Token)).Phase);
        using var client = new HttpClient(new HttpReplies(_ => throw new IOException("Stop before transfer.")));
        _ = await job.DownloadAsync(feed, client, Token);
        Assert.Equal(SelectedTargets, targets);
        Assert.Equal(0, runtime.Prepares);
    }

    private sealed class SelectionTrust(string status) : ILocalUpdateTrust
    {
        public Task<AuthorizedUpdate> AuthorizeAsync(string target, UpdateSelection selection, CancellationToken token) => throw new UpdateSelectionException(status);
        public Task<VerifiedLocalUpdate> OpenPackageAsync(string path, UpdateSelection selection, CancellationToken token) => throw new InvalidOperationException("No package was selected.");
    }
    private sealed class TrackingTrust(Trust inner, List<string> targets) : ILocalUpdateTrust
    {
        public Task<AuthorizedUpdate> AuthorizeAsync(string target, UpdateSelection selection, CancellationToken token)
        { targets.Add(target); return inner.AuthorizeAsync(target, selection, token); }
        public Task<VerifiedLocalUpdate> OpenPackageAsync(string path, UpdateSelection selection, CancellationToken token) => inner.OpenPackageAsync(path, selection, token);
    }

    [Fact]
    public async Task ChangedSavedJobVersionCannotMislabelAnAuthorizedPackageOrPrepareTheApp()
    {
        var trust = await CreatePackageAsync(); var runtime = new Runtime();
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime)) await job.StageAsync(Source, Token);
        string path = Workspace.Resolve("job.json");
        OwnedWorkspace.WriteJson(path, OwnedWorkspace.ReadJson<UpdateJobState>(path) with { Version = "9.9.9" });
        await using var reopened = new LocalUpdateJob(Workspace, trust, runtime);
        Assert.Equal(UpdateJobPhase.Failed, (await reopened.ApplyAsync(Token)).Phase);
        Assert.Equal(0, runtime.Prepares);
        Assert.Equal(0, runtime.Deploys);
    }

    [Fact]
    public void HttpsClient_UsesPublicCertificateWithoutRequiringOrPersistingItsPrivateKey()
    {
        Directory.CreateDirectory(_root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test root", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string pem = Path.Combine(_root, "public.pem"); File.WriteAllText(pem, certificate.ExportCertificatePem());
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(pem), StringComparison.Ordinal);
        using var client = new HttpsTestFeed("https://127.0.0.1:9443/", pem, Convert.ToHexString(SHA256.HashData(certificate.RawData)), "candidate.zip").CreateClient();
        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
    }
    [Fact]
    public async Task HttpsInterruptedTransfer_ResumesThroughControllerAndDoesNotPrepareBeforeVerification()
    {
        var trust = await CreatePackageAsync(); var runtime = new Runtime();
        byte[] data = await File.ReadAllBytesAsync(Source, Token);
        int calls = 0; long resumed = 0;
        using var client = new HttpClient(new HttpReplies(request =>
        {
            calls++;
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(data)) };
            resumed = offset;
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(data[(int)offset..]) };
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
            return response;
        }));
        var feed = new HttpsTestFeed("https://127.0.0.1:9443/", "unused", "unused", "candidate.zip");
        await using (var job = new LocalUpdateJob(Workspace, trust, runtime))
        {
            Assert.Equal(UpdateJobPhase.Failed, (await job.DownloadAsync(feed, client, Token)).Phase);
            Assert.Equal(0, runtime.Prepares);
        }
        await using var reopened = new LocalUpdateJob(Workspace, trust, runtime);
        Assert.Equal(UpdateJobPhase.Ready, (await reopened.DownloadAsync(feed, client, Token)).Phase);
        Assert.True(resumed > 0);
        Assert.Equal(2, calls);
        Assert.Equal(0, runtime.Prepares);
        Assert.Equal(UpdateJobPhase.Completed, (await reopened.ApplyAsync(Token)).Phase);
        Assert.Equal(1, runtime.Deploys);
    }
    [Theory]
    [InlineData("http://127.0.0.1:9443/")]
    [InlineData("https://example.com/")]
    [InlineData("https://127.0.0.1:9443/path/")]
    public void HttpsTestFeed_RejectsPublicAndUnboundOrigins(string origin) =>
        Assert.Throws<InvalidDataException>(() => new HttpsTestFeed(origin, "unused", "unused", "candidate.zip").BaseUri);

    private sealed class HttpReplies(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request); response.RequestMessage = request; response.Headers.ETag = new EntityTagHeaderValue("\"test-target\"");
            return Task.FromResult(response);
        }
    }
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 65536) throw new IOException("Injected connection loss");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 65536)], cancellationToken);
        }
    }
}
