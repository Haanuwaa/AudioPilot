using AudioPilot.Coordinators;
using AudioPilot.Models;

namespace AudioPilot.Tests.TestDoubles;

internal sealed class FakeStartupViewModel : IStartupViewModel
{
    public int InitializeCalls { get; private set; }
    public int RegisterRoutineHotkeysCalls { get; private set; }
    public int EnableRoutineAppStartMonitoringCalls { get; private set; }
    public int ExecuteAudioPilotStartupRoutinesCalls { get; private set; }
    public int MarkStartupVisibilityResolvedCalls { get; private set; }
    public int ShowCalls { get; private set; }
    public int StartHiddenCalls { get; private set; }
    public int MinimizeCalls { get; private set; }
    public bool? LastNoSettingsFlag { get; private set; }
    public bool? LastAudioPilotStartupShowOverlay { get; private set; }
    public Settings? CurrentSettings { get; set; }
    public IReadOnlyList<SettingsDiagnostic> Diagnostics { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public string? LoadWarning { get; set; }
    public Func<bool>? HasInteractiveShowRequestProvider { get; set; }
    public bool HasInteractiveShowRequest { get; set; }

    public Task InitializeAsync(bool noSettingsFileExists)
    {
        InitializeCalls++;
        LastNoSettingsFlag = noSettingsFileExists;
        return Task.CompletedTask;
    }

    public IReadOnlyList<SettingsDiagnostic> GetConfigurationWarningDiagnosticsForUi()
    {
        if (Diagnostics.Count > 0)
        {
            return Diagnostics;
        }

        return [.. Warnings.Select(static warning => new SettingsDiagnostic("test-warning", warning, string.Empty))];
    }

    public string? GetConfigurationLoadWarningForUi() => LoadWarning;

    public void RegisterRoutineHotkeys(Settings settings)
    {
        RegisterRoutineHotkeysCalls++;
        CurrentSettings = settings;
    }

    public void EnableRoutineAppStartMonitoring()
    {
        EnableRoutineAppStartMonitoringCalls++;
    }

    public Task ExecuteAudioPilotStartupRoutinesAsync(bool showOverlay)
    {
        ExecuteAudioPilotStartupRoutinesCalls++;
        LastAudioPilotStartupShowOverlay = showOverlay;
        return Task.CompletedTask;
    }

    bool IStartupViewModel.HasInteractiveShowRequest => HasInteractiveShowRequestProvider?.Invoke() ?? HasInteractiveShowRequest;

    public void MarkStartupVisibilityResolved()
    {
        MarkStartupVisibilityResolvedCalls++;
    }

    public Task<bool> ShowWindowAsync()
    {
        ShowCalls++;
        return Task.FromResult(true);
    }

    public Task<bool> StartHiddenToTrayAsync()
    {
        StartHiddenCalls++;
        return Task.FromResult(true);
    }

    public void MinimizeWindow() => MinimizeCalls++;
}

internal sealed class FakeStartupHotkeyRegistrar : IStartupHotkeyRegistrar
{
    public bool ToggleAppVisibilityResult { get; set; } = true;
    public bool ShowAudioStatusResult { get; set; } = true;
    public bool MediaResult { get; set; } = true;
    public bool MuteResult { get; set; } = true;
    public bool ListenResult { get; set; } = true;
    public bool VolumeStepResult { get; set; } = true;
    public bool OutputSwitchResult { get; set; } = true;
    public bool InputSwitchResult { get; set; } = true;
    public bool OutputReverseResult { get; set; } = true;
    public bool InputReverseResult { get; set; } = true;
    public int ToggleAppVisibilityCalls { get; private set; }
    public int ShowAudioStatusCalls { get; private set; }
    public string? LastShowAudioStatusHotkey { get; private set; }
    public int MediaCalls { get; private set; }
    public int MuteCalls { get; private set; }
    public (string? Talk, string? Mute) LastHolds { get; private set; }
    public (string? Up, string? Down, string? Mute) LastForeground { get; private set; }
    public int ListenCalls { get; private set; }
    public int VolumeStepCalls { get; private set; }
    public int OutputSwitchCalls { get; private set; }
    public int InputSwitchCalls { get; private set; }
    public int OutputReverseCalls { get; private set; }
    public int InputReverseCalls { get; private set; }
    public string? LastOutputSwitchHotkey { get; private set; }
    public string? LastInputSwitchHotkey { get; private set; }
    public string? LastOutputReverseHotkey { get; private set; }
    public string? LastInputReverseHotkey { get; private set; }

    public bool RegisterToggleAppVisibilityHotkey(string? hotkey)
    {
        ToggleAppVisibilityCalls++;

        return ToggleAppVisibilityResult;
    }

    public bool RegisterShowAudioStatusHotkey(string? hotkey)
    {
        ShowAudioStatusCalls++;
        LastShowAudioStatusHotkey = hotkey;

        return ShowAudioStatusResult;
    }

    public bool RegisterMediaHotkeys(string? showCurrent, string? playPause, string? nextTrack, string? previousTrack, string? seekForward = null, string? seekBackward = null)
    {
        MediaCalls++;

        return MediaResult;
    }

    public bool RegisterMuteHotkeys(string? muteMic, string? muteSound, string? deafen, string? pushToTalk, string? holdToMute)
    {
        MuteCalls++;
        LastHolds = (pushToTalk, holdToMute);

        return MuteResult;
    }

    public bool RegisterListenToInputHotkey(string? hotkey)
    {
        ListenCalls++;

        return ListenResult;
    }

    public bool RegisterVolumeStepHotkeys(string? masterUp, string? masterDown, string? micUp, string? micDown, string? foregroundUp, string? foregroundDown, string? foregroundMute)
    {
        VolumeStepCalls++;
        LastForeground = (foregroundUp, foregroundDown, foregroundMute);

        return VolumeStepResult;
    }

    public bool RegisterOutputSwitchHotkey(string? hotkey)
    {
        OutputSwitchCalls++;
        LastOutputSwitchHotkey = hotkey;

        return OutputSwitchResult;
    }

    public bool RegisterInputSwitchHotkey(string? hotkey)
    {
        InputSwitchCalls++;
        LastInputSwitchHotkey = hotkey;

        return InputSwitchResult;
    }

    public bool RegisterOutputReverseSwitchHotkey(string? hotkey)
    {
        OutputReverseCalls++;
        LastOutputReverseHotkey = hotkey;

        return OutputReverseResult;
    }

    public bool RegisterInputReverseSwitchHotkey(string? hotkey)
    {
        InputReverseCalls++;
        LastInputReverseHotkey = hotkey;

        return InputReverseResult;
    }

}
