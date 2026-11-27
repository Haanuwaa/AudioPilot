using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPilot.Updater.Trust;

internal sealed record UpdateArtifact(string FileName, string Kind, string Architecture, string Runtime, long Length, string Sha256);
internal sealed record UpdateSelection(string Kind, string Architecture, string Runtime, string InstalledVersion, string HelperVersion);

/// <summary>Bounded serialization and validation for update metadata and package identities.</summary>
internal static class UpdateTrustMetadata
{
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
}
