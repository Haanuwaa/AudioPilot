using AudioPilot.Logging;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioDeviceRoleConfigurationHelper(
        AudioRoleConfiguration roleConfiguration,
        Logger logger)
    {
        private readonly AudioRoleConfiguration _roleConfiguration = roleConfiguration;
        private readonly Logger _logger = logger;

        public (NRole[] OutputRoles, NRole[] InputRoles) UpdateConfiguration(
            IEnumerable<string>? outputRoles,
            IEnumerable<string>? inputRoles,
            IReadOnlyList<NRole> defaultOutputRoles,
            IReadOnlyList<NRole> defaultInputRoles)
        {
            var (normalizedOutput, normalizedInput) = _roleConfiguration.Update(
                outputRoles,
                inputRoles,
                defaultOutputRoles,
                defaultInputRoles);

            _logger.Info(
                "AudioDeviceService",
                $"Updated role configuration - Output: [{string.Join(", ", normalizedOutput)}], Input: [{string.Join(", ", normalizedInput)}]");

            return (normalizedOutput, normalizedInput);
        }

        public NRole[] GetOutputRolesSnapshot()
        {
            return _roleConfiguration.GetOutputRolesSnapshot();
        }

        public NRole[] GetInputRolesSnapshot()
        {
            return _roleConfiguration.GetInputRolesSnapshot();
        }
    }
}
