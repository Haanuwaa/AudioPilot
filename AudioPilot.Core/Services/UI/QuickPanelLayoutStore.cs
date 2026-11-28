using System.Collections.Immutable;
using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

internal enum QuickPanelKind { DevicePicker, Mixer }
internal sealed record QuickPanelLayout(bool Pinned, bool AlwaysOnTop, SavedWindowPlacement? Placement);
internal sealed record QuickMixerPreferences(bool PlayingFirst, ImmutableHashSet<string> PinnedApps)
{
    internal const int MaxPinnedApps = 96;
    internal static QuickMixerPreferences Default { get; } = new(false, []);

    public bool Equals(QuickMixerPreferences? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || PlayingFirst != other.PlayingFirst) return false;
        ImmutableHashSet<string> left = PinnedApps ?? [];
        ImmutableHashSet<string> right = other.PinnedApps ?? [];
        return left.Count == right.Count && left.SetEquals(right);
    }

    public override int GetHashCode()
    {
        int pinHash = 0;
        if (PinnedApps != null)
        {
            foreach (string key in PinnedApps)
            {
                pinHash ^= StringComparer.Ordinal.GetHashCode(key);
            }
        }
        return HashCode.Combine(PlayingFirst, PinnedApps?.Count ?? 0, pinHash);
    }
}
internal sealed record QuickPanelLayouts(QuickPanelLayout? DevicePicker = null, QuickPanelLayout? Mixer = null,
    QuickMixerPreferences? MixerPreferences = null);

/// <summary>Two independent local UI layouts, shared by reopened windows and written atomically off-thread.</summary>
internal sealed class QuickPanelLayoutStore(string path, Logger logger, Action<string, string>? write = null)
{
    internal const string FileName = AppConstants.Files.QuickPanelLayoutsFileName;
    private static readonly JsonSerializerOptions ReadOptions = new()
    { AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true };
    private readonly Lock _sync = new();
    private readonly Action<string, string> _write = write ?? AtomicFileWriter.WriteAllText;
    private QuickPanelLayouts _latest = new();
    private QuickPanelLayouts? _persisted;
    private Task? _load;
    private bool _loaded;
    private bool _devicePickerDirty;
    private bool _mixerDirty;
    private bool _mixerPreferencesDirty;
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
        lock (_sync)
        {
            if (_loaded) return Task.CompletedTask;
            return (_load ??= Task.Run(Read, CancellationToken.None)).WaitAsync(token);
        }
    }

    private void Read()
    {
        bool retryableFailure = false;
        try
        {
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 8192) throw new InvalidDataException("Quick panel layout is too large.");
            var state = JsonSerializer.Deserialize<QuickPanelLayouts>(stream, ReadOptions) ?? throw new InvalidDataException("Invalid panel layouts.");
            QuickPanelLayouts loaded = new(
                ValidateLayout(state.DevicePicker),
                ValidateLayout(state.Mixer),
                SanitizePreferences(state.MixerPreferences));
            lock (_sync)
            {
                _persisted = loaded;
                _latest = new(
                    _devicePickerDirty ? _latest.DevicePicker : loaded.DevicePicker,
                    _mixerDirty ? _latest.Mixer : loaded.Mixer,
                    _mixerPreferencesDirty ? _latest.MixerPreferences : loaded.MixerPreferences);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            retryableFailure = true;
            logger.Warning("QuickPanelLayout", "quick-panel-layout-load-failed", nameof(Read), error);
        }
        catch (Exception error)
        {
            logger.Warning("QuickPanelLayout", "quick-panel-layout-load-failed", nameof(Read), error);
        }
        finally
        {
            lock (_sync)
            {
                if (retryableFailure) _load = null;
                else _loaded = true;
            }
        }
    }

    internal Task SaveAsync(QuickPanelKind kind, QuickPanelLayout layout)
    {
        if (layout.Placement is { } p && !WindowPlacementStore.IsValid(p)) throw new ArgumentException("Invalid panel placement.", nameof(layout));
        lock (_sync)
        {
            if (kind == QuickPanelKind.DevicePicker) _devicePickerDirty = true;
            else _mixerDirty = true;
            return SaveLocked(kind == QuickPanelKind.DevicePicker ? _latest with { DevicePicker = layout } : _latest with { Mixer = layout });
        }
    }

    internal Task SaveMixerPreferencesAsync(QuickMixerPreferences preferences, QuickPanelLayout? layout = null)
    {
        if (layout?.Placement is { } p && !WindowPlacementStore.IsValid(p)) throw new ArgumentException("Invalid panel placement.", nameof(layout));
        QuickMixerPreferences sanitized = SanitizePreferences(preferences)!;
        lock (_sync)
        {
            _mixerPreferencesDirty = true;
            if (layout != null) _mixerDirty = true;
            return SaveLocked(_latest with { MixerPreferences = sanitized, Mixer = layout ?? _latest.Mixer });
        }
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

    private static QuickPanelLayout? ValidateLayout(QuickPanelLayout? layout) =>
        layout?.Placement is { } p && !WindowPlacementStore.IsValid(p) ? layout with { Placement = null } : layout;

    private static QuickMixerPreferences? SanitizePreferences(QuickMixerPreferences? preferences)
    {
        if (preferences == null) return null;
        var validPins = (preferences.PinnedApps ?? []).Where(IsAppKey).Take(QuickMixerPreferences.MaxPinnedApps);
        return preferences with { PinnedApps = [.. validPins] };
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
            Task? loadTask = null;
            lock (_sync)
            {
                if (!_loaded)
                {
                    loadTask = _load ??= Task.Run(Read, CancellationToken.None);
                }
            }
            if (loadTask != null)
            {
                try { loadTask.GetAwaiter().GetResult(); }
                catch { }
            }

            QuickPanelLayouts snapshot;
            long revision;
            lock (_sync)
            {
                if (!_loaded)
                {
                    _draining = false;
                    return;
                }
                snapshot = _latest;
                revision = _revision;
            }
            try { _write(path, JsonSerializer.Serialize(snapshot)); lock (_sync) _persisted = snapshot; }
            catch (Exception error) { logger.Warning("QuickPanelLayout", "quick-panel-layout-save-failed", nameof(Drain), error); }
            lock (_sync) if (revision == _revision) { _draining = false; return; }
        }
    }
}
