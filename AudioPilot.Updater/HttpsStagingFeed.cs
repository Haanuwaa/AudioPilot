using System.Net;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;

namespace AudioPilot.Updater;

internal interface IUpdateFeed
{
    string Target { get; }
    Uri BaseUri { get; }
    Uri PackageUri { get; }
    Uri GetPackageUri(UpdateArtifact artifact);
    HttpClient CreateClient();
}

/// <summary>Pinned prerelease connection for Sandbox tests and explicit pilot builds. Ordinary releases remain disabled.</summary>
internal sealed record HttpsStagingFeed(string Target, bool IsPilot = false) : IUpdateFeed
{
    internal const string Origin = "https://github.com/Haanuwaa/AudioPilot/releases/download/updater-staging-feed/";
    internal const string BootstrapHash = "85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F";
    internal const string PublisherHash = "B42574DFCDEBBFFFE1BD56426979ED0C8676DB5B860BEC3120B3E9D268BB6902";
    public Uri BaseUri
    {
        get
        {
            if (IsPilot)
            {
                UpdateClientPolicy.RequireConfigured();
                var policy = UpdateClientPolicy.Current!;
                if (Target != policy.Target("msi") && Target != policy.Target("portable")) throw new InvalidDataException("Target is outside the approved pilot release.");
            }
            else MsiDeployment.RequireSandbox();
            if (Target is not { Length: >= 1 and <= 160 } || Target.StartsWith('.') || Target.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || Target.Contains("..", StringComparison.Ordinal) ||
                !(Target.EndsWith(".zip", StringComparison.Ordinal) || Target.EndsWith(".msi", StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid staging target.");
            return new(Origin);
        }
    }
    public Uri PackageUri => new(BaseUri, Target);
    public Uri GetPackageUri(UpdateArtifact artifact) => UpdateTrustMetadata.IsDigest(artifact.Sha256) && artifact.FileName == Target
        ? new(BaseUri, artifact.Sha256.ToLowerInvariant() + "." + Target) : throw new InvalidDataException("The package differs from the bound staging target.");
    public HttpClient CreateClient()
    {
        _ = BaseUri;
        return new(new ReleaseRedirectHandler(Origin, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None
        }))
        { Timeout = TimeSpan.FromMinutes(10) };
    }

    internal static bool IsReleaseAsset(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && uri.Host == "release-assets.githubusercontent.com" && uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal);

    internal sealed class ReleaseRedirectHandler(string origin, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri is not { } initial || !initial.AbsoluteUri.StartsWith(origin, StringComparison.Ordinal) || initial.Query.Length != 0 || initial.Fragment.Length != 0 || request.Method != HttpMethod.Get)
                throw new InvalidDataException("Request left the staging release.");
            HttpRequestMessage current = request;
            try
            {
                for (int redirects = 0; ; redirects++)
                {
                    var response = await base.SendAsync(current, token).ConfigureAwait(false);
                    if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
                    Uri? location = response.Headers.Location;
                    Uri? next = location == null ? null : location.IsAbsoluteUri ? location : new(current.RequestUri!, location);
                    response.Dispose();
                    if (redirects >= 2 || next == null || !IsReleaseAsset(next)) throw new HttpRequestException("Staging release redirected outside its permitted CDN.");
                    var redirected = new HttpRequestMessage(HttpMethod.Get, next);
                    // Only transfer headers are forwarded; no credentials or cookies cross origins.
                    if (request.Headers.Range != null) redirected.Headers.Range = request.Headers.Range;
                    if (request.Headers.IfRange != null) redirected.Headers.IfRange = request.Headers.IfRange;
                    if (!ReferenceEquals(current, request)) current.Dispose();
                    current = redirected;
                }
            }
            finally { if (!ReferenceEquals(current, request)) current.Dispose(); }
        }
    }
}
