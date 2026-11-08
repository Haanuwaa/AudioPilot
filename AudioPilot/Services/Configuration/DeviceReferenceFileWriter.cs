using System.IO;
using System.Security.Cryptography;
using System.Text;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration;

/// <summary>Writes stable device references and suppresses only successfully persisted, unchanged exports.</summary>
internal sealed class DeviceReferenceFileWriter(Action<string, string> writeFile)
{
    private readonly Lock _gate = new();

    public bool WriteIfChanged(string path, IEnumerable<CycleDevice> outputDevices, IEnumerable<CycleDevice> inputDevices, bool anonymizeIds)
    {
        lock (_gate)
        {
            var content = new StringBuilder();
            content.AppendLine("AudioPilot device reference");
            content.AppendLine("Generated from the current device inventory. Editing this file does not change settings.");
            content.AppendLine(anonymizeIds
                ? "IDs are hashed; device names remain visible. Hashed IDs cannot be used as CLI device selectors."
                : "Format: endpoint ID | device name. IDs and names may identify your devices.");
            content.AppendLine("Stable ID is included when Windows provides one. Use a redacted diagnostic bundle for support.");
            content.AppendLine();
            AppendDevices(content, "OUTPUT", outputDevices, anonymizeIds);
            content.AppendLine();
            AppendDevices(content, "INPUT", inputDevices, anonymizeIds);
            string text = content.ToString();
            if (MatchesFile(path, text))
            {
                return false;
            }

            writeFile(path, text);
            return true;
        }
    }

    private static bool MatchesFile(string path, string text)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Span<char> buffer = stackalloc char[1024];
            int offset = 0;
            int count;
            while ((count = reader.Read(buffer)) > 0)
            {
                if (count > text.Length - offset || !buffer[..count].SequenceEqual(text.AsSpan(offset, count))) return false;
                offset += count;
            }
            return offset == text.Length;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void AppendDevices(StringBuilder content, string direction, IEnumerable<CycleDevice> devices, bool anonymizeIds)
    {
        content.AppendLine($"[{direction} DEVICES]");
        content.AppendLine();
        foreach (CycleDevice device in devices
            .Where(static device => device != null && !string.IsNullOrWhiteSpace(device.Id))
            .OrderBy(static device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static device => device.Id, StringComparer.Ordinal)
            .ThenBy(static device => device.Name, StringComparer.Ordinal)
            .ThenBy(static device => device.StableId, StringComparer.Ordinal))
        {
            content.AppendLine($"{FormatId(device.Id, anonymizeIds)} | {SingleLine(device.Name)}");
            if (!string.IsNullOrEmpty(device.StableId))
            {
                content.AppendLine($"  Stable ID: {FormatId(device.StableId, anonymizeIds)}");
            }
        }
    }

    private static string FormatId(string id, bool anonymizeIds)
    {
        if (!anonymizeIds) return SingleLine(id);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return $"sha256:{Convert.ToHexString(hash.AsSpan(0, 10))}";
    }

    private static string SingleLine(string? value) =>
        value?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
}
