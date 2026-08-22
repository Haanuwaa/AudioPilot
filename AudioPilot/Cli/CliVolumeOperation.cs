using AudioPilot.Models;

namespace AudioPilot.Cli;

/// <summary>Shared endpoint selection, adjustment policy and results; callers own endpoint lifetime and UI projection.</summary>
internal static class CliVolumeOperation
{
    internal delegate bool ReadVolume(out float percent, out bool muted);
    internal delegate bool WriteVolume(float percent, out float appliedPercent, out bool muted);

    internal static bool TryResolve(bool playback, string selector, Func<List<CycleDevice>> devices, out string? id, out string? error)
    {
        id = null;
        error = null;
        CliDeviceSelectorQuery query = CliDeviceSelectorResolver.Decode(selector);
        if (string.IsNullOrWhiteSpace(query.Value)) return true;
        CliDeviceSelectorResolution resolution = CliDeviceSelectorResolver.ResolveExact(devices(), selector);
        if (resolution.Success && resolution.Device != null)
        {
            id = resolution.Device.Id;
            return true;
        }
        if (query.Kind == CliDeviceSelectorKind.ExactId)
        {
            id = query.Value;
            return true;
        }
        string kind = playback ? "output" : "input";
        error = resolution.Ambiguous
            ? CliDeviceSelectorResolver.BuildAmbiguousMessage(kind, resolution.Selector, resolution.Matches)
            : CliDeviceSelectorResolver.BuildNotFoundMessage(kind, resolution.Selector);
        return false;
    }

    internal static (bool Success, string Output) Execute(bool playback, string? id, float? requested, bool relative,
        bool json, bool redact, ReadVolume read, WriteVolume write, Func<string> failureMessage, Action<float, bool>? project = null)
    {
        string kind = playback ? "master" : "mic";
        string failure = requested == null ? "volume-get-failed" : relative ? "volume-adjust-failed" : "volume-set-failed";
        (bool, string) Fail(string message) => (false, CliOutputFormatter.FormatVolumeError(kind, failure, message, json, id, redact));
        if (requested == null)
            return read(out float current, out bool muted) && float.IsFinite(current)
                ? (true, CliOutputFormatter.FormatVolumeResult(kind, current, muted, json, "volume-get-success", id, redact))
                : Fail(failureMessage());

        float target = requested.Value;
        if (!float.IsFinite(target) || (relative ? target is < -100f or > 100f : target is < 0f or > 100f))
            return Fail("Volume is outside the supported range.");
        float previous = 0f;
        if (relative)
        {
            if (string.IsNullOrWhiteSpace(id) || !read(out previous, out bool previouslyMuted) || !float.IsFinite(previous))
                return Fail("Failed to read a valid volume for adjustment.");
            if (target == 0f)
                return (true, CliOutputFormatter.FormatVolumeAdjustment(kind, previous, previous, previouslyMuted, json, id, redact));
            target = Math.Clamp(previous + target, 0f, 100f);
        }
        if (!write(target, out float applied, out bool isMuted)) return Fail(failureMessage());
        project?.Invoke(applied, isMuted);
        return (true, relative
            ? CliOutputFormatter.FormatVolumeAdjustment(kind, previous, applied, isMuted, json, id, redact)
            : CliOutputFormatter.FormatVolumeResult(kind, applied, isMuted, json, "volume-set-success", id, redact));
    }
}
