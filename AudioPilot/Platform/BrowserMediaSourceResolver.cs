using System.IO;
using System.Security;
using Microsoft.Win32;

namespace AudioPilot.Platform;

/// <summary>
/// Identifies media sources using browser families, Windows browser registrations, and running browser engines.
/// Cached decisions expire so new installations and portable browser sessions can be discovered without restarting AudioPilot.
/// </summary>
internal sealed class BrowserMediaSourceResolver
{
    internal const string PrivateBrowsingSuffix = ";PrivateBrowsingAUMID";
    private const string BrowserClientsKey = @"Software\Clients\StartMenuInternet";
    private const int MaxCachedSources = 128;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (bool IsBrowser, long ExpiresAt)> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _discoveredSources = new(StringComparer.OrdinalIgnoreCase);
    private long _discoveryExpiresAt;
    private bool _hasDiscoverySnapshot;
    private readonly Func<IEnumerable<string>> _discoverSourceIds;
    private readonly Func<long> _tickCount;
    private static readonly string[] BrowserFamilies =
    [
        "Brave", "Chromium", "Chrome", "MSEdge", "Edge", "MicrosoftEdge", "Microsoft.MicrosoftEdge",
        "Firefox", "Mozilla.Firefox", "Opera", "Vivaldi", "Arc",
    ];

    internal static BrowserMediaSourceResolver Installed { get; } = new();

    internal BrowserMediaSourceResolver(Func<IEnumerable<string>>? discoverSourceIds = null, Func<long>? tickCount = null)
    {
        _discoverSourceIds = discoverSourceIds ?? ReadBrowserSourceIds;
        _tickCount = tickCount ?? (static () => Environment.TickCount64);
    }

    internal bool IsBrowserSource(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId) || sourceAppUserModelId.Length > 512)
        {
            return false;
        }

        string source = sourceAppUserModelId.Trim();
        foreach (string family in BrowserFamilies)
        {
            if (MatchesFamily(source, family)) { return true; }
        }

        lock (_lock)
        {
            long now = _tickCount();
            if (_decisions.TryGetValue(source, out var cached) && now < cached.ExpiresAt)
            {
                return cached.IsBrowser;
            }

            RefreshDiscoveryIfExpired(now);
            bool isBrowser = _discoveredSources.Contains(source);

            if (_decisions.Count >= MaxCachedSources)
            {
                _decisions.Clear();
            }
            _decisions[source] = (isBrowser, isBrowser ? _tickCount() + 60_000 : _discoveryExpiresAt);
            return isBrowser;
        }
    }

    internal int CachedSourceCount { get { lock (_lock) { return _decisions.Count; } } }

    /// <summary>
    /// Shares one short-lived registry/window scan across unrelated source IDs. Partial discoveries survive
    /// an inaccessible registry view or a browser exiting during enumeration; later scans can retry.
    /// </summary>
    private void RefreshDiscoveryIfExpired(long now)
    {
        if (_hasDiscoverySnapshot && now < _discoveryExpiresAt) { return; }
        _discoveredSources.Clear();
        try
        {
            foreach (string id in _discoverSourceIds())
            {
                if (!string.IsNullOrWhiteSpace(id) && id.Length <= 512) { _discoveredSources.Add(id.Trim()); }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
        }
        _hasDiscoverySnapshot = true;
        _discoveryExpiresAt = _tickCount() + 5_000;
    }

    private static bool MatchesFamily(string source, string family) =>
        source.Equals(family, StringComparison.OrdinalIgnoreCase)
        || (source.StartsWith(family, StringComparison.OrdinalIgnoreCase) && source.Length > family.Length
            && source[family.Length] is '.' or '_' or '-');

    /// <summary>Extracts the installation hash from a Gecko browser's registered client name.</summary>
    internal static string? GetInstallationId(string browserClientName)
    {
        int separator = browserClientName.LastIndexOf('-');
        if (separator <= 0 || !IsInstallationId(browserClientName.AsSpan(separator + 1)))
        {
            return null;
        }

        return browserClientName[(separator + 1)..];
    }

    private static bool IsInstallationId(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > 16)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static HashSet<string> GetRegistrationSourceIds(string clientName, string? openCommand, IEnumerable<string> appUserModelIds)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { clientName };
        if (GetInstallationId(clientName) is string installationId)
        {
            sources.Add(installationId);
            sources.Add(installationId + PrivateBrowsingSuffix);
        }
        sources.UnionWith(appUserModelIds.Where(static id => !string.IsNullOrWhiteSpace(id)));
        string? executable = GetCommandExecutable(openCommand);
        if (executable != null)
        {
            string name = Path.GetFileNameWithoutExtension(executable);
            if (name.ToUpperInvariant() is not ("CMD" or "POWERSHELL" or "PWSH" or "RUNDLL32" or "EXPLORER" or "WSCRIPT" or "CSCRIPT"))
            {
                sources.Add(executable);
                sources.Add(Path.GetFileName(executable));
                sources.Add(name);
            }
        }

        return sources;
    }

    internal static string? GetCommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) { return null; }
        command = command.Trim();
        if (command[0] == '"')
        {
            int closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 && command.AsSpan(1, closingQuote - 1).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? command[1..closingQuote] : null;
        }
        int extension = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (extension >= 0)
        {
            int end = extension + 4;
            if (end == command.Length || char.IsWhiteSpace(command[end])) { return command[..end]; }
            extension = command.IndexOf(".exe", end, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    private static IEnumerable<string> ReadBrowserSourceIds()
    {
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RegistryView[] views = Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in views)
            {
                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? clients = root.OpenSubKey(BrowserClientsKey);
                    foreach (string clientName in (clients?.GetSubKeyNames() ?? []).Take(256))
                    {
                        try
                        {
                            using RegistryKey? client = clients!.OpenSubKey(clientName);
                            using RegistryKey? command = client?.OpenSubKey(@"shell\open\command");
                            sourceIds.UnionWith(GetRegistrationSourceIds(clientName, command?.GetValue(null) as string, []));
                            using RegistryKey? associations = client?.OpenSubKey(@"Capabilities\URLAssociations");
                            foreach (string scheme in new[] { "http", "https" })
                            {
                                if (associations?.GetValue(scheme) is not string progId || progId.Length is 0 or > 255
                                    || progId.IndexOfAny(['\\', '/', '\0']) >= 0) { continue; }
                                foreach (RegistryHive classHive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                                {
                                    try
                                    {
                                        using RegistryKey classRoot = RegistryKey.OpenBaseKey(classHive, view);
                                        using RegistryKey? application = classRoot.OpenSubKey($@"Software\Classes\{progId}\Application");
                                        if (application?.GetValue("AppUserModelID") is string appId && !string.IsNullOrWhiteSpace(appId))
                                        {
                                            sourceIds.Add(appId);
                                        }
                                    }
                                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                                    {
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }

        foreach (string sourceId in sourceIds) { yield return sourceId; }
        foreach (string sourceId in AudioDeviceHelper.GetRunningBrowserMediaSourceIds()) { yield return sourceId; }
    }
}
