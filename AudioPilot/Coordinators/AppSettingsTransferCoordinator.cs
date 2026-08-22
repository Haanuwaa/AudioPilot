using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioPilot.Models;

namespace AudioPilot.Coordinators;

internal static partial class AppSettingsTransferCoordinator
{
    [GeneratedRegex("([a-z])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex HotkeyLabelWords();

    internal static Task<Settings> PreviewImportAsync(SettingsService service, Settings? current, string path, CancellationToken cancellationToken)
    {
        Settings baseline = (current ?? service.LoadSettingsReadOnly()).Clone();
        return Task.Run(() => SettingsTransferService.ParseImportedSettings(
            SettingsTransferService.ReadImportText(path, service.ReadTextFileWithSettingsLock), baseline, replaceImport: true), cancellationToken);
    }

    internal static string DescribeImport(Settings current, Settings imported)
    {
        JsonElement before = JsonSerializer.SerializeToElement(current, SettingsJson.Options);
        JsonElement after = JsonSerializer.SerializeToElement(imported, SettingsJson.Options);
        if (JsonElement.DeepEquals(before, after)) return "No saved settings will change.";
        var changes = new List<string>();
        foreach (var (section, label) in new[] { ("Overlay", "Overlay settings"), ("Miscellaneous", "General settings"), ("AdvancedTuning", "Advanced options") })
            if (!JsonElement.DeepEquals(before.GetProperty(section), after.GetProperty(section))) changes.Add($"• {label} will change.");
        if (current.Theme != imported.Theme) changes.Add($"• Theme: {current.Theme} → {imported.Theme}");
        if (current.RunAtStartup != imported.RunAtStartup) changes.Add($"• Run at startup: {(current.RunAtStartup ? "On" : "Off")} → {(imported.RunAtStartup ? "On" : "Off")}");
        static string Label(string value)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return value.Length <= 64 ? value : value[..64] + "…";
        }
        static string Value(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() ? "On" : "Off" : Label(value.ToString());
        static string Order(IEnumerable<CycleDevice> devices)
        {
            CycleDevice[] entries = [.. devices];
            return entries.Length == 0 ? "None" : string.Join(", ", entries.Take(8).Select(static device => Label(device.Name))) + (entries.Length > 8 ? "…" : "");
        }
        JsonElement previousSwitching = before.GetProperty("DeviceSwitching");
        JsonElement nextSwitching = after.GetProperty("DeviceSwitching");
        foreach (string flow in new[] { "Output", "Input" })
        {
            JsonElement previous = previousSwitching.GetProperty(flow);
            JsonElement next = nextSwitching.GetProperty(flow);
            if (!JsonElement.DeepEquals(previous.GetProperty("CycleDevices"), next.GetProperty("CycleDevices")))
            {
                var oldDevices = flow == "Output" ? current.DeviceSwitching.Output.CycleDevices : current.DeviceSwitching.Input.CycleDevices;
                var newDevices = flow == "Output" ? imported.DeviceSwitching.Output.CycleDevices : imported.DeviceSwitching.Input.CycleDevices;
                string oldOrder = Order(oldDevices);
                string newOrder = Order(newDevices);
                changes.Add(oldOrder == newOrder ? $"• {flow} device references will change: {newOrder}" : $"• {flow} switch order: {oldOrder} → {newOrder}");
            }
            if (next.EnumerateObject().Any(property => property.Name != "CycleDevices" && !JsonElement.DeepEquals(previous.GetProperty(property.Name), property.Value)))
                changes.Add($"• {flow} switching options will change.");
        }
        foreach (var property in nextSwitching.EnumerateObject().Where(static property => property.Name is not ("Output" or "Input")))
            if (!JsonElement.DeepEquals(previousSwitching.GetProperty(property.Name), property.Value))
                changes.Add($"• {HotkeyLabelWords().Replace(property.Name, "$1 $2")}: {Value(previousSwitching.GetProperty(property.Name))} → {Value(property.Value)}");
        if (!JsonElement.DeepEquals(before.GetProperty("Routines"), after.GetProperty("Routines")))
        {
            changes.Add($"• Routines: {current.Routines.Items.Count} → {imported.Routines.Items.Count} saved");
            if (imported.Routines.Items.Count > 0)
                changes.Add($"  Imported: {string.Join(", ", imported.Routines.Items.Take(8).Select(static routine => Label(routine.Name)))}{(imported.Routines.Items.Count > 8 ? "…" : "")}");
        }
        var hotkeyChanges = new List<string>();
        void CompareHotkeys(JsonElement previous, JsonElement next, string prefix)
        {
            foreach (var property in next.EnumerateObject())
            {
                if (!previous.TryGetProperty(property.Name, out var oldValue)) continue;
                if (property.Value.ValueKind == JsonValueKind.Object) CompareHotkeys(oldValue, property.Value, prefix + property.Name + ".");
                else if (!JsonElement.DeepEquals(oldValue, property.Value))
                {
                    string name = prefix + property.Name == "Mute.PushToTalkEnabled" ? "Push-to-talk mode" : HotkeyLabelWords().Replace(prefix + property.Name, "$1 $2").Replace(".", " / ");
                    hotkeyChanges.Add($"• {name}: {Value(oldValue)} → {Value(property.Value)}");
                }
            }
        }
        CompareHotkeys(before.GetProperty("Hotkeys"), after.GetProperty("Hotkeys"), "");
        changes.AddRange(hotkeyChanges.Take(16));
        if (hotkeyChanges.Count > 16) changes.Add($"…and {hotkeyChanges.Count - 16} more hotkey changes.");
        if (imported.Hotkeys.Mute.PushToTalkEnabled && !current.Hotkeys.Mute.PushToTalkEnabled)
            changes.Add("Enabling push-to-talk mutes the default microphones until its key is held.");
        return changes.Count == 0 ? "Other saved settings will change." : "Changes to import:" + Environment.NewLine + string.Join(Environment.NewLine, changes);
    }

    internal static async Task ExportAsync(SettingsService settingsService, Settings? currentSettings, string filePath)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Settings settings = (currentSettings ?? settingsService.LoadSettings()).Clone();
        await Task.Run(() => SettingsTransferService.ExportSettings(settings, filePath));
    }

    internal static async Task<Settings> ImportAsync(SettingsService settingsService, Settings? currentSettings,
        SemaphoreSlim settingsWriteSemaphore, string filePath, Action<Settings>? persistSettings = null,
        Settings? preparedSettings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(settingsWriteSemaphore);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await settingsWriteSemaphore.WaitAsync(cancellationToken);
        try
        {
            Settings current = currentSettings ?? settingsService.LoadSettings();
            Settings imported = preparedSettings?.Clone() ?? await Task.Run(() =>
            {
                string importJson = SettingsTransferService.ReadImportText(filePath, settingsService.ReadTextFileWithSettingsLock);
                return SettingsTransferService.ParseImportedSettings(importJson, current, replaceImport: true);
            });
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                if (persistSettings != null) persistSettings(imported);
                else settingsService.SaveSettings(imported);
            }, cancellationToken);
            return imported;
        }
        finally { settingsWriteSemaphore.Release(); }
    }

    internal static string BuildDefaultExportFileName(DateTime timestamp) => $"AudioPilot-settings-{timestamp:yyyyMMdd-HHmmss}.zip";

    internal static string ResolveInitialDirectory(string settingsPath)
    {
        string settingsDirectory = Path.GetDirectoryName(settingsPath) ?? string.Empty;
        return Directory.Exists(settingsDirectory) ? settingsDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
