using System.Collections.Immutable;
using System.Text.Json;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

internal enum QuickPanelKind { DevicePicker, Mixer }
internal sealed record QuickPanelLayout(bool Pinned, bool AlwaysOnTop, SavedWindowPlacement? Placement);
internal sealed record QuickMixerPreferences(bool PlayingFirst, ImmutableHashSet<string> PinnedApps)
{
    internal const int MaxPinnedApps = 96;
    internal static QuickMixerPreferences Default { get; } = new(false, []);
}
internal sealed record QuickPanelLayouts(QuickPanelLayout? DevicePicker = null, QuickPanelLayout? Mixer = null,
    QuickMixerPreferences? MixerPreferences = null);

/// <summary>Two independent local UI layouts, shared by reopened windows and written atomically off-thread.</summary>
internal sealed class QuickPanelLayoutStore(string path, Logger logger, Action<string, string>? write = null)
{
    internal const string FileName = "quick-panel-layouts.json";
    private static readonly JsonSerializerOptions ReadOptions = new()
    { AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true };
    private readonly Lock _sync = new();
    private readonly Action<string, string> _write = write ?? AtomicFileWriter.WriteAllText;
    private QuickPanelLayouts _latest = new();
    private QuickPanelLayouts? _persisted;
    private Task? _load;
    private Task _writing = Task.CompletedTask;
    private long _revision;
    private bool _draining;

    internal async Task<QuickPanelLayout> LoadAsync(QuickPanelKind kind, CancellationToken token)
    {
        await EnsureLoadedAsync(token).ConfigureAwait(false);
        lock (_sync) return (kind == QuickPanelKind.DevicePicker ? _latest.DevicePicker : _latest.Mixer) ?? new(false, false, null);
    }

    internal async Task<QuickMixerPreferences> LoadMixerPreferencesAsync(CancellationToken token)
    {
        await EnsureLoadedAsync(token).ConfigureAwait(false);
        lock (_sync) return _latest.MixerPreferences ?? QuickMixerPreferences.Default;
    }

    private Task EnsureLoadedAsync(CancellationToken token)
    {
        // A closing window can cancel its wait, but must not poison the shared load.
        lock (_sync) return (_load ??= Task.Run(Read, CancellationToken.None)).WaitAsync(token);
    }

    private void Read()
    {
        try
        {
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 8192) throw new InvalidDataException("Quick panel layout is too large.");
            var state = JsonSerializer.Deserialize<QuickPanelLayouts>(stream, ReadOptions) ?? throw new InvalidDataException("Invalid panel layouts.");
            static QuickPanelLayout? Validate(QuickPanelLayout? layout) => layout?.Placement is { } p && !WindowPlacementStore.IsValid(p)
                ? layout with { Placement = null } : layout;
            var preferences = state.MixerPreferences;
            if (preferences != null)
            {
                var validPins = (preferences.PinnedApps ?? []).Where(IsAppKey).Take(QuickMixerPreferences.MaxPinnedApps);
                preferences = preferences with { PinnedApps = [.. validPins] };
            }
            lock (_sync) if (_revision == 0) _latest = _persisted = new(Validate(state.DevicePicker), Validate(state.Mixer), preferences);
        }
        catch (Exception error) { logger.Warning("QuickPanelLayout", "quick-panel-layout-load-failed", nameof(Read), error); }
    }

    internal Task SaveAsync(QuickPanelKind kind, QuickPanelLayout layout)
    {
        if (layout.Placement is { } p && !WindowPlacementStore.IsValid(p)) throw new ArgumentException("Invalid panel placement.", nameof(layout));
        lock (_sync)
            return SaveLocked(kind == QuickPanelKind.DevicePicker ? _latest with { DevicePicker = layout } : _latest with { Mixer = layout });
    }

    internal Task SaveMixerPreferencesAsync(QuickMixerPreferences preferences, QuickPanelLayout? layout = null)
    {
        lock (_sync) return SaveLocked(_latest with { MixerPreferences = preferences, Mixer = layout ?? _latest.Mixer });
    }

    private Task SaveLocked(QuickPanelLayouts next)
    {
        if (next == _latest && (_draining || _latest == _persisted)) return _writing;
        _latest = next;
        _revision++;
        if (_draining) return _writing;
        _draining = true;
        return _writing = Task.Run(Drain);
    }

    private static bool IsAppKey(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);

    internal async Task FlushAsync()
    {
        Task writing;
        lock (_sync) writing = _writing;
        await writing.ConfigureAwait(false);

        lock (_sync)
        {
            if (_revision == 0 || _latest == _persisted) return;
            writing = _draining ? _writing : SaveLocked(_latest);
        }
        await writing.ConfigureAwait(false);
    }

    private void Drain()
    {
        while (true)
        {
            QuickPanelLayouts snapshot;
            long revision;
            lock (_sync) { snapshot = _latest; revision = _revision; }
            try { _write(path, JsonSerializer.Serialize(snapshot)); lock (_sync) _persisted = snapshot; }
            catch (Exception error) { logger.Warning("QuickPanelLayout", "quick-panel-layout-save-failed", nameof(Drain), error); }
            lock (_sync) if (revision == _revision) { _draining = false; return; }
        }
    }
}
