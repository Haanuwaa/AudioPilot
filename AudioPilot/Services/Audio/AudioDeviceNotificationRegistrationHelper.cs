using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    /// <summary>
    /// Owns the NAudio 3 notification subscription and serializes register/unregister transitions without exposing
    /// NAudio's internal COM callback interface.
    /// </summary>
    internal sealed class AudioDeviceNotificationRegistrationHelper(
        Logger logger,
        Func<IDisposable> createNotificationSubscription,
        Action onRegistered,
        Action onUnregistered)
    {
        private const int Unregistered = 0;
        private const int Registering = 1;
        private const int Registered = 2;
        private const int Unregistering = 3;
        private const string RegisterMethodName = "RegisterNotificationClient";
        private const string UnregisterMethodName = "UnregisterNotificationClient";

        private readonly Logger _logger = logger;
        private readonly Func<IDisposable> _createNotificationSubscription = createNotificationSubscription;
        private readonly Action _onRegistered = onRegistered;
        private readonly Action _onUnregistered = onUnregistered;
        private IDisposable? _subscription;
        private int _state;

        public bool IsRegistered => Volatile.Read(ref _subscription) != null;

        internal bool IsUnregisteringForTests => Volatile.Read(ref _state) == Unregistering;

        public void Register()
        {
            if (Interlocked.CompareExchange(ref _state, Registering, Unregistered) != Unregistered)
            {
                return;
            }

            bool postRegistrationStarted = false;
            try
            {
                IDisposable subscription = _createNotificationSubscription()
                    ?? throw new InvalidOperationException("The notification subscription factory returned null.");
                Volatile.Write(ref _subscription, subscription);

                if (Volatile.Read(ref _state) != Registering)
                {
                    DisposeSubscription();
                    Volatile.Write(ref _state, Unregistered);
                    return;
                }

                postRegistrationStarted = true;
                _onRegistered();

                if (Interlocked.CompareExchange(ref _state, Registered, Registering) != Registering)
                {
                    DisposeSubscription();
                    Volatile.Write(ref _state, Unregistered);
                    RunPostUnregistration();
                    return;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Register} | success=true");
                }
            }
            catch (Exception ex)
            {
                DisposeSubscription();
                Volatile.Write(ref _state, Unregistered);
                if (postRegistrationStarted)
                {
                    RunPostUnregistration();
                }

                AudioDeviceHelper.LogException(_logger, RegisterMethodName, ex);
            }
        }

        public void Unregister()
        {
            while (true)
            {
                int state = Volatile.Read(ref _state);
                switch (state)
                {
                    case Unregistered:
                        return;
                    case Registering:
                        if (Interlocked.CompareExchange(ref _state, Unregistering, Registering) == Registering)
                        {
                            WaitForRegistrationCleanup();
                            return;
                        }

                        break;
                    case Registered:
                        if (Interlocked.CompareExchange(ref _state, Unregistering, Registered) == Registered)
                        {
                            UnregisterCore();
                            return;
                        }

                        break;
                    case Unregistering:
                        WaitForRegistrationCleanup();
                        return;
                }
            }
        }

        private void UnregisterCore()
        {
            try
            {
                DisposeSubscription();
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Unregister} | success=true");
                }
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, UnregisterMethodName, ex);
            }
            finally
            {
                Volatile.Write(ref _state, Unregistered);
                RunPostUnregistration();
            }
        }

        private void DisposeSubscription()
        {
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
        }

        private void RunPostUnregistration()
        {
            try
            {
                _onUnregistered();
            }
            catch (Exception ex)
            {
                AudioDeviceHelper.LogException(_logger, UnregisterMethodName, ex);
            }
        }

        private void WaitForRegistrationCleanup()
        {
            bool completed = SpinWait.SpinUntil(
                () => Volatile.Read(ref _state) == Unregistered,
                AppConstants.Timing.CleanupWaitMs);
            if (!completed)
            {
                _logger.Warning("AudioDeviceService", () => $"{AppConstants.Audio.LogEvents.DeviceNotifications.Unregister} | success=false reason=registration-cleanup-timeout");
            }
        }
    }
}
