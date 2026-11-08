using System.Reflection;
using System.Runtime.InteropServices;

namespace AudioPilot.Services.Diagnostics;

/// <summary>Process-wide application and runtime identity shared by Diagnostics and support bundles; contains no user paths or settings.</summary>
internal sealed record AppBuildInformation(
    string Version,
    string InformationalVersion,
    string? Revision,
    string Configuration,
    string OperatingSystem,
    string OSArchitecture,
    string ProcessArchitecture,
    string Framework,
    string Deployment = "Not recorded")
{
    internal static AppBuildInformation Current { get; } = Capture();

    internal IReadOnlyList<KeyValuePair<string, string>> GetDisplayRows() =>
    [
        new("Version", Version),
        new("Recorded revision", Revision ?? "Not recorded"),
        new("Build configuration", Configuration),
        new("Deployment", Deployment),
        new("App architecture", ProcessArchitecture.ToLowerInvariant()),
        new("OS architecture", OSArchitecture.ToLowerInvariant()),
        new(".NET runtime", Framework),
        new("Windows version", OperatingSystem),
    ];

    internal string ToDisplayText() => "AudioPilot build information" + Environment.NewLine
        + string.Join(Environment.NewLine, GetDisplayRows().Select(static row => $"{row.Key}: {row.Value}"));

    private static AppBuildInformation Capture()
    {
        Assembly assembly = typeof(AppBuildInformation).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "Unknown";
        string[] parts = informational.Split('+', 2);
        string? revision = parts.Length == 2 && parts[1].Length is >= 7 and <= 64 && parts[1].All(char.IsAsciiHexDigit)
            ? parts[1] : null;
        return new(parts[0], informational, revision,
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Not recorded",
            RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription,
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(static item => item.Key == "DeploymentType")?.Value ?? "Not recorded");
    }
}
