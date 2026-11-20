using System.IO;
using System.Text.Json;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI;

internal enum QuickPanelKind { DevicePicker, Mixer }
internal sealed record QuickPanelLayout(bool Pinned, bool AlwaysOnTop, SavedWindowPlacement? Placement);
internal sealed record QuickPanelLayouts(QuickPanelLayout? DevicePicker = null, QuickPanelLayout? Mixer = null);

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
        Task load;
        // A closing window can cancel its wait, but must not poison the shared load.
        lock (_sync) load = _load ??= Task.Run(Read, CancellationToken.None);
        await load.WaitAsync(token).ConfigureAwait(false);
        lock (_sync) return (kind == QuickPanelKind.DevicePicker ? _latest.DevicePicker : _latest.Mixer) ?? new(false, false, null);
    }

    private void Read()
    {
        try
        {
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 8192) throw new InvalidDataException("Quick panel layout is too large.");
            var state = JsonSerializer.Deserialize<QuickPanelLayouts>(stream, ReadOptions) ?? throw new InvalidDataException("Invalid panel layouts.");
            static QuickPanelLayout? Validate(QuickPanelLayout? layout) => layout?.Placement is { } p && !MainWindowPlacementService.IsValid(p)
                ? layout with { Placement = null } : layout;
            lock (_sync) if (_revision == 0) _latest = _persisted = new(Validate(state.DevicePicker), Validate(state.Mixer));
        }
        catch (Exception error) { logger.Warning("QuickPanelLayout", "quick-panel-layout-load-failed", nameof(Read), error); }
    }

    internal Task SaveAsync(QuickPanelKind kind, QuickPanelLayout layout)
    {
        if (layout.Placement is { } p && !MainWindowPlacementService.IsValid(p)) throw new ArgumentException("Invalid panel placement.", nameof(layout));
        lock (_sync)
        {
            var next = kind == QuickPanelKind.DevicePicker ? _latest with { DevicePicker = layout } : _latest with { Mixer = layout };
            if (next == _latest && (_draining || _latest == _persisted)) return _writing;
            _latest = next;
            _revision++;
            if (_draining) return _writing;
            _draining = true;
            return _writing = Task.Run(Drain);
        }
    }

    internal Task FlushAsync() { lock (_sync) return _writing; }

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
