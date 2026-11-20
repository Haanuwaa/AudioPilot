using System.Security.Cryptography;

namespace AudioPilot.Updater.Trust;

/// <summary>Development fixture only. Private keys stay in memory and are never exported or saved.</summary>
internal sealed class TemporaryUpdateSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public UpdatePublicKey PublicKey { get; }
    public TemporaryUpdateSigner()
    {
        byte[] spki = _key.ExportSubjectPublicKeyInfo();
        PublicKey = new(UpdateTrustMetadata.Digest(spki), Convert.ToBase64String(spki));
    }
    public static byte[] Sign<T>(string role, T metadata, params TemporaryUpdateSigner[] signers) => SignPayload(role, UpdateTrustMetadata.Encode(metadata), signers);
    public static byte[] SignPayload(string role, byte[] payload, params TemporaryUpdateSigner[] signers)
    {
        byte[] message = UpdateTrustMetadata.SigningBytes(role, payload);
        return UpdateTrustMetadata.Encode(new SignedUpdateDocument(Convert.ToBase64String(payload),
            [.. signers.Select(signer => new UpdateSignature(signer.PublicKey.Id,
                Convert.ToBase64String(signer._key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))))]));
    }
    public void Dispose() => _key.Dispose();
}

/// <summary>Public local fixture files accompany a separately passed bootstrap pin; no feed or production key exists.</summary>
internal static class LocalUpdateTrustFixture
{
    public static string Create(OwnedWorkspace workspace, string package, string kind)
    {
        using var rootKey = new TemporaryUpdateSigner();
        using var releaseKey = new TemporaryUpdateSigner();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] root = TemporaryUpdateSigner.Sign("root", new UpdateRoot(1, UpdateTrustMetadata.Scope, 1, 1, now.AddDays(2),
            [rootKey.PublicKey], 1, [releaseKey.PublicKey], 1), rootKey);
        byte[] release = TemporaryUpdateSigner.Sign("release", new UpdateRelease(1, "AudioPilot", "local-test", 1, 1,
            "1.0.1", "1.0.0", now, now.AddDays(1), [Artifact(package, kind, "win-x64", "self-contained")]), releaseKey);
        File.WriteAllBytes(workspace.Resolve("local-root.signed.json"), root);
        File.WriteAllBytes(workspace.Resolve("local-release.signed.json"), release);
        string pin = UpdateTrustMetadata.Digest(root);
        new LocalUpdateTrustClient(workspace, pin).Initialize(root);
        return pin;
    }
    public static UpdateArtifact Artifact(string path, string kind, string architecture, string runtime)
    {
        OwnedWorkspace.CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(Path.GetFileName(path), kind, architecture, runtime, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
    public static Task<VerifiedLocalUpdate> OpenAsync(OwnedWorkspace workspace, string pin, string path, string kind, CancellationToken token) =>
        new LocalUpdateTrustClient(workspace, pin).OpenPackageAsync(UpdateTrustMetadata.ReadFile(workspace.Resolve("local-release.signed.json")),
            new(kind, "win-x64", "self-contained", "1.0.0", "1.0.0"), path, token);
}
