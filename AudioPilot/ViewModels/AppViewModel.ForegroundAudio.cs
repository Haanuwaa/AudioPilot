using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private ForegroundAudioControlService? _foregroundAudio;
    private readonly Lock _foregroundHotkeyGate = new();
    private readonly LinkedList<ForegroundHotkeyRequest> _foregroundHotkeys = [];
    private bool _foregroundHotkeysDraining;
    private bool _foregroundHotkeysOverflowLogged;

    private ForegroundAudioControlService ForegroundAudio =>
        _foregroundAudio ??= new(Logger.Instance, OnForegroundSessionVolumeApplied);

    private void OnForegroundSessionVolumeApplied(string? displayName, string? processName, float volumePercent, uint processId)
    {
        _audio?.UpdateSessionVolumeCache(displayName, processName, volumePercent, AudioMixerMode.Output, processId);
    }

    private readonly record struct ForegroundHotkeyRequest(
        ForegroundProcessTarget? Target, decimal? Delta, long Generation);

    internal void ChangeForegroundAudioFromHotkey(int direction)
    {
        long generation = _hotkeys.CaptureGeneration;
        if (!CanApplyAudioHotkey(generation)) return;
        var target = ForegroundAudioControlService.DispatchTarget;
        if (target is { } captured && ForegroundAudioControlService.TryResolveTarget(ref captured, out _)) target = captured;
        decimal step;
        lock (_settingsLock) step = _cachedSettings?.Hotkeys.Volume.ForegroundVolumeStepPercent ?? VolumePercent.DefaultStep;
        decimal? delta = direction == 0 ? null : Math.Sign(direction) * Math.Min(100, VolumePercent.NormalizeStep(step) * _hotkeys.VolumeDispatchSteps);
        lock (_foregroundHotkeyGate)
        {
            if (!CanApplyAudioHotkey(generation)) return;
            if (_foregroundHotkeys.Last is { } tail && delta is decimal adjustment && tail.Value.Delta is decimal previous &&
                Math.Sign(previous) == Math.Sign(adjustment) && tail.Value.Generation == generation &&
                CanCoalesceForegroundTargets(target, tail.Value.Target))
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

    private static bool CanCoalesceForegroundTargets(ForegroundProcessTarget? current, ForegroundProcessTarget? previous)
    {
        if (!current.HasValue && !previous.HasValue) return true;
        if (current is not { } cur || previous is not { } prev) return false;
        if (cur.ProcessId != prev.ProcessId || cur.IncludeDescendants != prev.IncludeDescendants || cur.ProtectedProcess != prev.ProtectedProcess)
        {
            return false;
        }

        if (cur.ProtectedProcess)
        {
            return cur.WindowHandle != 0 && cur.WindowHandle == prev.WindowHandle;
        }

        return cur.StartedUtcTicks == prev.StartedUtcTicks;
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
                var result = await ForegroundAudio.ApplyAsync(request.Target, request.Delta,
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
            "applied" or "partial" => direction == 0 ? result.Muted ? "App muted" : "App unmuted" : $"App volume: {VolumePercent.Format(result.Volume * 100)}%{(result.Muted ? " (muted)" : "")}",
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
        else
            _overlay.ShowForegroundApp(message, result.Name, icon, volumeState, result.AppliedTarget.GetValueOrDefault());
    }
}
