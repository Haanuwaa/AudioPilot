using AudioPilot.Logging;

namespace AudioPilot.Services.Audio
{
    internal sealed class RegistryPerAppAudioRoutingResetter : IPerAppAudioRoutingResetter
    {
        internal const string PropertyStorePath = @"Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore";

        private readonly Logger _logger;
        private readonly IUserRegistryAccessor _registry;
        private readonly string _propertyStorePath;

        public RegistryPerAppAudioRoutingResetter(Logger logger)
            : this(CurrentUserRegistryAccessor.Instance, PropertyStorePath, logger)
        {
        }

        internal RegistryPerAppAudioRoutingResetter(IUserRegistryAccessor registry, string propertyStorePath, Logger logger)
        {
            _registry = registry;
            _propertyStorePath = propertyStorePath;
            _logger = logger;
        }

        public PerAppAudioRoutingResetResult TryResetAll()
        {
            bool hadAssignments = false;
            try
            {
                hadAssignments = _registry.HasValuesOrSubKeys(_propertyStorePath);
                if (!hadAssignments)
                {
                    _logger.Info("RegistryPerAppAudioRoutingResetter", "No persisted per-app audio assignments found to reset");
                    return new PerAppAudioRoutingResetResult(Success: true, HadAssignments: false);
                }

                _registry.DeleteSubKeyTree(_propertyStorePath);
                _logger.Info("RegistryPerAppAudioRoutingResetter", "Cleared persisted per-app audio assignments from Windows policy store");
                return new PerAppAudioRoutingResetResult(Success: true, HadAssignments: true);
            }
            catch (Exception ex)
            {
                _logger.Warning("RegistryPerAppAudioRoutingResetter", "Failed to clear persisted per-app audio assignments", nameof(TryResetAll), ex);
                return new PerAppAudioRoutingResetResult(Success: false, HadAssignments: hadAssignments);
            }
        }
    }
}
