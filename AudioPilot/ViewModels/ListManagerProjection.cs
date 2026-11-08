using AudioPilot.Localization;
using AudioPilot.Models;
using NAudio.CoreAudioApi;

namespace AudioPilot.ViewModels;

internal enum ListManagerMode { Output, Input, Routines }

internal sealed record ListManagerDeviceInventory(
    IReadOnlyList<CycleDevice> Active, IReadOnlyDictionary<Role, string> Defaults, IReadOnlyList<Role> UnreadableRoles,
    bool IsComplete = true);

internal sealed record ListManagerRow(object Item, string Id, string Name, int? Position, string Status, string Summary,
    bool? Connected = null, bool NeedsAttention = false, int? DuplicateOfPosition = null, string? DefaultRoles = null,
    string? ResolvedDeviceId = null)
{
    public string Title => Position is { } position ? $"{position}. {Name}" : Name;
    public string Subtitle => string.IsNullOrEmpty(DefaultRoles) ? Summary : $"Default for {DefaultRoles}";
    public string RowSummary => string.IsNullOrEmpty(Subtitle) ? Status : $"{Status} · {Subtitle}";
    public Func<LocalizationService, string> RowPresentation => FormatRow;
    public Func<LocalizationService, string> StatusPresentation => FormatStatus;

    internal string FormatStatus(LocalizationService localization)
    {
        if (Item is AudioRoutine routine)
        {
            string status = routine.StatusPresentation(localization);
            return NeedsAttention ? localization.FormatKey("Manage_RoutineAttention", status) : status;
        }
        return localization[DuplicateOfPosition.HasValue ? "Manage_Duplicate" : Connected switch
        {
            true => "Manage_ConnectedRow",
            false => "Manage_DisconnectedRow",
            _ => "Manage_StatusUnavailable",
        }];
    }

    private string FormatRow(LocalizationService localization)
    {
        string status = FormatStatus(localization);
        string summary = Item is AudioRoutine routine ? routine.TriggerPresentation(localization)
            : DuplicateOfPosition is int duplicate ? localization.FormatKey("Manage_EntryMatch", duplicate)
            : string.IsNullOrEmpty(DefaultRoles) ? string.Empty : localization.FormatKey("Manage_DefaultFor", FormatRoles(DefaultRoles, localization));
        return string.IsNullOrEmpty(summary) ? status : $"{status} · {summary}";
    }

    internal static string FormatRoles(string roles, LocalizationService localization) => string.Join(", ",
        roles.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(role => role switch
        {
            nameof(Role.Communications) => localization["Ui_Communications"],
            nameof(Role.Console) => localization["Ui_Console"],
            _ => role,
        }));

}

internal static class ListManagerProjection
{
    private static readonly LocalizationService[] AdditionalSearchLanguages = [.. LocalizationService.Languages
        .Where(static option => option.Code is not ("system" or "en"))
        .Select(static option => new LocalizationService(option.Code))];

    internal static ListManagerRow[] BuildDeviceRows(IReadOnlyList<CycleDevice> configured, ListManagerDeviceInventory? inventory)
    {
        Dictionary<string, CycleDevice> active = QuickDevicePickerItem.IndexAvailableDevices(inventory?.Active ?? []);
        CycleDevice?[] matches = inventory?.IsComplete == false
            ? [.. configured.Select(device => active.GetValueOrDefault(device.Id))]
            : QuickDevicePickerItem.MatchConfiguredDevices(configured, active);
        var resolvedPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < matches.Length; index++)
        {
            if (matches[index] is not { } endpoint) continue;
            bool exact = string.Equals(endpoint.Id, configured[index].Id, StringComparison.OrdinalIgnoreCase);
            if (!resolvedPositions.TryGetValue(endpoint.Id, out int previous)
                || (exact && !string.Equals(endpoint.Id, configured[previous].Id, StringComparison.OrdinalIgnoreCase)))
                resolvedPositions[endpoint.Id] = index;
        }

        var rows = new List<ListManagerRow>(configured.Count + active.Count);
        for (int index = 0; index < configured.Count; index++)
        {
            CycleDevice item = configured[index];
            CycleDevice? match = matches[index];
            if (match != null && resolvedPositions[match.Id] != index)
            {
                int position = resolvedPositions[match.Id] + 1;
                rows.Add(new(item, item.Id, item.Name, index + 1, "Duplicate", $"Same device as entry {position}", DuplicateOfPosition: position));
                continue;
            }
            bool? connected = match != null ? true : inventory is { IsComplete: true } ? false : null;
            string status = connected switch { true => "Connected", false => "Disconnected", _ => "Status unavailable" };
            rows.Add(new(item, item.Id, match?.Name ?? item.Name, index + 1, status, string.Empty, connected,
                DefaultRoles: match == null ? null : FormatDefaultRoles(inventory!, match.Id), ResolvedDeviceId: match?.Id));
        }
        foreach (CycleDevice item in active.Values)
            if (!resolvedPositions.ContainsKey(item.Id))
                rows.Add(new(item, item.Id, item.Name, null, "Connected", string.Empty, true, DefaultRoles: FormatDefaultRoles(inventory!, item.Id), ResolvedDeviceId: item.Id));
        return [.. rows];
    }

    private static string FormatDefaultRoles(ListManagerDeviceInventory inventory, string endpointId)
    {
        string[] roles = [.. inventory.Defaults.Where(pair => string.Equals(pair.Value, endpointId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key.ToString())];
        return string.Join(", ", roles);
    }

    internal static ListManagerRow BuildRoutineRow(AudioRoutine routine)
    {
        bool issue = routine.HasConflict || routine.HasHotkeyWarning || routine.LastRunState == RoutineLastRunState.Failed
            || (routine.LastRunState == RoutineLastRunState.Skipped && routine.LastRunSkipCode is
                "routine-condition-invalid" or "routine-condition-unavailable" or Cli.CliRoutineExecutionPolicy.TargetAppUnavailableCode);
        string status = issue ? $"{routine.StatusLabel} · Needs attention" : routine.StatusLabel;
        return new(routine, routine.Id, routine.Name, routine.DisplayOrder, status, routine.TriggerSummary, NeedsAttention: issue);
    }

    internal static ListManagerRow[] GetVisibleRows(IEnumerable<ListManagerRow> all, ListManagerMode mode, string search, int filter, int sort)
    {
        string query = search.Trim();
        IEnumerable<ListManagerRow> rows = mode == ListManagerMode.Routines
            ? all.Where(row => filter switch
            {
                1 => ((AudioRoutine)row.Item).Enabled,
                2 => !((AudioRoutine)row.Item).Enabled,
                3 => row.NeedsAttention,
                _ => true,
            })
            : all.Where(row => filter == 3 ? row.Position == null : row.Position != null && (filter switch
            {
                1 => row.Connected == true,
                2 => row.Connected == false,
                _ => true,
            }));
        if (query.Length != 0)
            rows = rows.Where(row => row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (row.Item is CycleDevice device && device.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                || (row.Item is AudioRoutine routine && (routine.Hotkey.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || row.Subtitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || routine.RoutineDetailsTriggerSummary.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || AdditionalSearchLanguages.Any(language => routine.TriggerPresentation(language).Contains(query, StringComparison.CurrentCultureIgnoreCase)))));
        return sort switch
        {
            1 => [.. rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)],
            2 => [.. rows.OrderBy(row => row.Status, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)],
            _ => [.. rows.OrderBy(row => row.Position).ThenBy(row => row.Position == null ? row.Name : string.Empty, StringComparer.CurrentCultureIgnoreCase)],
        };
    }
}
