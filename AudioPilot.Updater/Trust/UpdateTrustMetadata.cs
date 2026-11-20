using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPilot.Updater.Trust;

internal sealed record UpdatePublicKey(string Id, string SubjectPublicKeyInfo);
internal sealed record UpdateSignature(string KeyId, string Value);
internal sealed record SignedUpdateDocument(string Payload, UpdateSignature[] Signatures);
internal sealed record UpdateRoot(int Schema, string Scope, long Version, long Epoch, DateTimeOffset ExpiresUtc,
    UpdatePublicKey[] RootKeys, int RootThreshold, UpdatePublicKey[] ReleaseKeys, int ReleaseThreshold);
internal sealed record UpdateArtifact(string FileName, string Kind, string Architecture, string Runtime, long Length, string Sha256);
internal sealed record UpdateRelease(int Schema, string Product, string Channel, long Epoch, long Sequence, string Version,
    string MinimumHelperVersion, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, UpdateArtifact[] Artifacts);
internal sealed record UpdateSelection(string Kind, string Architecture, string Runtime, string InstalledVersion, string HelperVersion);

/// <summary>Bounded local-test documents. Signatures cover exact payload bytes with a role-specific domain.</summary>
internal static class UpdateTrustMetadata
{
    public const string Scope = "AudioPilot.LocalUpdater";
    public const int MaximumDocumentBytes = 64 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 12,
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static T Decode<T>(ReadOnlySpan<byte> bytes, int limit = MaximumDocumentBytes)
    {
        if (bytes.Length is 0 || bytes.Length > limit) throw new InvalidDataException("Update metadata exceeds its bound.");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty update metadata.");
    }
    public static byte[] ReadFile(string path, int limit = MaximumDocumentBytes)
    {
        OwnedWorkspace.CheckAncestors(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("Update metadata exceeds its bound.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static bool IsDigest(string value) => value.Length == 64 && value.All(ch => char.IsAsciiHexDigit(ch) && !char.IsAsciiLetterLower(ch));
    public static Version ParseVersion(string text)
    {
        if (!Version.TryParse(text, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != text)
            throw new InvalidDataException("Update versions require three nonnegative numeric components.");
        return version;
    }
    public static void ValidateSelection(UpdateSelection selection)
    {
        ValidateVariant(selection.Kind, selection.Architecture, selection.Runtime);
        _ = ParseVersion(selection.InstalledVersion);
        _ = ParseVersion(selection.HelperVersion);
    }
    public static void ValidateVariant(string kind, string architecture, string runtime)
    {
        if (kind is not ("msi" or "portable") || architecture is not ("win-x86" or "win-x64" or "win-arm64") ||
            runtime is not ("self-contained" or "framework-dependent") || (kind == "msi" && runtime != "self-contained"))
            throw new InvalidDataException("Unsupported update package variant.");
    }
    public static void ValidateRoot(UpdateRoot root)
    {
        if (root.Schema != 1 || root.Scope != Scope || root.Version is < 1 or > 1000000 || root.Epoch is < 1 or > 1000000 || root.ExpiresUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid update root.");
        ValidateKeys(root.RootKeys, root.RootThreshold);
        ValidateKeys(root.ReleaseKeys, root.ReleaseThreshold);
        if (root.RootKeys.Select(key => key.Id).Intersect(root.ReleaseKeys.Select(key => key.Id), StringComparer.Ordinal).Any())
            throw new InvalidDataException("Root and release signing keys must be separate.");
    }
    private static void ValidateKeys(UpdatePublicKey[] keys, int threshold)
    {
        if (keys.Length is < 1 or > 4 || keys.Any(key => key is null) || threshold < 1 || threshold > keys.Length || keys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidDataException("Invalid update signature threshold or key set.");
        foreach (var key in keys) { using var imported = ImportKey(key); }
    }
    public static ECDsa ImportKey(UpdatePublicKey key)
    {
        if (!IsDigest(key.Id) || key.SubjectPublicKeyInfo.Length > 256) throw new InvalidDataException("Invalid update key.");
        byte[] spki = Convert.FromBase64String(key.SubjectPublicKeyInfo);
        var algorithm = ECDsa.Create();
        try
        {
            algorithm.ImportSubjectPublicKeyInfo(spki, out int consumed);
            if (consumed != spki.Length || algorithm.KeySize != 256 || algorithm.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !algorithm.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki) || Digest(spki) != key.Id)
                throw new InvalidDataException("Only canonical P-256 update keys are supported.");
            return algorithm;
        }
        catch { algorithm.Dispose(); throw; }
    }
    public static byte[] Payload(SignedUpdateDocument document)
    {
        if (document.Payload.Length > 48 * 1024 || document.Signatures.Length is < 1 or > 8 || document.Signatures.Any(signature => signature is null) ||
            document.Signatures.Select(signature => signature.KeyId).Distinct(StringComparer.Ordinal).Count() != document.Signatures.Length)
            throw new InvalidDataException("Invalid update envelope.");
        foreach (var signature in document.Signatures)
            if (!IsDigest(signature.KeyId) || signature.Value.Length != 88 || Convert.FromBase64String(signature.Value).Length != 64)
                throw new InvalidDataException("Invalid update signature encoding.");
        byte[] payload = Convert.FromBase64String(document.Payload);
        if (payload.Length is 0 or > 32 * 1024) throw new InvalidDataException("Invalid update payload size.");
        return payload;
    }
    public static byte[] SigningBytes(string role, byte[] payload) => [.. System.Text.Encoding.ASCII.GetBytes(Scope + "/v1/" + role + "\n"), .. payload];
    public static void VerifyThreshold(string role, SignedUpdateDocument document, byte[] payload, UpdatePublicKey[] keys, int threshold)
    {
        byte[] message = SigningBytes(role, payload);
        int valid = 0;
        foreach (var key in keys)
        {
            var signature = document.Signatures.SingleOrDefault(candidate => candidate.KeyId == key.Id);
            if (signature == null) continue;
            using var algorithm = ImportKey(key);
            if (algorithm.VerifyData(message, Convert.FromBase64String(signature.Value), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) valid++;
        }
        if (valid < threshold) throw new CryptographicException("Update signature threshold was not met.");
    }
}
