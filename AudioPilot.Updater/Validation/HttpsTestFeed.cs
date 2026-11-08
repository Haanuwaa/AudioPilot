using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AudioPilot.Updater.Validation;

/// <summary>Explicit loopback-only HTTPS transport with an isolated temporary trust root. Public feeds remain disabled.</summary>
internal sealed record HttpsTestFeed(string Origin, string RootCertificate, string RootSha256, string Target) : IUpdateFeed
{
    public Uri BaseUri
    {
        get
        {
            var uri = new Uri(Origin, UriKind.Absolute);
            if (uri.Scheme != "https" || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip) ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                Target.Length is < 1 or > 160 || Target.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || Target.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("Only an explicit loopback HTTPS test feed is permitted.");
            return uri;
        }
    }
    public Uri PackageUri => new(BaseUri, "targets/" + Uri.EscapeDataString(Target));
    public Uri GetPackageUri(Trust.UpdateArtifact artifact) => PackageUri;
    public HttpClient CreateClient()
    {
        _ = BaseUri;
        OwnedWorkspace.CheckAncestors(RootCertificate);
        if (new FileInfo(RootCertificate).Length > 16384) throw new InvalidDataException("Oversized TLS trust root.");
        var root = X509Certificate2.CreateFromPem(File.ReadAllText(RootCertificate));
        if (!Convert.ToHexString(SHA256.HashData(root.RawData)).Equals(RootSha256, StringComparison.Ordinal))
        { root.Dispose(); throw new InvalidDataException("The test TLS trust root changed."); }
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
        policy.CustomTrustStore.Add(root);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None };
        handler.SslOptions.CertificateChainPolicy = policy;
        return new FeedClient(handler, root) { Timeout = TimeSpan.FromMinutes(10) };
    }
    private sealed class FeedClient(SocketsHttpHandler handler, X509Certificate2 root) : HttpClient(handler)
    {
        protected override void Dispose(bool disposing) { try { base.Dispose(disposing); } finally { if (disposing) root.Dispose(); } }
    }
}
