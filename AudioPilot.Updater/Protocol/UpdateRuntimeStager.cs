using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
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
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .Concat(EnumerateSatelliteResources(payloadDirectory)).Order(StringComparer.OrdinalIgnoreCase)];
        foreach (string required in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            if (!files.Any(path => Path.GetFileName(path) == required)) throw new FileNotFoundException("This pilot package is incomplete: " + required);
        byte[] hashBuffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var sourceHashes = new byte[files.Length][];
            try
            {
                Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                    () => ArrayPool<byte>.Shared.Rent(65536),
                    (index, _, buffer) =>
                    {
                        token.ThrowIfCancellationRequested(); CheckPath(files[index]);
                        sourceHashes[index] = HashFile(files[index], buffer, token);
                        return buffer;
                    }, buffer => ArrayPool<byte>.Shared.Return(buffer));
            }
            catch (AggregateException error)
            {
                token.ThrowIfCancellationRequested();
                ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
                throw;
            }
            for (int index = 0; index < files.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetRelativePath(payloadDirectory, files[index]);
                hashes.Add(name, sourceHashes[index]);
                fingerprint.AppendData(Encoding.UTF8.GetBytes(name)); fingerprint.AppendData(sourceHashes[index]);
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
                bool reused = false;
                if (!created)
                {
                    using var readLease = File.Open(lease, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                    if (IsReady(runtime))
                    {
                        reused = true;
                        foreach (string file in files)
                        {
                            token.ThrowIfCancellationRequested();
                            string relative = Path.GetRelativePath(payloadDirectory, file);
                            string destination = Path.Combine(runtime, relative); CheckPath(destination);
                            if (!File.Exists(destination) || !CryptographicOperations.FixedTimeEquals(hashes[relative], HashFile(destination, hashBuffer, token)))
                            { reused = false; break; }
                            copied?.Invoke();
                        }
                        token.ThrowIfCancellationRequested();
                    }
                }
                if (!reused)
                {
                    using var stagingLease = File.Open(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    WriteMarker(Path.Combine(runtime, ".pilot-runtime"), "AudioPilot pilot runtime 1");
                    foreach (string file in files)
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = Path.GetRelativePath(payloadDirectory, file);
                        string destination = Path.Combine(runtime, relative); CheckPath(destination);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        byte[] expected = hashes[relative];
                        bool matches = File.Exists(destination) && CryptographicOperations.FixedTimeEquals(expected, HashFile(destination, hashBuffer, token));
                        if (!matches)
                        {
                            CopyFile(file, destination, hashBuffer, token);
                            if (!CryptographicOperations.FixedTimeEquals(expected, HashFile(destination, hashBuffer, token))) throw new InvalidDataException("A staged pilot runtime file changed.");
                        }
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
        finally { ArrayPool<byte>.Shared.Return(hashBuffer); }
    }

    private static void CopyFile(string source, string destination, byte[] buffer, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = input.Read(buffer);
            if (count == 0) return;
            output.Write(buffer, 0, count);
        }
    }

    private static byte[] HashFile(string path, byte[] buffer, CancellationToken token)
    {
        // The shared hash buffer replaces FileStream's per-file buffering.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = stream.Read(buffer);
            if (count == 0) return hash.GetHashAndReset();
            hash.AppendData(buffer, 0, count);
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
                if (!retainedPrevious && IsReady(directory))
                { retainedPrevious = true; continue; }
                DeleteRuntime(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private static bool IsReady(string directory)
    {
        string ready = Path.Combine(directory, "runtime.ready"); CheckPath(ready);
        return File.Exists(ready) && new FileInfo(ready).Length < 64 && File.ReadAllText(ready) == "AudioPilot runtime ready 1";
    }

    private static bool IsOwned(string directory)
    {
        CheckPath(directory);
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) return false;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            CheckPath(child);
            if (!IsCultureDirectory(child) || Directory.EnumerateDirectories(child).Any()
                || Directory.EnumerateFiles(child).Any(file => !file.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))) return false;
        }
        string marker = Path.Combine(directory, ".pilot-runtime"); CheckPath(marker);
        return File.Exists(marker) && new FileInfo(marker).Length <= 64 && File.ReadAllText(marker) == "AudioPilot pilot runtime 1";
    }

    private static void DeleteRuntime(string directory)
    {
        CheckPath(directory);
        foreach (string file in Directory.EnumerateFiles(directory)) CheckPath(file);
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            CheckPath(child);
            if (!IsCultureDirectory(child) || Directory.EnumerateDirectories(child).Any()) throw new InvalidDataException("Unexpected runtime directory.");
            foreach (string file in Directory.EnumerateFiles(child)) CheckPath(file);
        }
        using var lease = File.Open(Path.Combine(directory, "runtime.inuse"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
        Directory.Delete(directory, recursive: true);
    }

    private static void WriteMarker(string path, string value)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true);
    }

    private static IEnumerable<string> EnumerateSatelliteResources(string payloadDirectory)
    {
        foreach (string directory in Directory.EnumerateDirectories(payloadDirectory))
        {
            if (!IsCultureDirectory(directory)) continue;
            CheckPath(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "*.resources.dll")) yield return file;
        }
    }

    private static bool IsCultureDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        if (name.Length < 2 || !char.IsAsciiLetter(name[0]) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        try { return string.Equals(CultureInfo.GetCultureInfo(name).Name, name, StringComparison.OrdinalIgnoreCase); }
        catch (CultureNotFoundException) { return false; }
    }

    private static void CheckPath(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A pilot runtime path is redirected.");
        }
    }
}
