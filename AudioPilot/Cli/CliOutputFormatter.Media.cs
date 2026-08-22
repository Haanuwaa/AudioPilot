namespace AudioPilot.Cli
{
    public static partial class CliOutputFormatter
    {
        public static string FormatListenStatus(bool? currentInputListenEnabled, string? listenMonitorTargetOutputDeviceName, bool jsonOutput, bool redactOutput = false)
        {
            var status = new CliListenStatusSnapshot(currentInputListenEnabled, FormatOptionalDeviceName(listenMonitorTargetOutputDeviceName, redactOutput));

            if (jsonOutput)
            {
                return SerializeCliJson(status);
            }

            return $"listen to input: {FormatListenState(status.CurrentInputListenEnabled)}{Environment.NewLine}" +
                   $"listen monitor target output: {FormatMonitorTarget(status.ListenMonitorTargetOutputDeviceName)}";
        }

        public static string FormatMediaStatus(MediaOverlaySessionSnapshot snapshot, bool jsonOutput, bool redactOutput = false)
        {
            bool hasSession = !MediaOverlayEngine.IsSessionMissing(snapshot);
            var status = new CliMediaStatusSnapshot(
                hasSession,
                hasSession ? snapshot.PlaybackStatus?.ToString() : null,
                hasSession ? FormatOptionalMediaField(snapshot.Title, redactOutput) : null,
                hasSession ? FormatOptionalMediaField(snapshot.Artist, redactOutput) : null,
                hasSession ? FormatOptionalMediaField(snapshot.AlbumTitle, redactOutput) : null,
                hasSession ? FormatOptionalMediaSource(snapshot.SourceAppUserModelId, redactOutput) : null,
                hasSession ? snapshot.PositionSeconds : null);

            if (jsonOutput)
            {
                return SerializeCliJson(status);
            }

            if (!status.HasSession)
            {
                return "No current media";
            }

            var lines = new List<string>
            {
                $"playback status: {status.PlaybackStatus ?? "Unknown"}",
                $"title: {FormatOptionalOutput(status.Title)}",
                $"artist: {FormatOptionalOutput(status.Artist)}",
                $"album: {FormatOptionalOutput(status.AlbumTitle)}",
                $"source: {FormatOptionalOutput(status.SourceAppUserModelId)}",
                $"position seconds: {(status.PositionSeconds.HasValue ? status.PositionSeconds.Value.ToString() : "<unknown>")}",
            };

            return string.Join(Environment.NewLine, lines);
        }
    }
}
