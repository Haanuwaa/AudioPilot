using System.Net;
using AudioPilot.UpdateProtocol;
using AudioPilot.Updater.Trust;
using AudioPilot.Updater.Validation;

namespace AudioPilot.Updater;

/// <summary>Stable discovery is authenticated by TUF; package names never come from an unsigned release listing.</summary>
internal sealed class HttpsStableFeed(UpdateClientPolicy policy, HttpsTestFeed? sandboxFeed = null) : IUpdateFeed
{
    private readonly Uri _baseUri = Validate(policy);
    public UpdateClientPolicy Policy { get; } = policy;
    internal HttpsTestFeed? SandboxFeed { get; } = ValidateSandboxFeed(sandboxFeed);
    private static HttpsTestFeed? ValidateSandboxFeed(HttpsTestFeed? feed)
    {
        if (feed != null) MsiDeployment.RequireSandbox();
        return feed;
    }
    internal const string Latest = "latest-stable";
    public string Target => Latest;
    public Uri BaseUri
    {
        get
        {
            if (SandboxFeed != null) { MsiDeployment.RequireSandbox(); return SandboxFeed.BaseUri; }
            if (!Policy.PublicUpdatesEnabled) MsiDeployment.RequireSandbox();
            return _baseUri;
        }
    }
    private static Uri Validate(UpdateClientPolicy policy)
    {
        if (policy.Channel != "stable" || UpdateClientPolicy.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(policy)) != policy)
            throw new InvalidDataException("Invalid stable feed policy.");
        return new(UpdateClientPolicy.StableOrigin);
    }
    // A stable source identity survives newer releases; the selected artifact remains separately pinned in job state.
    public Uri PackageUri => new(BaseUri, Latest);
    public Uri GetPackageUri(UpdateArtifact artifact)
    {
        UpdateTrustMetadata.ValidateVariant(artifact.Kind, artifact.Architecture, artifact.Runtime);
        if (!UpdateTrustMetadata.IsDigest(artifact.Sha256) || artifact.FileName.Length is < 1 or > 160 ||
            artifact.FileName.StartsWith('.') || artifact.FileName.Contains("..", StringComparison.Ordinal) ||
            artifact.FileName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
            !artifact.FileName.EndsWith(artifact.Kind == "msi" ? ".msi" : ".zip", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid stable package.");
        return new(BaseUri, artifact.Sha256.ToLowerInvariant() + "." + artifact.FileName);
    }
    public HttpClient CreateClient() => SandboxFeed?.CreateClient() ?? new(new HttpsStagingFeed.ReleaseRedirectHandler(BaseUri.AbsoluteUri, new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.None
    }))
    { Timeout = TimeSpan.FromMinutes(10) };
}

internal sealed class UpdateSelectionException(string status) : Exception("Stable selection: " + status)
{
    internal string Status { get; } = status;
}
