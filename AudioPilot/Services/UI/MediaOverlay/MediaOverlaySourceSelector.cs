using AudioPilot.Logging;
using SessionSnapshot = AudioPilot.Services.UI.MediaOverlay.MediaOverlaySessionSnapshot;

namespace AudioPilot.Services.UI.MediaOverlay
{
    internal sealed class MediaOverlaySourceSelector
    {
        public static string? SelectPreferredSourceForSampling(
            string? baselineSource,
            bool baselineUsable,
            string? postCommandCurrentSource,
            bool postCommandCurrentIsUsableAndPlaying,
            string? commandTargetSource,
            string? stickySource,
            string? recoveredSource,
            MediaOverlayCommandPolicy commandPolicy)
        {
            if (commandPolicy.AllowRecoveredSourceOverride
                && !string.IsNullOrWhiteSpace(commandTargetSource)
                && !string.Equals(commandTargetSource, baselineSource, StringComparison.OrdinalIgnoreCase))
            {
                return commandTargetSource;
            }

            if (commandPolicy.AllowRecoveredSourceOverride
                && !string.IsNullOrWhiteSpace(recoveredSource)
                && !string.Equals(recoveredSource, baselineSource, StringComparison.OrdinalIgnoreCase))
            {
                return recoveredSource;
            }

            if (commandPolicy.AllowRecoveredSourceOverride
                && !string.IsNullOrWhiteSpace(stickySource)
                && !string.Equals(stickySource, baselineSource, StringComparison.OrdinalIgnoreCase))
            {
                return stickySource;
            }

            if (commandPolicy.PreferBaselineSourceForSampling && baselineUsable && !string.IsNullOrWhiteSpace(baselineSource))
            {
                return baselineSource;
            }

            if (postCommandCurrentIsUsableAndPlaying && !string.IsNullOrWhiteSpace(postCommandCurrentSource))
            {
                return postCommandCurrentSource;
            }

            if (baselineUsable && !string.IsNullOrWhiteSpace(baselineSource))
            {
                return baselineSource;
            }

            if (!string.IsNullOrWhiteSpace(stickySource))
            {
                return stickySource;
            }

            return baselineSource;
        }

        public static SessionSnapshot ResolveEffectiveBaselineForSampling(
            SessionSnapshot baseline,
            string? preferredSourceForCommand,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots,
            MediaOverlayCommandPolicy commandPolicy)
        {
            if (!commandPolicy.AllowRecoveredSourceOverride
                || string.IsNullOrWhiteSpace(preferredSourceForCommand)
                || string.Equals(preferredSourceForCommand, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return baseline;
            }

            return preCommandSnapshots.TryGetValue(preferredSourceForCommand, out SessionSnapshot preferredBaseline)
                && !MediaOverlayEngine.IsSessionMissing(preferredBaseline)
                ? preferredBaseline
                : baseline;
        }

        public static string? SelectPreferredSourceForPlayPauseSampling(string? baselineSource, string? stickySource)
        {
            if (!string.IsNullOrWhiteSpace(baselineSource))
            {
                return baselineSource;
            }

            return string.IsNullOrWhiteSpace(stickySource)
                ? null
                : stickySource;
        }

        public static string? ValidateStickySourceForSampling(
            string? stickySource,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots)
        {
            if (string.IsNullOrWhiteSpace(stickySource))
            {
                return null;
            }

            return IsCorroboratedSourceForSampling(
                    stickySource,
                    baseline,
                    postCommandCurrent,
                    preCommandSnapshots,
                    includePostCommandMatch: true)
                ? stickySource
                : null;
        }

        public static string? ValidateStickySourceForPlayPauseSampling(
            string? stickySource,
            SessionSnapshot baseline,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots)
        {
            if (string.IsNullOrWhiteSpace(stickySource))
            {
                return null;
            }

            return IsCorroboratedSourceForSampling(
                    stickySource,
                    baseline,
                    SessionSnapshot.Empty,
                    preCommandSnapshots,
                    includePostCommandMatch: false)
                ? stickySource
                : null;
        }

        public static bool IsCorroboratedSourceForSampling(
            string? source,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots,
            bool includePostCommandMatch)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            bool seenBeforeCommand = preCommandSnapshots.ContainsKey(source);
            bool matchesBaseline = string.Equals(
                source,
                baseline.SourceAppUserModelId,
                StringComparison.OrdinalIgnoreCase);
            bool matchesPostCommand = includePostCommandMatch
                && string.Equals(source, postCommandCurrent.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);

            return seenBeforeCommand || matchesBaseline || matchesPostCommand;
        }

        public static string DescribeStickySourceValidation(
            string? stickySource,
            SessionSnapshot baseline,
            SessionSnapshot postCommandCurrent,
            IReadOnlyDictionary<string, SessionSnapshot> preCommandSnapshots)
        {
            bool seenBeforeCommand = IsCorroboratedSourceForSampling(stickySource, baseline, SessionSnapshot.Empty, preCommandSnapshots, includePostCommandMatch: false)
                && !string.Equals(stickySource, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
            bool matchesBaseline = !string.IsNullOrWhiteSpace(stickySource)
                && string.Equals(stickySource, baseline.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
            bool matchesPostCommand = !string.IsNullOrWhiteSpace(stickySource)
                && string.Equals(stickySource, postCommandCurrent.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);

            return $"sticky={LogPrivacy.Id(stickySource)} seenBeforeCommand={seenBeforeCommand} matchesBaseline={matchesBaseline} matchesPostCommand={matchesPostCommand}";
        }
    }
}
