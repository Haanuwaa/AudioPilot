using AudioPilot.Logging;

namespace AudioPilot.Coordinators
{
    internal readonly record struct MuteStateChangePlan(
        bool DeviceMuteMicrophone,
        bool DeviceMutePlayback,
        bool NewDeafen,
        bool NewMuteMic,
        bool NewMuteSound,
        LogLevel LogLevel,
        string LogMessage,
        IReadOnlyList<string> PropertyNamesToNotify);

    internal static class AppMuteStateCoordinator
    {
        public static MuteStateChangePlan ResolveDeafenChange(bool value)
        {
            return new MuteStateChangePlan(
                DeviceMuteMicrophone: value,
                DeviceMutePlayback: value,
                NewDeafen: value,
                NewMuteMic: false,
                NewMuteSound: false,
                LogLevel: LogLevel.Info,
                LogMessage: value
                    ? "Deafening (muting/unmuting both mic and sound)"
                    : "Undeafening (muting/unmuting both mic and sound)",
                PropertyNamesToNotify: [nameof(ViewModels.AppViewModel.MuteMic), nameof(ViewModels.AppViewModel.MuteSound), nameof(ViewModels.AppViewModel.Deafen)]);
        }

        public static MuteStateChangePlan ResolveMuteMicChange(bool value, bool deafenValue, bool currentMuteSound)
        {
            return new MuteStateChangePlan(
                DeviceMuteMicrophone: value || deafenValue,
                DeviceMutePlayback: currentMuteSound || deafenValue,
                NewDeafen: deafenValue,
                NewMuteMic: value,
                NewMuteSound: currentMuteSound,
                LogLevel: LogLevel.Trace,
                LogMessage: $"Microphone mute changed to {value}",
                PropertyNamesToNotify: [nameof(ViewModels.AppViewModel.MuteMic)]);
        }

        public static MuteStateChangePlan ResolveMuteSoundChange(bool value, bool deafenValue, bool currentMuteMic)
        {
            return new MuteStateChangePlan(
                DeviceMuteMicrophone: currentMuteMic || deafenValue,
                DeviceMutePlayback: value || deafenValue,
                NewDeafen: deafenValue,
                NewMuteMic: currentMuteMic,
                NewMuteSound: value,
                LogLevel: LogLevel.Trace,
                LogMessage: $"Playback mute changed to {value}",
                PropertyNamesToNotify: [nameof(ViewModels.AppViewModel.MuteSound)]);
        }
    }
}
