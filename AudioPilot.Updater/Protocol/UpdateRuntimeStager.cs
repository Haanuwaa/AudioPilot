using System.Security.Cryptography;
using System.Text;

namespace AudioPilot.UpdateProtocol;

/// <summary>Copies and verifies the complete packaged runtime outside a replaceable installation.</summary>
public static class UpdateRuntimeStager
{
    public static string Stage(string payloadDirectory, CancellationToken token)
    {
        UpdateClientPolicy.RequireConfigured();
        return StageCore(payloadDirectory, UpdateClientPolicy.RuntimeRoot, token);
    }

    internal static string StageCore(string payloadDirectory, string runtimeRoot, CancellationToken token, Action? copied = null)
    {
        payloadDirectory = Path.GetFullPath(payloadDirectory);
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        string[] files = [.. Directory.EnumerateFiles(payloadDirectory).Where(path =>
            Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)];
        foreach (string required in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            if (!files.Any(path => Path.GetFileName(path) == required)) throw new FileNotFoundException("This pilot package is incomplete: " + required);
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested(); CheckPath(file);
            using var input = File.OpenRead(file);
            byte[] hash = SHA256.HashData(input);
            string name = Path.GetFileName(file);
            hashes.Add(name, hash); fingerprint.AppendData(Encoding.UTF8.GetBytes(name)); fingerprint.AppendData(hash);
        }
        CheckPath(runtimeRoot);
        Directory.CreateDirectory(runtimeRoot);
        using var pool = File.Open(Path.Combine(runtimeRoot, "runtime-pool.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string runtime = Path.Combine(runtimeRoot, new Guid(fingerprint.GetHashAndReset().AsSpan(0, 16)).ToString("N"));
        CheckPath(runtime);
        bool created = !Directory.Exists(runtime);
        if (!created && !IsOwned(runtime)) throw new InvalidDataException("The runtime directory is not updater-owned.");
        Directory.CreateDirectory(runtime);
        string lease = Path.Combine(runtime, "runtime.inuse"); CheckPath(lease);
        try
        {
            using (var stagingLease = File.Open(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                WriteMarker(Path.Combine(runtime, ".pilot-runtime"), "AudioPilot pilot runtime 1");
                foreach (string file in files)
                {
                    token.ThrowIfCancellationRequested();
                    string destination = Path.Combine(runtime, Path.GetFileName(file)); CheckPath(destination);
                    bool matches = false;
                    if (File.Exists(destination))
                    {
                        using var existing = File.OpenRead(destination);
                        matches = CryptographicOperations.FixedTimeEquals(hashes[Path.GetFileName(file)], SHA256.HashData(existing));
                    }
                    if (!matches) File.Copy(file, destination, overwrite: File.Exists(destination));
                    using var copy = File.OpenRead(destination);
                    if (!CryptographicOperations.FixedTimeEquals(hashes[Path.GetFileName(file)], SHA256.HashData(copy))) throw new InvalidDataException("A staged pilot runtime file changed.");
                    copied?.Invoke();
                }
                token.ThrowIfCancellationRequested();
                WriteMarker(Path.Combine(runtime, "runtime.ready"), "AudioPilot runtime ready 1");
            }
            try { Trim(runtimeRoot, runtime); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return Path.Combine(runtime, "AudioPilot.Updater.exe");
        }
        catch
        {
            if (created)
            {
                try
                {
                    if (IsOwned(runtime)) DeleteRuntime(runtime);
                    else if (Directory.EnumerateFileSystemEntries(runtime).All(path => path == lease))
                    { File.Delete(lease); Directory.Delete(runtime); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            throw;
        }
    }

    private static void Trim(string root, string active)
    {
        bool retainedPrevious = false;
        foreach (string directory in Directory.EnumerateDirectories(root).Where(path => path != active)
            .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            try
            {
                if (!IsOwned(directory)) continue;
                string ready = Path.Combine(directory, "runtime.ready"); CheckPath(ready);
                if (!retainedPrevious && File.Exists(ready) && new FileInfo(ready).Length < 64 && File.ReadAllText(ready) == "AudioPilot runtime ready 1")
                { retainedPrevious = true; continue; }
                DeleteRuntime(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private static bool IsOwned(string directory)
    {
        CheckPath(directory);
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || Directory.EnumerateDirectories(directory).Any()) return false;
        string marker = Path.Combine(directory, ".pilot-runtime"); CheckPath(marker);
        return File.Exists(marker) && new FileInfo(marker).Length <= 64 && File.ReadAllText(marker) == "AudioPilot pilot runtime 1";
    }

    private static void DeleteRuntime(string directory)
    {
        CheckPath(directory);
        foreach (string file in Directory.EnumerateFiles(directory)) CheckPath(file);
        using var lease = File.Open(Path.Combine(directory, "runtime.inuse"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
        Directory.Delete(directory, recursive: true);
    }

    private static void WriteMarker(string path, string value)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true);
    }

    private static void CheckPath(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A pilot runtime path is redirected.");
    }
}
