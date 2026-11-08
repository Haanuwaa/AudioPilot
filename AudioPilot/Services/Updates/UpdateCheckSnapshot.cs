using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioPilot.Logging;

namespace AudioPilot.Services.Updates;

internal sealed record UpdateCheckSnapshot(string InstalledVersion, string? LatestVersion, string ReleaseUrl,
    bool UpdateAvailable, bool AutomaticChecks, DateTimeOffset? LastCheckedUtc, string? ErrorCode, DateTimeOffset? RetryAfterUtc);

/// <summary>Small, non-sensitive check feedback shared with a headless CLI; it never authorizes packages.</summary>
internal static class UpdateCheckSnapshotStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private static string Path => System.IO.Path.Combine(AppDataPaths.GetLocalStateRoot(), "update-check.json");

    internal static UpdateCheckSnapshot? Read(string? path = null)
    {
        try
        {
            using var input = new FileStream(path ?? Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > 4096) return null;
            var snapshot = JsonSerializer.Deserialize<UpdateCheckSnapshot>(input, Json);
            if (snapshot == null || !Version.TryParse(snapshot.InstalledVersion, out _) ||
                snapshot.LatestVersion != null && !Version.TryParse(snapshot.LatestVersion, out _) ||
                snapshot.LastCheckedUtc?.Offset != TimeSpan.Zero && snapshot.LastCheckedUtc != null ||
                snapshot.RetryAfterUtc?.Offset != TimeSpan.Zero && snapshot.RetryAfterUtc != null) return null;
            return snapshot;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void Save(UpdateCheckSnapshot snapshot, Logger logger, string? path = null)
    {
        try { AtomicFileWriter.WriteAllText(path ?? Path, JsonSerializer.Serialize(snapshot, Json)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.Debug("UpdateCheck", () => $"update-check-feedback-not-saved | error={error.GetType().Name}"); }
    }
}
