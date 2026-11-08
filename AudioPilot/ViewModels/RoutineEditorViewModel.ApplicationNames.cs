using AudioPilot.Helpers;

namespace AudioPilot.ViewModels;

internal sealed partial class RoutineEditorViewModel
{
    private Dictionary<string, string> _packagedApplicationNames = new(StringComparer.OrdinalIgnoreCase);

    public string ResolvedTriggerAppTargetDisplayName => IsApplicationTriggerSelected ? GetResolvedApplicationName(TriggerAppPath) : string.Empty;
    public bool HasResolvedTriggerAppTarget => !string.IsNullOrWhiteSpace(ResolvedTriggerAppTargetDisplayName);
    public string ResolvedRequiredApplicationName => GetResolvedApplicationName(RequiredApplicationPath);
    public bool HasResolvedRequiredApplication => !string.IsNullOrWhiteSpace(ResolvedRequiredApplicationName);
    public string ResolvedRoutingApplicationName => RouteToApplication ? GetResolvedApplicationName(TargetAppPath) : string.Empty;
    public bool HasResolvedRoutingApplication => !string.IsNullOrWhiteSpace(ResolvedRoutingApplicationName);

    internal void SetPackagedApplicationDisplayNames(IReadOnlyList<AudioDeviceHelper.PackagedAppIdentity> apps)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            if (!string.IsNullOrWhiteSpace(app.AppUserModelId) && !string.IsNullOrWhiteSpace(app.DisplayName))
                names.TryAdd(app.AppUserModelId, app.DisplayName);
        if (names.Count == _packagedApplicationNames.Count && names.All(pair =>
            _packagedApplicationNames.TryGetValue(pair.Key, out string? name) && string.Equals(name, pair.Value, StringComparison.Ordinal))) return;
        _packagedApplicationNames = names;
        NotifyResolvedApplicationNames();
    }

    internal void SetResolvedPackagedAppDisplayName(string? displayName, string? appUserModelId = null)
    {
        string id = RoutineTriggerPathHelper.NormalizeTriggerTarget(appUserModelId ?? TriggerAppPath);
        if (!RoutineTriggerPathHelper.LooksLikePackagedAppId(id)) return;
        string name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            if (!_packagedApplicationNames.Remove(id)) return;
        }
        else
        {
            if (_packagedApplicationNames.TryGetValue(id, out string? existing) && string.Equals(existing, name, StringComparison.Ordinal)) return;
            _packagedApplicationNames[id] = name;
        }
        NotifyResolvedApplicationNames();
    }

    private string GetResolvedApplicationName(string target)
    {
        if (!RoutineTriggerPathHelper.LooksLikeSupportedStartupTarget(target)) return string.Empty;
        string normalized = RoutineTriggerPathHelper.NormalizeTriggerTarget(target);
        return _packagedApplicationNames.TryGetValue(normalized, out string? name)
            ? name : RoutineTriggerPathHelper.GetTriggerDisplayName(normalized);
    }

    private void NotifyResolvedApplicationNames()
    {
        OnPropertyChanged(nameof(ResolvedTriggerAppTargetDisplayName));
        OnPropertyChanged(nameof(HasResolvedTriggerAppTarget));
        OnPropertyChanged(nameof(ResolvedRequiredApplicationName));
        OnPropertyChanged(nameof(HasResolvedRequiredApplication));
        OnPropertyChanged(nameof(ResolvedRoutingApplicationName));
        OnPropertyChanged(nameof(HasResolvedRoutingApplication));
        OnPropertyChanged(nameof(ConditionsSummary));
    }
}
