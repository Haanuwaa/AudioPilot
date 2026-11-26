using System.Text.Json;
using System.Text.RegularExpressions;
using AudioPilot.Localization;
using AudioPilot.Models;

namespace AudioPilot.Coordinators;

internal static partial class AppSettingsTransferCoordinator
{
    [GeneratedRegex("([a-z])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex HotkeyLabelWords();

    internal static Settings PrepareImport(string json, Settings? current, bool replaceImport)
    {
        Settings imported = SettingsTransferService.ParseImportedSettings(json, current, replaceImport);
        var validation = SettingsCommitValidator.Validate(imported);
        if (validation.HasBlockingIssues)
            throw SettingsFailure.WithPresentation(
                new InvalidDataException(DialogText.Messages.BuildInvalidSettingsBeforeSaving(validation.BlockingMessages)),
                language => language["Message_PleaseFixTheseSettingsBeforeSaving"] + "\n" + validation.MessagePresentation!(language));
        SettingsTransferService.EnsureTransferTextSizeAllowed(SettingsTransferService.SerializeSettings(imported));
        return imported;
    }

    internal static Func<LocalizationService, string> DescribeImport(Settings current, Settings imported) =>
        DescribeImport(current, imported, out _);

    internal static Func<LocalizationService, string> DescribeImport(Settings current, Settings imported, out string[] changedSections,
        bool includeDetails = true)
    {
        JsonElement before = JsonSerializer.SerializeToElement(current, SettingsJson.Options);
        JsonElement after = JsonSerializer.SerializeToElement(imported, SettingsJson.Options);
        var changesBySection = before.EnumerateObject().Select(static property => property.Name)
            .Union(after.EnumerateObject().Select(static property => property.Name), StringComparer.Ordinal)
            .Where(section => !before.TryGetProperty(section, out var oldSection)
                || !after.TryGetProperty(section, out var newSection) || !JsonElement.DeepEquals(oldSection, newSection))
            .Select(static section => SettingsTransferService.IsKnownSection(section) ? section : "Other");
        changedSections = [.. changesBySection.Distinct(StringComparer.Ordinal)];
        if (changedSections.Length == 0)
            return static language => language["Message_NoSavedSettingsWillChange"];
        if (!includeDetails) return static language => language["Message_ChangesToImport"];
        var changes = new List<(string Prefix, LocalizedText Text)>();
        void Add(LocalizedText text, string prefix = "• ") => changes.Add((prefix, text));
        foreach (var (section, key) in new[] { ("Overlay", "Message_OverlaySettingsWillChange"),
            ("Updates", "Import_UpdateSettingsChange"), ("Diagnostics", "Import_DiagnosticsSettingsChange"),
            ("AdvancedTuning", "Message_AdvancedOptionsWillChange") })
            if (!JsonElement.DeepEquals(before.GetProperty(section), after.GetProperty(section))) Add(new(key));
        if (current.App.AutoSaveEnabled != imported.App.AutoSaveEnabled || current.App.PlayAppSounds != imported.App.PlayAppSounds
            || current.App.AutoScrollToMixerOnRestore != imported.App.AutoScrollToMixerOnRestore)
            Add(new("Message_GeneralSettingsWillChange"));
        if (current.Startup.UseTaskScheduler != imported.Startup.UseTaskScheduler || current.Startup.SuppressDeviceWarnings != imported.Startup.SuppressDeviceWarnings)
            Add(new("Import_StartupSettingsChange"));
        if (current.App.Theme != imported.App.Theme)
            Add(new("Message_Theme01", new LocalizedText($"Enum_AppTheme_{current.App.Theme}"), new LocalizedText($"Enum_AppTheme_{imported.App.Theme}")));
        if (current.Startup.Enabled != imported.Startup.Enabled)
            Add(new("Message_RunAtStartup01", OnOff(current.Startup.Enabled), OnOff(imported.Startup.Enabled)));
        if (current.App.Language != imported.App.Language)
            Add(new("Import_LanguageChange", LanguageLabel(current.App.Language), LanguageLabel(imported.App.Language)));

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
        if (current.Routines.DefaultTimeZoneId != imported.Routines.DefaultTimeZoneId)
            Add(new("Import_RoutineTimeZoneChange", Label(current.Routines.DefaultTimeZoneId), Label(imported.Routines.DefaultTimeZoneId)));
        if (!JsonElement.DeepEquals(before.GetProperty("Routines").GetProperty("Items"), after.GetProperty("Routines").GetProperty("Items")))
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

    internal static async Task<Settings?> CommitPreparedImportAsync(Settings approvedBaseline, Settings prepared,
        Func<Settings> readBaseline, SemaphoreSlim settingsWriteSemaphore,
        Action<Settings> persistSettings, Action<Settings> publishSettings, CancellationToken cancellationToken)
    {
        await settingsWriteSemaphore.WaitAsync(cancellationToken);
        try
        {
            Settings? changedBaseline = await Task.Run(() =>
            {
                Settings latest = readBaseline();
                if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(latest, SettingsJson.Options),
                    JsonSerializer.SerializeToElement(approvedBaseline, SettingsJson.Options))) return latest;
                cancellationToken.ThrowIfCancellationRequested();
                persistSettings(prepared);
                return null;
            }, cancellationToken);
            // The newly committed snapshot must be visible before another settings writer can read its baseline.
            if (changedBaseline == null) publishSettings(prepared);
            return changedBaseline;
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
