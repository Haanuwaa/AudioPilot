using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio;

/// <summary>Shared endpoint volume reads, writes, and step policy; callers retain device ownership.</summary>
internal static class EndpointVolumeOperations
{
    internal static float ComputeSteppedVolumePercent(float currentPercent, int stepPercent, bool increase)
    {
        float normalizedCurrent = Math.Clamp(currentPercent, 0f, 100f);
        int normalizedStep = NormalizeVolumeStepPercent(stepPercent);
        float delta = increase ? normalizedStep : -normalizedStep;
        return Math.Clamp(normalizedCurrent + delta, 0f, 100f);
    }

    internal static bool TryGetEndpointVolumeState(Logger logger, MMDevice? device, string reason, out float currentPercent, out bool muted)
    {
        currentPercent = 0f;
        muted = false;

        if (device == null)
        {
            return false;
        }

        if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
        {
            return false;
        }

        currentPercent = Math.Clamp(endpointVolume.MasterVolumeLevelScalar * 100f, 0f, 100f);
        muted = endpointVolume.Mute;
        return true;
    }

    internal static bool TryApplyEndpointVolume(Logger logger, MMDevice? device, float targetPercent, string reason, bool muteAtZero, bool unmuteAboveZero, out float appliedPercent)
    {
        appliedPercent = 0f;
        if (device == null)
        {
            return false;
        }

        if (!AudioDeviceHelper.TryGetEndpointVolume(logger, device, out var endpointVolume, reason))
        {
            return false;
        }

        bool applied = TryApplyEndpointVolume(endpointVolume, targetPercent, muteAtZero, unmuteAboveZero, out appliedPercent);

        return applied;
    }

    internal static bool TryApplyEndpointVolume(AudioEndpointVolume endpointVolume, float targetPercent, bool muteAtZero, bool unmuteAboveZero, out float appliedPercent)
    {
        bool? previousMute = !muteAtZero && !unmuteAboveZero ? endpointVolume.Mute : null;
        appliedPercent = Math.Clamp(targetPercent, 0f, 100f);
        endpointVolume.MasterVolumeLevelScalar = appliedPercent / 100f;
        if (previousMute.HasValue && endpointVolume.Mute != previousMute.Value)
            endpointVolume.Mute = previousMute.Value;

        if (muteAtZero && appliedPercent <= 0f)
        {
            endpointVolume.Mute = true;
        }
        else if (unmuteAboveZero && appliedPercent > 0f)
        {
            endpointVolume.Mute = false;
        }

        return true;
    }

    internal static int NormalizeVolumeStepPercent(int stepPercent)
    {
        return stepPercent < 1 ? 5 : Math.Clamp(stepPercent, 1, 100);
    }
}
