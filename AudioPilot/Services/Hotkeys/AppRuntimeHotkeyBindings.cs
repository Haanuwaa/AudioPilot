namespace AudioPilot.Services.Hotkeys
{
    internal sealed class AppRuntimeHotkeyBindings(
        HotkeyService hotkeyService,
        Action onToggleAppVisibility,
        Action onMediaShowCurrentTrack,
        Action onMediaPlayPause,
        Action onMediaNextTrack,
        Action onMediaPreviousTrack,
        Action onMuteMic,
        Action onMuteSound,
        Action onDeafen,
        Action onListenToInput,
        Action onMasterVolumeUp,
        Action onMasterVolumeDown,
        Action onMicVolumeUp,
        Action onMicVolumeDown,
        Action onInputSwitch,
        Action onOutputSwitch,
        Action onInputReverseSwitch,
        Action onOutputReverseSwitch)
    {
        private readonly HotkeyService _hotkeyService = hotkeyService;
        private readonly Action _onToggleAppVisibility = onToggleAppVisibility;
        private readonly Action _onMediaShowCurrentTrack = onMediaShowCurrentTrack;
        private readonly Action _onMediaPlayPause = onMediaPlayPause;
        private readonly Action _onMediaNextTrack = onMediaNextTrack;
        private readonly Action _onMediaPreviousTrack = onMediaPreviousTrack;
        private readonly Action _onMuteMic = onMuteMic;
        private readonly Action _onMuteSound = onMuteSound;
        private readonly Action _onDeafen = onDeafen;
        private readonly Action _onListenToInput = onListenToInput;
        private readonly Action _onMasterVolumeUp = onMasterVolumeUp;
        private readonly Action _onMasterVolumeDown = onMasterVolumeDown;
        private readonly Action _onMicVolumeUp = onMicVolumeUp;
        private readonly Action _onMicVolumeDown = onMicVolumeDown;
        private readonly Action _onInputSwitch = onInputSwitch;
        private readonly Action _onOutputSwitch = onOutputSwitch;
        private readonly Action _onInputReverseSwitch = onInputReverseSwitch;
        private readonly Action _onOutputReverseSwitch = onOutputReverseSwitch;
        private bool _wired;

        public void Wire()
        {
            if (_wired)
            {
                return;
            }

            _hotkeyService.OnToggleAppVisibilityHotkeyPressed += _onToggleAppVisibility;
            _hotkeyService.OnMediaShowCurrentTrackPressed += _onMediaShowCurrentTrack;
            _hotkeyService.OnMediaPlayPausePressed += _onMediaPlayPause;
            _hotkeyService.OnMediaNextTrackPressed += _onMediaNextTrack;
            _hotkeyService.OnMediaPrevTrackPressed += _onMediaPreviousTrack;
            _hotkeyService.OnMuteMicPressed += _onMuteMic;
            _hotkeyService.OnMuteSoundPressed += _onMuteSound;
            _hotkeyService.OnDeafenPressed += _onDeafen;
            _hotkeyService.OnListenToInputPressed += _onListenToInput;
            _hotkeyService.OnMasterVolumeUpPressed += _onMasterVolumeUp;
            _hotkeyService.OnMasterVolumeDownPressed += _onMasterVolumeDown;
            _hotkeyService.OnMicVolumeUpPressed += _onMicVolumeUp;
            _hotkeyService.OnMicVolumeDownPressed += _onMicVolumeDown;
            _hotkeyService.OnInputSwitchHotkeyPressed += _onInputSwitch;
            _hotkeyService.OnOutputSwitchHotkeyPressed += _onOutputSwitch;
            _hotkeyService.OnInputReverseSwitchHotkeyPressed += _onInputReverseSwitch;
            _hotkeyService.OnOutputReverseSwitchHotkeyPressed += _onOutputReverseSwitch;

            _wired = true;
        }

        public void Unwire()
        {
            if (!_wired)
            {
                return;
            }

            _hotkeyService.OnToggleAppVisibilityHotkeyPressed -= _onToggleAppVisibility;
            _hotkeyService.OnMediaShowCurrentTrackPressed -= _onMediaShowCurrentTrack;
            _hotkeyService.OnMediaPlayPausePressed -= _onMediaPlayPause;
            _hotkeyService.OnMediaNextTrackPressed -= _onMediaNextTrack;
            _hotkeyService.OnMediaPrevTrackPressed -= _onMediaPreviousTrack;
            _hotkeyService.OnMuteMicPressed -= _onMuteMic;
            _hotkeyService.OnMuteSoundPressed -= _onMuteSound;
            _hotkeyService.OnDeafenPressed -= _onDeafen;
            _hotkeyService.OnListenToInputPressed -= _onListenToInput;
            _hotkeyService.OnMasterVolumeUpPressed -= _onMasterVolumeUp;
            _hotkeyService.OnMasterVolumeDownPressed -= _onMasterVolumeDown;
            _hotkeyService.OnMicVolumeUpPressed -= _onMicVolumeUp;
            _hotkeyService.OnMicVolumeDownPressed -= _onMicVolumeDown;
            _hotkeyService.OnInputSwitchHotkeyPressed -= _onInputSwitch;
            _hotkeyService.OnOutputSwitchHotkeyPressed -= _onOutputSwitch;
            _hotkeyService.OnInputReverseSwitchHotkeyPressed -= _onInputReverseSwitch;
            _hotkeyService.OnOutputReverseSwitchHotkeyPressed -= _onOutputReverseSwitch;

            _wired = false;
        }
    }
}
