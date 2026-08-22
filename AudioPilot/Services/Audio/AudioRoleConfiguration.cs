using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Services.Audio
{
    internal sealed class AudioRoleConfiguration(
        IReadOnlyList<NRole> defaultOutputRoles,
        IReadOnlyList<NRole> defaultInputRoles)
    {
        private readonly Lock _lock = new();
        private NRole[] _outputRoles = [.. defaultOutputRoles];
        private NRole[] _inputRoles = [.. defaultInputRoles];

        public (NRole[] outputRoles, NRole[] inputRoles) Update(IEnumerable<string>? outputRoles, IEnumerable<string>? inputRoles, IReadOnlyList<NRole> defaultOutputRoles, IReadOnlyList<NRole> defaultInputRoles)
        {
            var normalizedOutput = NormalizeConfiguredRoles(outputRoles, defaultOutputRoles);
            var normalizedInput = NormalizeConfiguredRoles(inputRoles, defaultInputRoles);

            lock (_lock)
            {
                _outputRoles = normalizedOutput;
                _inputRoles = normalizedInput;
            }

            return (normalizedOutput, normalizedInput);
        }

        public NRole[] GetOutputRolesSnapshot()
        {
            lock (_lock)
            {
                return [.. _outputRoles];
            }
        }

        public NRole[] GetInputRolesSnapshot()
        {
            lock (_lock)
            {
                return [.. _inputRoles];
            }
        }

        internal static NRole[] NormalizeConfiguredRoles(IEnumerable<string>? configuredRoles, IReadOnlyList<NRole> fallback)
        {
            if (configuredRoles == null)
            {
                return [.. fallback];
            }

            List<NRole> normalized = [];
            foreach (var roleValue in configuredRoles)
            {
                if (string.IsNullOrWhiteSpace(roleValue))
                {
                    continue;
                }

                string trimmed = roleValue.Trim();
                if (trimmed.Equals("Console", StringComparison.OrdinalIgnoreCase))
                {
                    if (!normalized.Contains(NRole.Console))
                    {
                        normalized.Add(NRole.Console);
                    }
                }
                else if (trimmed.Equals("Multimedia", StringComparison.OrdinalIgnoreCase))
                {
                    if (!normalized.Contains(NRole.Multimedia))
                    {
                        normalized.Add(NRole.Multimedia);
                    }
                }
                else if (trimmed.Equals("Communications", StringComparison.OrdinalIgnoreCase))
                {
                    if (!normalized.Contains(NRole.Communications))
                    {
                        normalized.Add(NRole.Communications);
                    }
                }
            }

            return normalized.Count > 0 ? [.. normalized] : [.. fallback];
        }

        internal static NRole ResolveDetectionRole(IReadOnlyList<NRole> configuredRoles, NRole fallback)
        {
            return configuredRoles.Count > 0 ? configuredRoles[0] : fallback;
        }

        internal static void ApplyConfiguredRoles(string targetDeviceId, IReadOnlyList<NRole> roles)
        {
            foreach (var role in roles)
            {
                AudioPolicyConfig.SetDefaultDeviceOnCurrentThread(targetDeviceId, role);
            }
        }

        internal static void ApplyConfiguredRole(string targetDeviceId, NRole role)
        {
            AudioPolicyConfig.SetDefaultDeviceOnCurrentThread(targetDeviceId, role);
        }
    }
}
