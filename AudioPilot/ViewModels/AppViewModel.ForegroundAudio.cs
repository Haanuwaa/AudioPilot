using AudioPilot.Logging;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private readonly ForegroundAudioControlService _foregroundAudio = new(Logger.Instance);
    private readonly Lock _foregroundHotkeyGate = new();
    private readonly LinkedList<ForegroundHotkeyRequest> _foregroundHotkeys = [];
    private bool _foregroundHotkeysDraining;
    private bool _foregroundHotkeysOverflowLogged;

    private readonly record struct ForegroundHotkeyRequest(
        ForegroundProcessTarget? Target, int? Delta, long Generation);

    internal void ChangeForegroundAudioFromHotkey(int direction)
    {
        long generation = _hotkeys.CaptureGeneration;
        if (!CanApplyAudioHotkey(generation)) return;
        var target = ForegroundAudioControlService.DispatchTarget;
        if (target is { } captured && ForegroundAudioControlService.TryResolveTarget(ref captured, out _)) target = captured;
        int step;
        lock (_settingsLock) step = _cachedSettings?.Hotkeys.Volume.ForegroundVolumeStepPercent ?? 5;
        int? delta = direction == 0 ? null : Math.Sign(direction) * Math.Clamp(step, 1, 100);
        lock (_foregroundHotkeyGate)
        {
            if (!CanApplyAudioHotkey(generation)) return;
            if (_foregroundHotkeys.Last is { } tail && delta is int adjustment && tail.Value.Delta is int previous &&
                Math.Sign(previous) == Math.Sign(adjustment) && tail.Value.Generation == generation &&
                target is { StartedUtcTicks: > 0 } current && tail.Value.Target is { } last &&
                current.ProcessId == last.ProcessId && current.StartedUtcTicks == last.StartedUtcTicks &&
                current.IncludeDescendants == last.IncludeDescendants)
            {
                tail.Value = tail.Value with { Delta = Math.Clamp(previous + adjustment, -100, 100) };
                return;
            }

            if (_foregroundHotkeys.Count >= (delta.HasValue ? 96 : 128))
            {
                if (!_foregroundHotkeysOverflowLogged)
                {
                    _foregroundHotkeysOverflowLogged = true;
                    _logger.Warning("ForegroundAudio", "foreground-hotkey-queue-full");
                }
                return;
            }

            _foregroundHotkeys.AddLast(new ForegroundHotkeyRequest(target, delta, generation));
            if (_foregroundHotkeysDraining) return;
            _foregroundHotkeysDraining = true;
        }

        RunBackgroundWork(DrainForegroundHotkeysAsync, "foreground-hotkeys", onDiscarded: DiscardForegroundHotkeys);
    }

    private bool CanApplyAudioHotkey(long generation) =>
        !_isCleaningUp && !IsRuntimeShutdownRequested && _hotkeys.CanDispatch(generation);

    private async Task DrainForegroundHotkeysAsync(CancellationToken token)
    {
        while (true)
        {
            ForegroundHotkeyRequest request;
            lock (_foregroundHotkeyGate)
            {
                if (token.IsCancellationRequested || _foregroundHotkeys.First == null)
                {
                    DiscardForegroundHotkeys();
                    return;
                }
                request = _foregroundHotkeys.First.Value;
                _foregroundHotkeys.RemoveFirst();
            }
            if (!CanApplyAudioHotkey(request.Generation)) continue;
            try
            {
                var result = await _foregroundAudio.ApplyAsync(request.Target, request.Delta,
                    () => CanApplyAudioHotkey(request.Generation), token).ConfigureAwait(false);
                if (CanApplyAudioHotkey(request.Generation) && result.Code != "cancelled")
                    ShowForegroundHotkeyResult(result, Math.Sign(request.Delta ?? 0));
            }
            catch (Exception ex)
            {
                _logger.Warning("ForegroundAudio", "foreground-hotkey-failed", nameof(DrainForegroundHotkeysAsync), ex);
            }
        }
    }

    private void DiscardForegroundHotkeys()
    {
        lock (_foregroundHotkeyGate)
        {
            _foregroundHotkeys.Clear();
            _foregroundHotkeysDraining = false;
            _foregroundHotkeysOverflowLogged = false;
        }
    }

    private void ShowForegroundHotkeyResult(ForegroundAudioResult result, int direction)
    {
        string message = result.Code switch
        {
            "applied" or "partial" => direction == 0 ? result.Muted ? "App muted" : "App unmuted" : $"App volume: {Math.Round(result.Volume * 100)}%{(result.Muted ? " (muted)" : "")}",
            "app-exited" => "The foreground app has closed",
            "access-denied" => "Windows denied access to this app",
            "no-audio-session" => "No audio session for this app",
            "no-foreground-app" => "No foreground app to control",
            _ => "Could not change app audio",
        };
        if (result.Code == "partial") message += " (partially applied)";
        OverlayIcon icon = result.Code is "applied" ? result.Muted ? OverlayIcon.SpeakerMuted : OverlayIcon.Speaker
            : result.Code == "partial" ? OverlayIcon.Warning : OverlayIcon.Information;
        OverlayActionStateKind? volumeState = direction != 0 && result.Code is "applied" or "partial"
            ? direction > 0 ? OverlayActionStateKind.Enabled : OverlayActionStateKind.Disabled
            : null;
        if (string.IsNullOrWhiteSpace(result.Name))
        {
            if (volumeState.HasValue) _overlay.Show(volumeState.Value, message, icon: icon);
            else _overlay.Show(message, icon: icon);
        }
        else _overlay.Show(OverlayDeviceKind.Output, message, result.Name, icon: icon, headerState: volumeState);
    }
}
