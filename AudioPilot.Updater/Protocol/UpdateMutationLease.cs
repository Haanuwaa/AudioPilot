using System.Text.Json;

namespace AudioPilot.UpdateProtocol;

public sealed class UpdateMutationBlockedException(string errorCode, string message) : IOException(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Protects headless mutations from a bound installation's deployment and configuration recovery.</summary>
public static class UpdateMutationLease
{
    private static readonly JsonSerializerOptions Json = new() { AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true };
    private sealed record Binding(int Schema, string Kind, string Root, string Identity);
    private sealed record Deployment(int Schema, string? Current, string? Previous, bool PendingHealth);
    private sealed record Recovery(int Schema, string DiagnosticId, bool Completed = false);

    public static IDisposable? Acquire(string applicationDirectory)
    {
        try
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            string? portable = PortableLayout.GetWorkspaceRoot(directory);
            string? versions = Path.GetDirectoryName(directory);
            if (portable == null && Path.GetFileName(versions) == "versions" && Path.GetFileName(Path.GetDirectoryName(versions)) == PortableLayout.AppDirectoryName)
                throw Unavailable();
            return AcquireWorkspace(directory, portable ?? UpdaterControlWire.Workspace(directory), portable != null);
        }
        catch (UpdateMutationBlockedException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw Unavailable(); }
    }

    internal static IDisposable? AcquireWorkspace(string directory, string workspace, bool portable)
    {
        FileStream? lease = null;
        try
        {
            if (!Exists(workspace))
            {
                if (portable) throw Unavailable();
                return null;
            }
            CheckPath(workspace);
            string application = Path.Combine(directory, "AudioPilot.exe");
            if (!portable)
            {
                // Do not create or inspect another installation's workspace. The helper records this owner before deployment.
                string owner = Path.Combine(workspace, ".updater-installation");
                CheckPath(owner);
                string ownerText;
                try { ownerText = ReadText(owner, 8192); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
                if (!ownerText.Equals("AudioPilot installed updater workspace 1\n" + application + "\n", StringComparison.OrdinalIgnoreCase)) return null;
            }
            string marker = Path.Combine(workspace, ".updater");
            CheckPath(marker);
            if (ReadText(marker, 64) != "AudioPilot updater workspace 1") throw Unavailable();
            string lockPath = Path.Combine(workspace, "lifecycle.lock");
            CheckPath(lockPath);
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { throw new UpdateMutationBlockedException("update-in-progress", "AudioPilot is installing or recovering an update. Wait for it to finish before changing settings or audio."); }

            string identity;
            if (portable)
            {
                var layout = Read<PortableLayoutDescriptor>(workspace, PortableLayout.FileName);
                if (layout.Schema != 1 || !layout.Managed) throw Unavailable();
                identity = layout.DiagnosticId;
                var deployment = Read<Deployment>(workspace, "deployment.json");
                if (deployment.Schema != 1 || deployment.PendingHealth) throw RecoveryRequired();
                if (deployment.Current != Path.GetFileName(directory))
                    throw new UpdateMutationBlockedException("update-generation-changed", "AudioPilot changed versions while this command was starting. Run it again using the installation's launcher.");
            }
            else
            {
                var binding = Read<Binding>(workspace, "installation.json");
                if (binding.Schema != 1 || binding.Kind != "msi" || !Path.TrimEndingDirectorySeparator(binding.Root).Equals(directory, StringComparison.OrdinalIgnoreCase)) throw Unavailable();
                identity = binding.Identity;
            }
            if (!Guid.TryParseExact(identity, "N", out var id) || id == Guid.Empty) throw Unavailable();
            string journal = Path.Combine(workspace, "recovery.json");
            CheckPath(journal + ".new");
            if (Exists(journal + ".new")) throw RecoveryRequired();
            Recovery? recovery;
            try { recovery = Read<Recovery>(workspace, "recovery.json"); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { recovery = null; }
            if (recovery != null)
            {
                if (recovery.Schema != 1 || recovery.DiagnosticId != identity || !recovery.Completed) throw RecoveryRequired();
            }
            return lease;
        }
        catch (UpdateMutationBlockedException) { lease?.Dispose(); throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { lease?.Dispose(); throw Unavailable(); }
    }

    private static T Read<T>(string workspace, string name)
    {
        string path = Path.Combine(workspace, name);
        CheckPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 8192) throw Unavailable();
        return JsonSerializer.Deserialize<T>(input, Json) ?? throw Unavailable();
    }
    private static string ReadText(string path, int limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > limit) throw Unavailable();
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }
    private static void CheckPath(string path)
    {
        if (UpdatePathSecurity.HasRedirectedAncestor(path)) throw Unavailable();
    }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    private static UpdateMutationBlockedException Unavailable() => new("update-state-unavailable", "AudioPilot update state could not be verified. Reopen AudioPilot to recover before changing settings or audio.");
    private static UpdateMutationBlockedException RecoveryRequired() => new("update-recovery-required", "An interrupted AudioPilot update needs recovery. Open AudioPilot before changing settings or audio.");
}
