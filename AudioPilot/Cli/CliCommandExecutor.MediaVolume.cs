using System.Globalization;

namespace AudioPilot.Cli
{
    public static partial class CliCommandExecutor
    {
        private static async Task<CliExecutionResult> ExecuteMediaCommandAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (command.Action == CliAction.MediaStatus)
            {
                return new CliExecutionResult(
                    0,
                    await runtime.GetMediaStatusAsync(command.JsonOutput, command.RedactOutput));
            }

            return command.Action switch
            {
                CliAction.MediaPlayPause => ExecuteNoOutput(runtime.MediaPlayPause),
                CliAction.MediaNextTrack => ExecuteNoOutput(runtime.MediaNextTrack),
                CliAction.MediaPreviousTrack => ExecuteNoOutput(runtime.MediaPreviousTrack),
                _ => BuildErrorResult(2, "unsupported-media-command", "Unsupported media command.", command.JsonOutput),
            };
        }

        private static async Task<CliExecutionResult> ExecuteMediaAndVolumeCommandAsync(CliCommand command, ICliCommandRuntime runtime)
        {
            if (command.Action is CliAction.SwitchOutput or CliAction.SwitchInput)
            {
                return await ExecuteSwitchAsync(command, runtime, output: command.Action == CliAction.SwitchOutput);
            }

            return command.Action switch
            {
                CliAction.MuteMicToggle => ExecuteMuteCommand(command, runtime, runtime.ToggleMuteMic(), "mic", "mute-mic-toggle-failed", "Failed to toggle microphone mute."),
                CliAction.MuteMicOn => ExecuteMuteCommand(command, runtime, runtime.SetMuteMic(true), "mic", "mute-mic-set-failed", "Failed to set microphone mute."),
                CliAction.MuteMicOff => ExecuteMuteCommand(command, runtime, runtime.SetMuteMic(false), "mic", "mute-mic-set-failed", "Failed to set microphone mute."),
                CliAction.MuteSoundToggle => ExecuteMuteCommand(command, runtime, runtime.ToggleMuteSound(), "sound", "mute-sound-toggle-failed", "Failed to toggle playback mute."),
                CliAction.MuteSoundOn => ExecuteMuteCommand(command, runtime, runtime.SetMuteSound(true), "sound", "mute-sound-set-failed", "Failed to set playback mute."),
                CliAction.MuteSoundOff => ExecuteMuteCommand(command, runtime, runtime.SetMuteSound(false), "sound", "mute-sound-set-failed", "Failed to set playback mute."),
                CliAction.DeafenToggle => ExecuteMuteCommand(command, runtime, runtime.ToggleDeafen(), "deafen", "deafen-toggle-failed", "Failed to toggle deafen."),
                CliAction.DeafenOn => ExecuteMuteCommand(command, runtime, runtime.SetDeafen(true), "deafen", "deafen-set-failed", "Failed to set deafen."),
                CliAction.DeafenOff => ExecuteMuteCommand(command, runtime, runtime.SetDeafen(false), "deafen", "deafen-set-failed", "Failed to set deafen."),
                CliAction.ListenToggle => ExecuteListenCommand(command, runtime, () => runtime.ToggleListenToInput(), "listen-toggle-failed", "Failed to toggle input listen state."),
                CliAction.ListenOn => ExecuteListenCommand(command, runtime, () => runtime.SetListenToInput(true), "listen-set-failed", "Failed to enable input listen state."),
                CliAction.ListenOff => ExecuteListenCommand(command, runtime, () => runtime.SetListenToInput(false), "listen-set-failed", "Failed to disable input listen state."),
                CliAction.VolumeGetMaster => ExecuteVolumeRead(command, runtime, playback: true),
                CliAction.VolumeGetMic => ExecuteVolumeRead(command, runtime, playback: false),
                CliAction.VolumeSetMaster => ExecuteVolumeWrite(command, runtime, playback: true),
                CliAction.VolumeSetMic => ExecuteVolumeWrite(command, runtime, playback: false),
                _ => BuildErrorResult(2, "unsupported-audio-command", "Unsupported audio command.", command.JsonOutput),
            };
        }

        private static CliExecutionResult ExecuteNoOutput(Action action)
        {
            action();
            return new CliExecutionResult(0);
        }

        private static CliExecutionResult ExecuteListenCommand(
            CliCommand command,
            ICliCommandRuntime runtime,
            Func<bool> execute,
            string errorCode,
            string errorMessage)
        {
            return execute()
                ? new CliExecutionResult(0, runtime.GetListenStatus(command.JsonOutput, command.RedactOutput))
                : BuildErrorResult(3, errorCode, errorMessage, command.JsonOutput);
        }

        private static CliExecutionResult ExecuteVolumeRead(CliCommand command, ICliCommandRuntime runtime, bool playback)
        {
            var (success, output) = runtime.GetVolume(playback, command.Key, command.JsonOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }

        private static CliExecutionResult ExecuteVolumeWrite(CliCommand command, ICliCommandRuntime runtime, bool playback)
        {
            if (string.IsNullOrWhiteSpace(command.Value))
            {
                return BuildErrorResult(2, "missing-volume-percent", "Missing volume percent.", command.JsonOutput);
            }

            if (!float.TryParse(command.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float percent) || percent < 0f || percent > 100f)
            {
                return BuildErrorResult(2, "invalid-volume-percent", "Invalid volume percent. Use a number between 0 and 100.", command.JsonOutput);
            }

            var (success, output) = runtime.SetVolume(playback, command.Key, percent, command.JsonOutput);
            return new CliExecutionResult(success ? 0 : 3, output);
        }

        private static async Task<CliExecutionResult> ExecuteSwitchAsync(CliCommand command, ICliCommandRuntime runtime, bool output)
        {
            string kind = output ? "output" : "input";
            if (!string.IsNullOrWhiteSpace(command.Key))
            {
                string? current = runtime.GetCurrentDeviceId(output);
                if (!string.Equals(current, command.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return BuildErrorResult(5, "require-current-mismatch", $"Current {kind} device does not match --require-current value.", command.JsonOutput);
                }
            }

            if (command.DryRun)
            {
                var (canSwitch, previewOutput) = runtime.PreviewSwitch(output, command.Reverse, command.JsonOutput, command.RedactOutput);
                return new CliExecutionResult(canSwitch ? 0 : 5, previewOutput);
            }

            bool switched = output
                ? await runtime.SwitchOutputAsync(command.MuteMic, command.MuteSound, command.Deafen, command.Reverse)
                : await runtime.SwitchInputAsync(command.Reverse);
            if (!switched)
            {
                return BuildErrorResult(5, $"{kind}-switch-precondition", $"{char.ToUpperInvariant(kind[0])}{kind[1..]} switch precondition failed.", command.JsonOutput);
            }

            if (!command.JsonOutput)
            {
                return new CliExecutionResult(0);
            }

            return new CliExecutionResult(0, CliOutputFormatter.SerializeCliJson(new
            {
                Success = true,
                Kind = kind,
                command.Reverse,
                DryRun = false,
                DiagCode = "switch-success",
            }));
        }

        private static CliExecutionResult ExecuteMuteCommand(CliCommand command, ICliCommandRuntime runtime, bool success, string target, string errorCode, string errorMessage)
        {
            if (!success)
            {
                return BuildErrorResult(3, errorCode, errorMessage, command.JsonOutput);
            }

            return command.JsonOutput
                ? new CliExecutionResult(0, runtime.GetMuteStatus(target, jsonOutput: true))
                : new CliExecutionResult(0);
        }
    }
}
