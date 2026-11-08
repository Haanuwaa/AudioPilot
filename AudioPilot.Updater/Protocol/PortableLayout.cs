using System.Text.Json;

namespace AudioPilot.UpdateProtocol;

public sealed record PortableLayoutDescriptor(int Schema, string DiagnosticId, bool Managed = false);

/// <summary>Recognizes only the explicitly isolated, versioned portable updater layout.</summary>
public static class PortableLayout
{
    public const string FileName = "portable-layout.json";
    private static readonly JsonSerializerOptions Json = new() { AllowDuplicateProperties = false };

    public static string GetLaunchExecutable(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!Path.GetFileName(executable).Equals("AudioPilot.exe", StringComparison.OrdinalIgnoreCase)) return executable;
        string? data = GetDataRoot(Path.GetDirectoryName(executable)!, null);
        if (data == null) return executable;
        string launcher = Path.Combine(Path.GetDirectoryName(data)!, "AudioPilot.exe");
        if (!File.Exists(launcher)) throw new FileNotFoundException("The managed portable launcher is missing.", launcher);
        if ((File.GetAttributes(launcher) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected portable launcher.");
        return launcher;
    }

    public static string? GetDataRoot(string baseDirectory, string? diagnosticId)
    {
        string generation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        string? versions = Path.GetDirectoryName(generation);
        if (versions == null || Path.GetFileName(versions) != "versions") return null;
        string root = Path.GetDirectoryName(versions)!;
        string manifest = Path.Combine(root, FileName);
        if (!File.Exists(manifest)) return null;
        for (string? path = generation; path != null; path = Path.GetDirectoryName(path))
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Redirected portable layout.");
        using var stream = File.OpenRead(manifest);
        if (stream.Length > 8192) throw new InvalidDataException("Oversized portable layout.");
        var descriptor = JsonSerializer.Deserialize<PortableLayoutDescriptor>(stream, Json);
        if (diagnosticId == null && descriptor?.Managed != true) return null;
        if (descriptor?.Schema != 1 || (diagnosticId == null ? !descriptor.Managed : descriptor.DiagnosticId != diagnosticId) ||
            !Guid.TryParseExact(descriptor.DiagnosticId, "N", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Portable layout does not belong to this diagnostic session.");
        string data = Path.Combine(root, "data");
        if (Path.Exists(data) && (File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Redirected portable data.");
        return data;
    }
}
