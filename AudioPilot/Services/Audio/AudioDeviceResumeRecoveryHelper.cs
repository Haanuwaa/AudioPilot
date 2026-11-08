using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio;

internal sealed class AudioDeviceResumeRecoveryHelper(
    Logger logger,
    Func<bool> isDisposed,
    Func<bool> isRegistered,
    Action registerNotificationClient,
    Func<CancellationToken, Task<bool>> recoverSessionMonitoringAsync,
    int retryDelayMs = 200)
{
    internal async Task<bool> RecoverCallbacksAsync(CancellationToken cancellationToken)
    {
        bool notificationsReady = false;
        bool monitoringReady = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isDisposed()) return false;

            try
            {
                if (!isRegistered()) registerNotificationClient();
                notificationsReady = isRegistered();
            }
            catch (Exception ex)
            {
                logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.NotificationRegistrationFailed, nameof(RecoverCallbacksAsync), ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (isDisposed()) return false;

            try
            {
                if (!monitoringReady) monitoringReady = await recoverSessionMonitoringAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.Warning("AudioDeviceService", AppConstants.Audio.LogEvents.ResumeRecovery.SessionMonitoringFailed, nameof(RecoverCallbacksAsync), ex);
            }

            if (notificationsReady && monitoringReady) return true;
            if (attempt == 0)
            {
                logger.Debug("AudioDeviceService", $"resume-callback-recovery-retry | notificationsReady={notificationsReady} monitoringReady={monitoringReady}");
                await Task.Delay(retryDelayMs, cancellationToken);
            }
        }

        logger.Warning("AudioDeviceService", $"resume-callback-recovery-incomplete | notificationsReady={notificationsReady} monitoringReady={monitoringReady}");
        return false;
    }
}
