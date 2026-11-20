using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPilot.UpdateProtocol;

/// <summary>Build-time feed and publisher pins. Stable public access stays disabled until rollout is explicitly enabled.</summary>
public sealed record UpdateClientPolicy(int Schema, string? CandidateVersion, string BootstrapRoot, string PublisherSha256,
    string Channel = "staging", string? BootstrapSha256 = null, bool PublicUpdatesEnabled = false)
{
    public const string Origin = "https://github.com/Haanuwaa/AudioPilot/releases/download/updater-staging-feed/";
    public const string RootSha256 = "85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F";
    public const string StableOrigin = "https://github.com/Haanuwaa/AudioPilot/releases/download/updater-stable-feed/";
    [JsonIgnore]
    public string RootPin => Channel == "stable" ? BootstrapSha256! : RootSha256;
    [JsonIgnore]
    public bool IsEnabled => Channel == "staging" || PublicUpdatesEnabled || Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase);
    private const string ResourceName = "AudioPilot.UpdateProtocol.PilotPolicy.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private static readonly Lazy<UpdateClientPolicy?> Embedded = new(ReadEmbedded);
    public static UpdateClientPolicy? Current => Embedded.Value;
    public static string RuntimeRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioPilot", Current?.Channel == "stable" ? "updater" : "updater-pilot", "runtime");

    public static bool IsHelperPath(string path)
    {
        if (Current?.IsEnabled != true || !Path.IsPathFullyQualified(path)) return false;
        string relative = Path.GetRelativePath(RuntimeRoot, path);
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) || parts[1] != "AudioPilot.Updater.exe") return false;
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return File.Exists(path);
    }

    public static UpdateClientPolicy Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 98304) throw new InvalidDataException("Invalid update policy length.");
        var policy = JsonSerializer.Deserialize<UpdateClientPolicy>(bytes, Json) ?? throw new InvalidDataException("Missing update policy.");
        bool validCandidate = policy.Channel == "staging" && Version.TryParse(policy.CandidateVersion, out var version) &&
            version.Revision == -1 && version.Build >= 0 && version.ToString(3) == policy.CandidateVersion;
        if (policy.Schema != 1 || policy.PublicUpdatesEnabled && policy.Channel != "stable" || policy.Channel is not ("stable" or "staging") ||
            (policy.Channel == "stable" ? policy.CandidateVersion != null || !IsDigest(policy.BootstrapSha256) : !validCandidate || policy.BootstrapSha256 != null) ||
            !IsDigest(policy.PublisherSha256))
            throw new InvalidDataException("Invalid update policy or channel activation.");
        byte[] root;
        try { root = Convert.FromBase64String(policy.BootstrapRoot); }
        catch (FormatException error) { throw new InvalidDataException("Invalid updater bootstrap.", error); }
        if (root.Length > 65536 || Convert.ToHexString(SHA256.HashData(root)) != policy.RootPin)
            throw new InvalidDataException("The updater bootstrap differs from the independent root pin.");
        return policy with { PublisherSha256 = policy.PublisherSha256.ToUpperInvariant() };
    }

    public string Target(string kind) => Channel == "staging" && kind is "portable" or "msi"
        ? $"AudioPilot-{CandidateVersion}-win-x64.{(kind == "msi" ? "msi" : "zip")}" : throw new InvalidDataException("Unsupported pilot deployment.");

    public static void RequirePackaged()
    {
        if (Current == null) throw new InvalidOperationException("This build has no packaged updater policy.");
    }

    public static void RequireConfigured()
    {
        if (Current?.IsEnabled != true) throw new InvalidOperationException("Updates are disabled in this build outside isolated validation.");
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');

    private static UpdateClientPolicy? ReadEmbedded()
    {
        using var input = typeof(UpdateClientPolicy).Assembly.GetManifestResourceStream(ResourceName);
        if (input == null) return null;
        if (input.Length > 98304) throw new InvalidDataException("Oversized embedded updater policy.");
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Parse(bytes);
    }
}
