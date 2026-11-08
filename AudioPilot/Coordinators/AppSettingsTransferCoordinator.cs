using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioPilot.Localization;
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

    internal static Func<LocalizationService, string> DescribeImport(Settings current, Settings imported)
    {
        JsonElement before = JsonSerializer.SerializeToElement(current, SettingsJson.Options);
        JsonElement after = JsonSerializer.SerializeToElement(imported, SettingsJson.Options);
        if (JsonElement.DeepEquals(before, after)) return static language => language["Message_NoSavedSettingsWillChange"];
        var changes = new List<(string Prefix, LocalizedText Text)>();
        void Add(LocalizedText text, string prefix = "• ") => changes.Add((prefix, text));
        foreach (var (section, key) in new[] { ("Overlay", "Message_OverlaySettingsWillChange"),
            ("Miscellaneous", "Message_GeneralSettingsWillChange"), ("AdvancedTuning", "Message_AdvancedOptionsWillChange") })
            if (!JsonElement.DeepEquals(before.GetProperty(section), after.GetProperty(section))) Add(new(key));
        if (current.Theme != imported.Theme)
            Add(new("Message_Theme01", new LocalizedText($"Enum_AppTheme_{current.Theme}"), new LocalizedText($"Enum_AppTheme_{imported.Theme}")));
        if (current.RunAtStartup != imported.RunAtStartup)
            Add(new("Message_RunAtStartup01", OnOff(current.RunAtStartup), OnOff(imported.RunAtStartup)));
        if (current.Language != imported.Language)
            Add(new("Import_LanguageChange", LanguageLabel(current.Language), LanguageLabel(imported.Language)));

        static LocalizedText OnOff(bool value) => new(value ? "Message_On" : "Enum_DeviceReferenceFileMode_Off");
        static LocalizedText LanguageLabel(string language)
        {
            string code = LocalizationService.NormalizeLanguage(language);
            return LocalizationService.Languages.First(option => option.Code == code).Label;
        }
        static string Label(string value)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return value.Length <= 64 ? value : value[..64] + "…";
        }
        static object Value(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? OnOff(value.GetBoolean()) : Label(value.ToString());
        static object Order(IReadOnlyList<CycleDevice> devices) => devices.Count == 0 ? new LocalizedText("Import_NoDevices")
            : string.Join(", ", devices.Take(8).Select(static device => Label(device.Name))) + (devices.Count > 8 ? "…" : "");
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
                object oldOrder = Order(oldDevices);
                object newOrder = Order(newDevices);
                Add(Equals(oldOrder, newOrder)
                    ? new($"Message_{flow}DeviceReferencesWillChange0", newOrder)
                    : new($"Message_{flow}SwitchOrder01", oldOrder, newOrder));
            }
            if (next.EnumerateObject().Any(property => property.Name != "CycleDevices" && !JsonElement.DeepEquals(previous.GetProperty(property.Name), property.Value)))
                Add(new($"Message_{flow}SwitchingOptionsWillChange"));
        }
        foreach (var property in nextSwitching.EnumerateObject().Where(static property => property.Name is not ("Output" or "Input")))
            if (!JsonElement.DeepEquals(previousSwitching.GetProperty(property.Name), property.Value))
                Add(new("Import_ValueChange", HotkeyLabelWords().Replace(property.Name, "$1 $2"),
                    Value(previousSwitching.GetProperty(property.Name)), Value(property.Value)));
        if (!JsonElement.DeepEquals(before.GetProperty("Routines"), after.GetProperty("Routines")))
        {
            Add(new("Message_Routines01Saved", current.Routines.Items.Count, imported.Routines.Items.Count));
            if (imported.Routines.Items.Count > 0)
                Add(new("Message_Imported0", string.Join(", ", imported.Routines.Items.Take(8).Select(static routine => Label(routine.Name)))
                    + (imported.Routines.Items.Count > 8 ? "…" : "")), "  ");
        }
        var hotkeyChanges = new List<LocalizedText>();
        static object HotkeyValue(JsonElement value) => value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())
            ? new LocalizedText("Import_NoShortcut") : Value(value);
        void CompareHotkeys(JsonElement previous, JsonElement next, string prefix)
        {
            foreach (var property in next.EnumerateObject())
            {
                if (!previous.TryGetProperty(property.Name, out var oldValue)) continue;
                if (property.Value.ValueKind == JsonValueKind.Object) CompareHotkeys(oldValue, property.Value, prefix + property.Name + ".");
                else if (!JsonElement.DeepEquals(oldValue, property.Value))
                {
                    object name = prefix + property.Name == "Mute.PushToTalkEnabled" ? new LocalizedText("Message_PushToTalkMode")
                        : HotkeyLabelWords().Replace(prefix + property.Name, "$1 $2").Replace(".", " / ");
                    hotkeyChanges.Add(new("Import_ValueChange", name, HotkeyValue(oldValue), HotkeyValue(property.Value)));
                }
            }
        }
        CompareHotkeys(before.GetProperty("Hotkeys"), after.GetProperty("Hotkeys"), "");
        foreach (var change in hotkeyChanges.Take(16)) Add(change);
        if (hotkeyChanges.Count > 16) Add(new("Message_And0MoreHotkeyChanges", hotkeyChanges.Count - 16), "");
        if (imported.Hotkeys.Mute.PushToTalkEnabled && !current.Hotkeys.Mute.PushToTalkEnabled)
            Add(new("Message_EnablingPushToTalkMutesTheDefault"), "");
        return changes.Count == 0 ? static language => language["Message_OtherSavedSettingsWillChange"]
            : language => language["Message_ChangesToImport"] + Environment.NewLine +
                string.Join(Environment.NewLine, changes.Select(change => change.Prefix + language.Format(change.Text)));
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
