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

    private const int IoBufferSize = 131072;

    internal static string StageCore(string payloadDirectory, string runtimeRoot, CancellationToken token, Action? copied = null)
    {
        payloadDirectory = Path.GetFullPath(payloadDirectory);
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        CheckPath(payloadDirectory);
        string[] files = [.. Directory.EnumerateFiles(payloadDirectory).Where(path =>
            Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .Concat(EnumerateSatelliteResources(payloadDirectory)).Order(StringComparer.OrdinalIgnoreCase)];
        foreach (string required in new[] { "AudioPilot.Updater.exe", "AudioPilot.Updater.dll", "AudioPilot.Updater.Core.dll", "AudioPilot.TufBridge.exe" })
            if (!files.Any(path => Path.GetFileName(path) == required)) throw new FileNotFoundException("This pilot package is incomplete: " + required);
        int maxDegree = copied == null ? Math.Clamp(Environment.ProcessorCount, 2, 8) : 2;
        var hashes = new Dictionary<string, byte[]>(files.Length, StringComparer.OrdinalIgnoreCase);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sourceHashes = new byte[files.Length][];
        try
        {
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = token },
                () => ArrayPool<byte>.Shared.Rent(IoBufferSize),
                (index, _, buffer) =>
                {
                    token.ThrowIfCancellationRequested();
                    CheckFileNotReparsePoint(files[index]);
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
        var relativePaths = new string[files.Length];
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetRelativePath(payloadDirectory, files[index]);
            relativePaths[index] = name;
            hashes.Add(name, sourceHashes[index]);
            fingerprint.AppendData(Encoding.UTF8.GetBytes(name)); fingerprint.AppendData(sourceHashes[index]);
        }
        var expectedFiles = new HashSet<string>(hashes.Keys, StringComparer.Ordinal) { ".pilot-runtime", "runtime.inuse", "runtime.ready" };
        var expectedDirectories = new HashSet<string>(hashes.Keys.Select(Path.GetDirectoryName).OfType<string>(), StringComparer.Ordinal);
        CheckPath(runtimeRoot);
        Directory.CreateDirectory(runtimeRoot);
        using var pool = OpenPoolLock(Path.Combine(runtimeRoot, "runtime-pool.lock"), token);
        string runtime = Path.Combine(runtimeRoot, new Guid(fingerprint.GetHashAndReset().AsSpan(0, 16)).ToString("N"));
        CheckPath(runtime);
        bool created = !Directory.Exists(runtime);
        if (!created && !IsOwned(runtime)) throw new InvalidDataException("The runtime directory is not updater-owned.");
        Directory.CreateDirectory(runtime);
        string lease = Path.Combine(runtime, "runtime.inuse"); CheckFileNotReparsePointIfExists(lease);
        try
        {
            bool reused = false;
            if (!created)
            {
                using var readLease = File.Open(lease, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                if (IsReady(runtime))
                {
                    reused = VerifyCachedFiles(runtime, relativePaths, sourceHashes, expectedDirectories, maxDegree, copied, token);
                    token.ThrowIfCancellationRequested();
                    if (reused)
                        reused = ScanInventory(runtime, runtime, expectedFiles, expectedDirectories, repair: false, token) == expectedFiles.Count;
                }
            }
            if (!reused)
            {
                using var stagingLease = File.Open(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                ScanInventory(runtime, runtime, expectedFiles, expectedDirectories, repair: true, token);
                File.Delete(Path.Combine(runtime, "runtime.ready"));
                WriteMarker(Path.Combine(runtime, ".pilot-runtime"), "AudioPilot pilot runtime 1");
                CopyRuntimeFiles(files, relativePaths, sourceHashes, expectedDirectories, runtime, maxDegree, copied, token);
                token.ThrowIfCancellationRequested();
                expectedFiles.Remove("runtime.ready");
                if (ScanInventory(runtime, runtime, expectedFiles, expectedDirectories, repair: false, token) != expectedFiles.Count)
                    throw new InvalidDataException("The staged updater runtime inventory does not match the package.");
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

    private static int? ScanInventory(string root, string directory, HashSet<string> expectedFiles,
        HashSet<string> expectedDirectories, bool repair, CancellationToken token)
    {
        int fileCount = 0;
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            RejectRedirectedEntry(entry);
            string relative = Path.GetRelativePath(root, entry.FullName);
            if (entry is DirectoryInfo child && expectedDirectories.Contains(relative))
            {
                int? childCount = ScanInventory(root, child.FullName, expectedFiles, expectedDirectories, repair, token);
                if (childCount == null) return null;
                fileCount += childCount.Value;
            }
            else if (entry is FileInfo && expectedFiles.Contains(relative))
            {
                fileCount++;
            }
            else
            {
                if (!repair) return null;
                if (entry is DirectoryInfo unexpected) DeleteUnexpectedDirectory(unexpected, token);
                else entry.Delete();
            }
        }
        return fileCount;
    }

    private static void DeleteUnexpectedDirectory(DirectoryInfo directory, CancellationToken token)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        while (pending.TryPop(out DirectoryInfo? current))
        {
            RejectRedirectedEntry(current);
            foreach (FileSystemInfo entry in current.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                RejectRedirectedEntry(entry);
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
        token.ThrowIfCancellationRequested();
        // The cache is exclusively leased. A directory is deleted only after its full tree is checked.
        directory.Delete(recursive: true);
    }

    private static void RejectRedirectedEntry(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pilot runtime path is redirected.");
    }

    private static FileStream OpenPoolLock(string lockPath, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + 15_000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CheckFileNotReparsePointIfExists(lockPath);
            try
            {
                return File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && Environment.TickCount64 < deadline)
            {
                if (token.WaitHandle.WaitOne(50))
                    token.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool VerifyCachedFiles(string runtime, string[] relativePaths, byte[][] sourceHashes,
        HashSet<string> expectedDirectories, int maxDegree, Action? copied, CancellationToken token)
    {
        foreach (string relativeDir in expectedDirectories)
        {
            if (relativeDir.Length == 0) continue;
            string subdir = Path.Combine(runtime, relativeDir);
            CheckPath(subdir);
            if (!Directory.Exists(subdir)) return false;
        }

        if (copied != null)
        {
            byte[] sequentialBuffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
            try
            {
                for (int index = 0; index < relativePaths.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string destination = Path.Combine(runtime, relativePaths[index]);
                    CheckFileNotReparsePointIfExists(destination);
                    if (!File.Exists(destination)) return false;
                    if (!CryptographicOperations.FixedTimeEquals(sourceHashes[index], HashFile(destination, sequentialBuffer, token)))
                        return false;
                    copied();
                }
                return true;
            }
            finally { ArrayPool<byte>.Shared.Return(sequentialBuffer); }
        }

        int mismatch = 0;
        try
        {
            Parallel.For(0, relativePaths.Length, new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = token },
                () => ArrayPool<byte>.Shared.Rent(IoBufferSize),
                (index, _, buffer) =>
                {
                    if (Volatile.Read(ref mismatch) != 0) return buffer;
                    token.ThrowIfCancellationRequested();
                    string destination = Path.Combine(runtime, relativePaths[index]);
                    CheckFileNotReparsePointIfExists(destination);
                    if (!File.Exists(destination))
                    {
                        Volatile.Write(ref mismatch, 1);
                        return buffer;
                    }
                    if (!CryptographicOperations.FixedTimeEquals(sourceHashes[index], HashFile(destination, buffer, token)))
                        Volatile.Write(ref mismatch, 1);
                    return buffer;
                }, buffer => ArrayPool<byte>.Shared.Return(buffer));
        }
        catch (AggregateException error)
        {
            token.ThrowIfCancellationRequested();
            ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
        return Volatile.Read(ref mismatch) == 0;
    }

    private static void CopyRuntimeFiles(string[] files, string[] relativePaths, byte[][] sourceHashes,
        HashSet<string> expectedDirectories, string runtime, int maxDegree, Action? copied, CancellationToken token)
    {
        foreach (string relativeDir in expectedDirectories)
        {
            if (relativeDir.Length == 0) continue;
            string subdir = Path.Combine(runtime, relativeDir);
            CheckPath(subdir);
            Directory.CreateDirectory(subdir);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = token };
        // Settle each bounded batch before reporting completion or beginning cleanup.
        int batchSize = copied != null ? 2 : files.Length;
        for (int first = 0; first < files.Length; first += batchSize)
        {
            int end = Math.Min(first + batchSize, files.Length);
            try
            {
                Parallel.For(first, end, options,
                    () => ArrayPool<byte>.Shared.Rent(IoBufferSize),
                    (index, _, buffer) =>
                    {
                        token.ThrowIfCancellationRequested();
                        string destination = Path.Combine(runtime, relativePaths[index]);
                        CheckFileNotReparsePointIfExists(destination);
                        byte[] expected = sourceHashes[index];
                        bool matches = File.Exists(destination) && CryptographicOperations.FixedTimeEquals(expected, HashFile(destination, buffer, token));
                        if (!matches)
                        {
                            CopyFile(files[index], destination, buffer, token);
                            if (!CryptographicOperations.FixedTimeEquals(expected, HashFile(destination, buffer, token))) throw new InvalidDataException("A staged pilot runtime file changed.");
                        }
                        return buffer;
                    }, buffer => ArrayPool<byte>.Shared.Return(buffer));
            }
            catch (AggregateException error)
            {
                token.ThrowIfCancellationRequested();
                ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
                throw;
            }
            if (copied != null)
            {
                for (int index = first; index < end; index++)
                {
                    token.ThrowIfCancellationRequested();
                    copied();
                }
            }
        }
    }

    private static void CopyFile(string source, string destination, byte[] buffer, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        string temporary = destination + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int count = input.Read(buffer);
                    if (count == 0) break;
                    output.Write(buffer, 0, count);
                }
            }
            token.ThrowIfCancellationRequested();
            CheckFileNotReparsePointIfExists(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
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
        File.Delete(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
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
        if (UpdatePathSecurity.HasRedirectedAncestor(path))
            throw new InvalidDataException("A pilot runtime path is redirected.");
    }

    private static void CheckFileNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pilot runtime path is redirected.");
    }

    private static void CheckFileNotReparsePointIfExists(string path)
    {
        if (UpdatePathSecurity.IsReparsePoint(path))
            throw new InvalidDataException("A pilot runtime path is redirected.");
    }
}
