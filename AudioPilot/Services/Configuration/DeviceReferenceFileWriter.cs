using System.IO;
using System.Security.Cryptography;
using System.Text;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration;

/// <summary>Writes stable device references and suppresses only successfully persisted, unchanged exports.</summary>
internal sealed class DeviceReferenceFileWriter(Action<string, string> writeFile)
{
    private readonly Lock _gate = new();
    private string? _lastPath;
    private string? _lastContent;

    public bool WriteIfChanged(string path, IEnumerable<CycleDevice> outputDevices, IEnumerable<CycleDevice> inputDevices, bool anonymizeIds)
    {
        lock (_gate)
        {
            var content = new StringBuilder();
            AppendDevices(content, "OUTPUT", outputDevices, anonymizeIds);
            content.AppendLine();
            AppendDevices(content, "INPUT", inputDevices, anonymizeIds);
            string text = content.ToString();
            if (string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(text, _lastContent, StringComparison.Ordinal) && File.Exists(path))
            {
                return false;
            }

            writeFile(path, text);
            _lastPath = path;
            _lastContent = text;
            return true;
        }
    }

    private static void AppendDevices(StringBuilder content, string direction, IEnumerable<CycleDevice> devices, bool anonymizeIds)
    {
        content.AppendLine($"[{direction} DEVICES]");
        content.AppendLine();
        foreach (CycleDevice device in devices
            .Where(static device => device != null && !string.IsNullOrWhiteSpace(device.Id))
            .OrderBy(static device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static device => device.Id, StringComparer.Ordinal)
            .ThenBy(static device => device.Name, StringComparer.Ordinal))
        {
            string id = device.Id;
            if (anonymizeIds)
            {
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
                id = $"sha256:{Convert.ToHexString(hash.AsSpan(0, 10))}";
            }

            content.AppendLine($"{SingleLine(id)} | {SingleLine(device.Name)}");
        }
    }

    private static string SingleLine(string? value) =>
        value?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
}
