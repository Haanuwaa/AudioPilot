using System.Net;
using System.Text;
using AudioPilot.Constants;
using AudioPilot.Services.Updates;

namespace AudioPilot.Tests.Services.Updates;

public sealed class GitHubReleaseServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.10.0", "1.10.0")]
    [InlineData("V2.0.0+build.4", "2.0.0")]
    public async Task StableRelease_ParsesVersionAndUsesTrustedReleaseUrl(string tag, string version)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(AppConstants.Links.LatestReleaseApiUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json($$"""{"draft":false,"prerelease":false,"tag_name":"{{tag}}","html_url":"https://untrusted.example/download"}"""));
        }));
        PublishedRelease? release = await new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(release);
        Assert.Equal(new Version(version), release.Version);
        Assert.Equal(AppConstants.Links.RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag), release.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"draft\":true,\"prerelease\":false,\"tag_name\":\"v2.0.0\"}")]
    [InlineData("{\"draft\":false,\"prerelease\":true,\"tag_name\":\"v2.0.0-preview\"}")]
    public async Task DraftOrPrerelease_IsIgnored(string json)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json))));
        Assert.Null(await new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("v1.2.3-preview")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v01.2.3")]
    [InlineData("v2147483648.0.0")]
    [InlineData("../other")]
    public async Task InvalidStableTag_IsRejected(string tag)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json($$"""{"draft":false,"prerelease":false,"tag_name":"{{tag}}"}"""))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"draft\":false,\"prerelease\":\"false\",\"tag_name\":\"v1.0.0\"}")]
    public async Task MissingOrWrongTypeFields_AreRejected(string json)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(json))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NotFound_MeansNoPublishedRelease()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnavailableService_ReportsFailureForBackoff(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Equal(status, exception.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBody_IsRejectedWithOrWithoutContentLength(bool declaredLength)
    {
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = declaredLength
                    ? new ByteArrayContent(new byte[1024 * 1024 + 1])
                    : new UnknownLengthContent(),
            };
            if (!declaredLength) Assert.Null(response.Content.Headers.ContentLength);
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseService(client).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_InterruptsAnActiveRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        }));
        using var cancellation = new CancellationTokenSource();
        Task<PublishedRelease?> pending = new GitHubReleaseService(client).GetLatestAsync(cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[1024 * 1024 + 1]).AsTask();
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
