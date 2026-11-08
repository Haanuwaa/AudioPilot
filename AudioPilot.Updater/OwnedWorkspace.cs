using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AudioPilot.Updater;

/// <summary>An isolated updater workspace, never an existing application or data directory.</summary>
internal sealed partial class OwnedWorkspace
{
    private const string Marker = "AudioPilot updater workspace 1";
    private static readonly JsonSerializerOptions JsonOptions = new() { AllowDuplicateProperties = false };
    public string Root { get; }
    internal IUpdateLog? Diagnostics { get; set; }

    public OwnedWorkspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Root == Path.GetPathRoot(Root)) throw new InvalidOperationException("A drive root cannot be an updater workspace.");
        CheckAncestors(Root);
        string marker = Path.Combine(Root, ".updater");
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any())
        {
            if (!File.Exists(marker) || File.ReadAllText(marker) != Marker)
                throw new InvalidOperationException("Choose an empty updater workspace; existing app folders are not supported.");
        }
        else
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(marker, Marker);
        }
    }

    public string Resolve(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path escapes the updater workspace.");
        CheckAncestors(path);
        return path;
    }

    public FileStream Lock() => new(Resolve("update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static T ReadJson<T>(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8192) throw new InvalidDataException("Oversized updater state.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException("Empty updater state.");
    }

    public static void CheckAncestors(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Redirected paths are not supported by the updater.");
        }
    }

    public static void WriteJson<T>(string path, T value)
    {
        string temporary = path + ".new";
        CheckAncestors(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        Promote(temporary, path);
    }

    internal static void WriteText(string path, string value)
    {
        string temporary = path + ".new";
        CheckAncestors(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(value));
            stream.Flush(flushToDisk: true);
        }
        Promote(temporary, path);
    }

    internal void RecordInstallation(string application)
    {
        string path = Resolve(".updater-installation");
        string owner = "AudioPilot installed updater workspace 1\n" + System.IO.Path.GetFullPath(application) + "\n";
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 8192 || File.ReadAllText(path) != owner) throw new InvalidDataException("The workspace belongs to another installation.");
            return;
        }
        WriteText(path, owner);
    }

    public static void Promote(string temporary, string path)
    {
        CheckAncestors(temporary);
        CheckAncestors(path);
        if (!MoveFileEx(temporary, path, 1 | 8)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Durable state promotion failed.");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string destination, uint flags);
}
