using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioPilot.Updater.Trust;

internal sealed record TufBridgeRequest(int Schema, string Operation, string Nonce, string Repository, string Cache,
    string BootstrapHash, string Target, string Package, string Kind, string Origin = "", string TlsRoot = "", string TlsRootHash = "", string Transport = "");
internal sealed record TufTargetIdentity(string Product, string Channel, string Version, string Kind, string Architecture,
    string Runtime, string MinimumHelperVersion, string WindowsPublisherSha256 = "", string WindowsTrust = "");
internal sealed record TufBridgeResponse(int Schema, string Nonce, string BootstrapHash, string Target, long Length,
    string Sha256, TufTargetIdentity? Custom, string Library);
internal sealed record TufApplicationPolicy(int Schema, string BootstrapHash, string Version, string TargetHash, string Checksum = "");

/// <summary>Maintained TUF adapter for local, loopback and separately pinned Sandbox staging. It has no custom-verifier fallback.</summary>
internal sealed class TufLocalTrustClient(OwnedWorkspace workspace, string bootstrapHash, IUpdateFeed? feed = null) : ILocalUpdateTrust
{
    private static readonly JsonSerializerOptions Json = new(UpdateTrustMetadata.Json) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string BridgePath => Path.Combine(AppContext.BaseDirectory, "AudioPilot.TufBridge.exe");

    public static async Task<string> CreateFixtureAsync(OwnedWorkspace workspace, string package, string kind, CancellationToken token)
    {
        package = Path.GetFullPath(package);
        var request = Request("fixture", workspace, "", Path.GetFileName(package), package, kind);
        var response = await InvokeAsync(request, token);
        if (!UpdateTrustMetadata.IsDigest(response.BootstrapHash)) throw new InvalidDataException("The local TUF publisher returned an invalid root pin.");
        return response.BootstrapHash;
    }

    public async Task<VerifiedLocalUpdate> OpenPackageAsync(string path, UpdateSelection selection, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        UpdateTrustMetadata.ValidateSelection(selection);
        var authorization = await AuthorizeAsync(Path.GetFileName(path), selection, token);
        OwnedWorkspace.CheckAncestors(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != authorization.Artifact.Length ||
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != authorization.Artifact.Sha256)
                throw new CryptographicException("The package does not match its TUF target hashes and length.");
            stream.Position = 0;
            return new(() => AuthorizeAsync(Path.GetFileName(path), selection, CancellationToken.None).GetAwaiter().GetResult(), authorization, path, stream);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    public async Task<AuthorizedUpdate> AuthorizeAsync(string target, UpdateSelection selection, CancellationToken token)
    {
        UpdateTrustMetadata.ValidateSelection(selection);
        if (!UpdateTrustMetadata.IsDigest(bootstrapHash)) throw new InvalidDataException("A pinned TUF bootstrap root is required.");
        if (feed != null) _ = feed.BaseUri;
        if (feed is HttpsStagingFeed && bootstrapHash != HttpsStagingFeed.BootstrapHash) throw new InvalidDataException("Staging needs its independent root pin.");
        using var ownership = new FileStream(workspace.Resolve("tuf.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var policy = new TrustApplicationPolicyStore(workspace, bootstrapHash);
        _ = policy.Read();
        var request = Request("verify", workspace, bootstrapHash, target, "", "");
        if (feed != null)
        {
            _ = feed.BaseUri;
            request = feed switch
            {
                HttpsTestFeed local => request with { Origin = local.Origin, TlsRoot = local.RootCertificate, TlsRootHash = local.RootSha256 },
                HttpsStagingFeed when bootstrapHash == HttpsStagingFeed.BootstrapHash => request with { Origin = HttpsStagingFeed.Origin, Transport = "staging" },
                _ => throw new InvalidDataException("Unsupported or unpinned update feed.")
            };
        }
        var response = await InvokeAsync(request, token);
        var identity = response.Custom ?? throw new InvalidDataException("The TUF target has no AudioPilot identity.");
        if (feed is HttpsStagingFeed && (identity.WindowsPublisherSha256 != HttpsStagingFeed.PublisherHash || identity.WindowsTrust != "self-signed-staging"))
            throw new InvalidDataException("The staging Windows publisher differs from its approved test identity.");
        if (response.BootstrapHash != bootstrapHash || response.Target != target || identity.Product != "AudioPilot" ||
            identity.Channel != (feed is HttpsStagingFeed ? "staging" : "local-test") || identity.Kind != selection.Kind || identity.Architecture != selection.Architecture || identity.Runtime != selection.Runtime ||
            !UpdateTrustMetadata.IsDigest(response.Sha256) || response.Length is <= 0 or > 1024L * 1024 * 1024 ||
            Path.GetFileName(target) != target || !target.EndsWith(selection.Kind == "msi" ? ".msi" : ".zip", StringComparison.Ordinal))
            throw new InvalidDataException("The TUF target does not match this deployment.");
        Version version = UpdateTrustMetadata.ParseVersion(identity.Version);
        if (version <= UpdateTrustMetadata.ParseVersion(selection.InstalledVersion) ||
            UpdateTrustMetadata.ParseVersion(selection.HelperVersion) < UpdateTrustMetadata.ParseVersion(identity.MinimumHelperVersion))
            throw new InvalidDataException("The TUF release is not newer or needs a newer helper.");
        string targetHash = UpdateTrustMetadata.Digest(UpdateTrustMetadata.Encode(new
        {
            response.Target,
            response.Length,
            response.Sha256,
            Identity = identity
        }));
        policy.Advance(new TufApplicationPolicy(1, bootstrapHash, identity.Version, targetHash));
        var artifact = new UpdateArtifact(target, identity.Kind, identity.Architecture, identity.Runtime, response.Length, response.Sha256);
        // TUF owns repository versions and expiration. These legacy DTO fields are not used as a second trust protocol.
        var release = new UpdateRelease(1, identity.Product, identity.Channel, 1, 1, identity.Version,
            identity.MinimumHelperVersion, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, [artifact]);
        return new(release, artifact);
    }

    private static TufBridgeRequest Request(string operation, OwnedWorkspace workspace, string pin, string target, string package, string kind) =>
        new(1, operation, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), workspace.Resolve("tuf-repository"), workspace.Resolve("tuf-cache"), pin, target, package, kind);

    private static async Task<TufBridgeResponse> InvokeAsync(TufBridgeRequest request, CancellationToken token)
    {
        OwnedWorkspace.CheckAncestors(BridgePath);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(new ProcessStartInfo(BridgePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("The maintained TUF verifier did not start.");
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 16384, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError.BaseStream, 16384, deadline.Token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, Json), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            byte[] bytes = await output;
            byte[] failure = await errors;
            if (process.ExitCode != 0)
            {
                if (request.Origin.Length != 0) Console.Error.WriteLine("TUF HTTPS test feed: " + System.Text.Encoding.UTF8.GetString(failure));
                throw new InvalidDataException("TUF verification failed: " + System.Text.Encoding.UTF8.GetString(failure));
            }
            var response = JsonSerializer.Deserialize<TufBridgeResponse>(bytes, Json) ?? throw new InvalidDataException("Empty TUF bridge reply.");
            if (response.Schema != 1 || response.Nonce != request.Nonce || response.Library != "go-tuf/v2 v2.4.2")
                throw new InvalidDataException("Unexpected TUF bridge reply identity.");
            return response;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            deadline.Cancel();
            try { await Task.WhenAll(output, errors); }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException) { }
        }
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new InvalidDataException("TUF bridge output exceeded its bound.");
            output.Write(buffer, 0, count);
        }
    }
}
