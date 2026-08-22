using System.IO;

namespace AudioPilot.Platform;

/// <summary>
/// Gives explicitly launched diagnostic processes their own IPC identity and disposable data root.
/// A malformed opt-in fails closed; diagnostics must never fall back to the user's normal instance.
/// </summary>
internal static class DiagnosticSession
{
    internal const string EnvironmentVariable = "AUDIOPILOT_DIAGNOSTIC_SESSION";
    internal static string? Id { get; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));
    internal static bool IsActive => Id != null;
    internal static string? DataRoot => Id == null ? null : Path.Combine(Path.GetTempPath(), "AudioPilot.Diagnostics", Id);
    internal static string InstanceName => Id == null ? "AudioPilot" : $"AudioPilot.Diagnostics.{Id}";

    internal static string? Parse(string? value)
    {
        if (value == null) return null;
        if (!Guid.TryParseExact(value, "N", out Guid id) || id == Guid.Empty)
            throw new InvalidOperationException($"{EnvironmentVariable} must be a nonempty GUID in N format.");
        return id.ToString("N");
    }
}
