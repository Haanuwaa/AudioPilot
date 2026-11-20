using System.Text.Json;

namespace AudioPilot.Updater.Trust;

internal sealed class UpdateFeedException(string reason, Exception? inner = null) : Exception("Update feed verification did not complete.", inner)
{
    private static readonly JsonSerializerOptions Json = new(UpdateTrustMetadata.Json) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal string Reason { get; } = IsKnown(reason) ? reason : "feed-verification-failed";
    internal static bool IsKnown(string reason) => reason is "feed-expired" or "feed-trust-state" or "feed-network" or
        "feed-rate-limited" or "feed-access-denied" or "feed-unavailable" or "feed-verification-failed" or "update-no-longer-offered";

    internal static UpdateFeedException FromBridge(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var failure = JsonSerializer.Deserialize<BridgeFailure>(bytes, Json);
            if (failure?.Schema == 1 && IsKnown(failure.Reason)) return new(failure.Reason);
        }
        catch (JsonException) { }
        return new("feed-verification-failed");
    }

    private sealed record BridgeFailure(int Schema, string Reason);
}
