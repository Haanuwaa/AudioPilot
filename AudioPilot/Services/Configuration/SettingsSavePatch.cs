using System.Text.Json;
using System.Text.Json.Nodes;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration;

/// <summary>Captures an edit against its original baseline, then applies changed fields to the latest committed settings. Collections are replaced as a unit.</summary>
internal sealed class SettingsSavePatch
{
    private readonly List<Edit> _edits = [];

    internal static SettingsSavePatch Capture(Settings? baseline, Settings candidate)
    {
        var patch = new SettingsSavePatch();
        patch.CaptureChanges(
            JsonSerializer.SerializeToNode(baseline ?? new Settings(), SettingsJson.Options),
            JsonSerializer.SerializeToNode(candidate, SettingsJson.Options), []);
        return patch;
    }

    private void CaptureChanges(JsonNode? baseline, JsonNode? candidate, string[] path)
    {
        if (JsonNode.DeepEquals(baseline, candidate)) return;
        if (baseline is JsonObject original && candidate is JsonObject edited)
        {
            foreach ((string key, JsonNode? value) in edited)
                CaptureChanges(original[key], value, [.. path, key]);
            foreach (string key in original.Select(property => property.Key))
                if (!edited.ContainsKey(key)) _edits.Add(new([.. path, key], null, Remove: true));
            return;
        }
        _edits.Add(new(path, candidate?.DeepClone(), Remove: false));
    }

    internal Settings Apply(Settings latest)
    {
        JsonObject merged = JsonSerializer.SerializeToNode(latest, SettingsJson.Options)!.AsObject();
        foreach (Edit edit in _edits)
        {
            JsonObject parent = merged;
            foreach (string key in edit.Path[..^1])
            {
                if (parent[key] is not JsonObject child)
                {
                    child = [];
                    parent[key] = child;
                }
                parent = child;
            }
            if (edit.Remove) parent.Remove(edit.Path[^1]);
            else parent[edit.Path[^1]] = edit.Value?.DeepClone();
        }
        return merged.Deserialize<Settings>(SettingsJson.Options)!;
    }

    private readonly record struct Edit(string[] Path, JsonNode? Value, bool Remove);
}
