using AudioPilot.Logging;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private readonly ForegroundAudioControlService _foregroundAudio = new(Logger.Instance);

    internal void ChangeForegroundAudioFromHotkey(int direction)
    {
        if (_isCleaningUp) return;
        var target = ForegroundAudioControlService.DispatchTarget;
        int step;
        lock (_settingsLock) step = _cachedSettings?.Hotkeys.Volume.ForegroundVolumeStepPercent ?? 5;
        var result = _foregroundAudio.Apply(target, direction == 0 ? null : Math.Sign(direction) * Math.Clamp(step, 1, 100), ShutdownToken);
        if (_isCleaningUp || result.Code is "busy" or "cancelled") return;
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
        if (string.IsNullOrWhiteSpace(result.Name)) _overlay.Show(message, icon: icon);
        else _overlay.Show(OverlayDeviceKind.Output, message, result.Name, icon: icon);
    }
}
