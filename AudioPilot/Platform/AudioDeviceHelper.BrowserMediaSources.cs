using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace AudioPilot.Platform;

public static partial class AudioDeviceHelper
{
    private const uint BrowserProcessQueryLimitedInformation = 0x1000;
    private const int BrowserPathInsufficientBuffer = 122;
    private static readonly string[] BrowserEngineFiles = ["xul.dll", "chrome.dll", "msedge.dll"];

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial SafeProcessHandle OpenBrowserProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryBrowserProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] path, ref int length);

    /// <summary>
    /// Discovers portable Gecko and Chromium browsers from window identity and engine files, without reading tab titles.
    /// Chromium window classes alone are insufficient because Electron and CEF applications also use them.
    /// </summary>
    internal static HashSet<string> GetRunningBrowserMediaSourceIds()
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processPaths = new Dictionary<uint, string?>();
        using var identity = new BrowserWindowIdentity();
        foreach (var (window, processId) in BuildWindowPidMap())
        {
            string? className = TryGetWindowClassName(window);
            if (!IsBrowserWindowClass(className)) { continue; }
            try
            {
                if (!processPaths.TryGetValue(processId, out string? path))
                {
                    path = ReadBrowserProcessPath(processId);
                    string? directory = path == null ? null : Path.GetDirectoryName(path);
                    if (directory == null || !HasBrowserEngine(directory)) { path = null; }
                    processPaths[processId] = path;
                }
                if (path == null) { continue; }

                sources.Add(path);
                sources.Add(Path.GetFileName(path));
                sources.Add(Path.GetFileNameWithoutExtension(path));
                string? sourceId = GetWindowAppUserModelId(window);
                if (string.IsNullOrWhiteSpace(sourceId)) { sourceId = identity.GetAppId(window, processId); }
                if (!string.IsNullOrWhiteSpace(sourceId))
                {
                    sources.Add(sourceId);
                    if (className == "MozillaWindowClass" && !sourceId.EndsWith(BrowserMediaSourceResolver.PrivateBrowsingSuffix, StringComparison.Ordinal))
                    {
                        sources.Add(sourceId + BrowserMediaSourceResolver.PrivateBrowsingSuffix);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or COMException)
            {
                processPaths[processId] = null;
            }
        }
        return sources;
    }

    internal static bool IsBrowserWindowClass(string? className) =>
        string.Equals(className, "MozillaWindowClass", StringComparison.Ordinal)
        || className?.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) == true;

    internal static bool HasBrowserEngine(string directory, Func<string, bool>? fileExists = null, Func<string, IEnumerable<string>>? enumerateDirectories = null)
    {
        fileExists ??= File.Exists;
        enumerateDirectories ??= Directory.EnumerateDirectories;
        if (fileExists(Path.Combine(directory, "msedgewebview2.exe"))) { return false; }
        foreach (string engine in BrowserEngineFiles)
        {
            if (fileExists(Path.Combine(directory, engine))) { return true; }
        }
        foreach (string versionDirectory in enumerateDirectories(directory).Take(64))
        {
            if (!Version.TryParse(Path.GetFileName(versionDirectory), out _)) { continue; }
            foreach (string engine in BrowserEngineFiles)
            {
                if (fileExists(Path.Combine(versionDirectory, engine))) { return true; }
            }
        }
        return false;
    }

    /// <summary>Reads image paths with limited process access, including when a 32-bit host inspects a 64-bit browser.</summary>
    private static string? ReadBrowserProcessPath(uint processId)
    {
        using SafeProcessHandle process = OpenBrowserProcess(BrowserProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) { return null; }
        char[] path = new char[1024];
        int length = path.Length;
        if (QueryBrowserProcessImageName(process, 0, path, ref length)) { return new string(path, 0, length); }
        if (Marshal.GetLastPInvokeError() != BrowserPathInsufficientBuffer) { return null; }
        path = new char[32768];
        length = path.Length;
        return QueryBrowserProcessImageName(process, 0, path, ref length) ? new string(path, 0, length) : null;
    }
}
