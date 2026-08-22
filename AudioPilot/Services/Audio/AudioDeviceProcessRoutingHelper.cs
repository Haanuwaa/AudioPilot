using System.Diagnostics;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceProcessRoutingHelper(Logger logger, IProcessAudioRouter processAudioRouter, int deferredLogEvery)
    {
        private readonly Logger _logger = logger;
        private readonly IProcessAudioRouter _processAudioRouter = processAudioRouter;
        private readonly int _deferredLogEvery = deferredLogEvery;

        public ProcessAudioDeviceSwitchResult ApplyProcessDeviceRouting(
            uint processId,
            DataFlow flow,
            string targetDeviceId,
            string targetDeviceName,
            Func<Role[]> getRoles,
            string logScope,
            string operationName,
            string opId,
            Func<string, string, int?> getDeferredLogOccurrence,
            Action<string, string> resetDeferredLogCount)
        {
            string pid = FormatProcessIdForLog(processId);
            string target = LogPrivacy.Device(targetDeviceName);
            long startTimestamp = Stopwatch.GetTimestamp();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AudioDeviceService", () => $"{logScope}-started | opId={opId} flow={flow} pid={pid} target={target}");
            }

            try
            {
                ProcessAudioRoutingResult result = _processAudioRouter.TrySetProcessDevice(processId, flow, targetDeviceId, getRoles());
                double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                if (result == ProcessAudioRoutingResult.Failed)
                {
                    _logger.Warning("AudioDeviceService", () => $"{logScope}-failed | opId={opId} flow={flow} pid={pid} target={target} elapsedMs={elapsedMs:F1}");
                    return new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null);
                }

                if (result == ProcessAudioRoutingResult.DeferredNoAudio)
                {
                    int? occurrence = getDeferredLogOccurrence(logScope, opId);
                    if (occurrence.HasValue)
                    {
                        _logger.Info("AudioDeviceService", () => $"{logScope}-deferred | opId={opId} flow={flow} pid={pid} target={target} reason=no-audio-session occurrence={occurrence.Value} sampleEvery={_deferredLogEvery} elapsedMs={elapsedMs:F1}");
                    }
                }
                else
                {
                    resetDeferredLogCount(logScope, opId);
                    _logger.Info("AudioDeviceService", () => $"{logScope}-success | opId={opId} flow={flow} pid={pid} target={target} elapsedMs={elapsedMs:F1}");
                }

                return new ProcessAudioDeviceSwitchResult(result, targetDeviceName);
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{logScope}-failed | opId={opId} flow={flow} pid={pid} target={target} elapsedMs={Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F1}", operationName, ex);
                return new ProcessAudioDeviceSwitchResult(ProcessAudioRoutingResult.Failed, null);
            }
        }

        public bool TryResetProcessDeviceRouting(uint processId, DataFlow flow, IReadOnlyList<Role> roles, string logScope, string opId, string operationName)
        {
            string pid = FormatProcessIdForLog(processId);
            long startTimestamp = Stopwatch.GetTimestamp();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.Debug("AudioDeviceService", () => $"{logScope}-started | opId={opId} flow={flow} pid={pid} target=reset");
            }

            try
            {
                ProcessAudioRoutingResult result = _processAudioRouter.TryClearProcessDevice(processId, flow, roles);
                double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                if (result == ProcessAudioRoutingResult.Failed)
                {
                    _logger.Warning("AudioDeviceService", () => $"{logScope}-failed | opId={opId} flow={flow} pid={pid} target=reset elapsedMs={elapsedMs:F1}");
                    return false;
                }

                if (result == ProcessAudioRoutingResult.DeferredNoAudio)
                {
                    _logger.Info("AudioDeviceService", () => $"{logScope}-deferred | opId={opId} flow={flow} pid={pid} target=reset reason=no-audio-session elapsedMs={elapsedMs:F1}");
                    return true;
                }

                _logger.Info("AudioDeviceService", () => $"{logScope}-success | opId={opId} flow={flow} pid={pid} target=reset elapsedMs={elapsedMs:F1}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("AudioDeviceService", () => $"{logScope}-failed | opId={opId} flow={flow} pid={pid} target=reset elapsedMs={Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F1}", operationName, ex);
                return false;
            }
        }

        private static string FormatProcessIdForLog(uint processId)
        {
            return LogPrivacy.Id(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
