namespace AudioPilot.Services.UI;

public enum OverlayDeviceKind
{
    Output = 0,
    Input = 1,
    Error = 2,
}

/// <summary>Presentation metadata; icons never infer playback or mute state from display text.</summary>
public enum OverlayIcon
{
    None,
    Speaker,
    SpeakerMuted,
    Microphone,
    MicrophoneMuted,
    Play,
    Pause,
    Next,
    Previous,
    SeekForward,
    SeekBackward,
    Media,
    Information,
    Warning,
}
