using System.Diagnostics;
using System.IO;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Configuration
{
    public class StartupService
    {
        private readonly Logger _logger;
        private readonly IUserRegistryAccessor _registry;
        private readonly string _startupRegistryPath;
        private readonly string _startupValueName;
        private readonly string? _startupExecutablePath;

        public StartupService()
            : this(AppConstants.Registry.StartupPath, AppConstants.Identity.AppName)
        {
        }

        internal StartupService(string startupRegistryPath, string startupValueName)
            : this(startupRegistryPath, startupValueName, logger: null)
        {
        }

        internal StartupService(string startupRegistryPath, string startupValueName, Logger? logger)
            : this(startupRegistryPath, startupValueName, logger, CurrentUserRegistryAccessor.Instance)
        {
        }

        internal StartupService(
            string startupRegistryPath,
            string startupValueName,
            Logger? logger,
            IUserRegistryAccessor registry,
            string? startupExecutablePath = null)
        {
            _logger = logger ?? Logger.Instance;
            _registry = registry;
            _startupRegistryPath = startupRegistryPath;
            _startupValueName = startupValueName;
            _startupExecutablePath = startupExecutablePath;
        }

        internal static StartupService CreateForExecutable(string startupExecutablePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(startupExecutablePath);
            return new StartupService(
                AppConstants.Registry.StartupPath,
                AppConstants.Identity.AppName,
                logger: null,
                CurrentUserRegistryAccessor.Instance,
                startupExecutablePath);
        }

        public void AddToStartup(string? startupRegistryOpId = null)
        {
            string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? $"startup-registry:{Guid.NewGuid():N}"
                : startupRegistryOpId;
            _logger.Info("StartupService", () => $"add-startup-start | opId={opId}");

            try
            {
                string exePath = GetStartupExecutablePath();
                _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupPath} | opId={opId} exeFile={GetFileNameForLog(exePath)}");
                string expectedValue = BuildStartupCommand(exePath);
                object? currentValue = _registry.GetValue(_startupRegistryPath, _startupValueName);

                if (currentValue == null)
                {
                    _registry.SetValue(_startupRegistryPath, _startupValueName, expectedValue);
                    _logger.Info("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupSuccess} | opId={opId} mode=create");
                    _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupValue} | opId={opId} {DescribeStartupValueForLog(expectedValue)}");
                }
                else if (currentValue.ToString() != expectedValue)
                {
                    _logger.Info("StartupService", () => $"add-startup-update | opId={opId}");
                    _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupUpdateValues} | opId={opId} old={DescribeStartupValueForLog(currentValue?.ToString())} new={DescribeStartupValueForLog(expectedValue)}");
                    _registry.SetValue(_startupRegistryPath, _startupValueName, expectedValue);
                }
                else
                {
                    _logger.Debug("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupSkip} | opId={opId} reason=already-matching");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("StartupService", "Access denied when adding to startup registry", nameof(AddToStartup), ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to add to startup registry", nameof(AddToStartup), ex);
                throw;
            }
        }

        public void RemoveFromStartup(string? startupRegistryOpId = null)
        {
            string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? $"startup-registry:{Guid.NewGuid():N}"
                : startupRegistryOpId;
            _logger.Info("StartupService", () => $"remove-startup-start | opId={opId}");

            try
            {
                if (_registry.GetValue(_startupRegistryPath, _startupValueName) != null)
                {
                    _registry.DeleteValue(_startupRegistryPath, _startupValueName);
                    _logger.Info("StartupService", () => $"remove-startup-success | opId={opId}");
                }
                else
                {
                    _logger.Debug("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.RemoveStartupSkip} | opId={opId} reason=entry-missing");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("StartupService", "Access denied when removing from startup registry", nameof(RemoveFromStartup), ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to remove from startup registry", nameof(RemoveFromStartup), ex);
                throw;
            }
        }

        public bool IsInStartup(string? _startupRegistryOpId = null)
        {
            try
            {
                _ = _startupRegistryOpId;
                object? value = _registry.GetValue(_startupRegistryPath, _startupValueName);
                return value != null;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to check startup registry", nameof(IsInStartup), ex);
                return false;
            }
        }

        public bool IsInStartupWithValidPath(string? startupRegistryOpId = null)
        {
            try
            {
                string opIdPrefix = FormatStartupRegistryOpIdPrefix(startupRegistryOpId);
                object? value = _registry.GetValue(_startupRegistryPath, _startupValueName);
                if (value == null)
                {
                    _logger.Debug("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | {opIdPrefix}result=false reason=entry-missing");
                    return false;
                }

                string registryValue = value.ToString() ?? string.Empty;
                if (!TryParseStartupCommand(registryValue, out string registryExePath))
                {
                    _logger.Warning("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | {opIdPrefix}result=false reason=invalid-command");
                    return false;
                }

                string currentExePath = GetStartupExecutablePath();

                bool pathMatches = string.Equals(
                    System.IO.Path.GetFullPath(registryExePath),
                    System.IO.Path.GetFullPath(currentExePath),
                    StringComparison.OrdinalIgnoreCase);

                if (pathMatches)
                {
                    return true;
                }
                else
                {
                    _logger.Warning("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | {opIdPrefix}result=false reason=path-mismatch");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to check startup registry with path validation", nameof(IsInStartupWithValidPath), ex);
                return false;
            }
        }

        private static bool TryParseStartupCommand(string registryValue, out string executablePath)
        {
            executablePath = string.Empty;
            string trimmed = registryValue.Trim();
            string arguments;

            if (trimmed.StartsWith('"'))
            {
                int endQuote = trimmed.IndexOf('\"', 1);
                if (endQuote <= 1)
                {
                    return false;
                }

                executablePath = trimmed[1..endQuote];
                arguments = trimmed[(endQuote + 1)..].Trim();
                return string.Equals(arguments, "-startup", StringComparison.OrdinalIgnoreCase);
            }

            int spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex <= 0)
            {
                return false;
            }

            executablePath = trimmed[..spaceIndex];
            arguments = trimmed[(spaceIndex + 1)..].Trim();
            return string.Equals(arguments, "-startup", StringComparison.OrdinalIgnoreCase);
        }

        public void ValidateAndUpdateStartupPath(string? startupRegistryOpId = null)
        {
            try
            {
                string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                    ? $"startup-registry:{Guid.NewGuid():N}"
                    : startupRegistryOpId;
                string exePath = GetStartupExecutablePath();

                object? currentValue = _registry.GetValue(_startupRegistryPath, _startupValueName);
                if (currentValue != null)
                {
                    string expectedValue = BuildStartupCommand(exePath);
                    string existingValue = currentValue.ToString() ?? string.Empty;
                    if (existingValue != expectedValue)
                    {
                        _logger.Info("StartupService", () => $"validate-startup-path-update | opId={opId}");
                        _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.ValidateStartupPathValues} | opId={opId} old={DescribeStartupValueForLog(existingValue)} new={DescribeStartupValueForLog(expectedValue)}");
                        _registry.SetValue(_startupRegistryPath, _startupValueName, expectedValue);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("StartupService", "Failed to validate or update startup path", nameof(ValidateAndUpdateStartupPath), ex);
            }
        }

        public void RemoveIfPresent(string? startupRegistryOpId = null)
        {
            if (IsInStartup(startupRegistryOpId))
            {
                string opId = string.IsNullOrWhiteSpace(startupRegistryOpId)
                    ? $"startup-registry:{Guid.NewGuid():N}"
                    : startupRegistryOpId;
                _logger.Info("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.RemoveIfPresent} | opId={opId} action=remove");
                RemoveFromStartup(opId);
            }
        }

        private static string GetFileNameForLog(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "<empty>";
            }

            return System.IO.Path.GetFileName(path);
        }

        private string GetStartupExecutablePath()
        {
            string? path = _startupExecutablePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Environment.ProcessPath;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("The AudioPilot startup executable path is unavailable.");
            }

            string fullPath = System.IO.Path.GetFullPath(path);
            if (fullPath.Contains('"', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The AudioPilot startup executable path contains an invalid quote character.");
            }

            if (_startupExecutablePath != null && !File.Exists(fullPath))
            {
                throw new FileNotFoundException("The AudioPilot startup executable does not exist.", fullPath);
            }

            return fullPath;
        }

        private static string BuildStartupCommand(string executablePath)
        {
            return $"\"{executablePath}\" -startup";
        }

        private static string DescribeStartupValueForLog(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "valueState=empty";
            }

            string trimmed = value.Trim();
            bool hasStartupArg = trimmed.Contains("-startup", StringComparison.OrdinalIgnoreCase);
            return $"valueState=present length={trimmed.Length} hasStartupArg={hasStartupArg}";
        }

        private static string FormatStartupRegistryOpIdPrefix(string? startupRegistryOpId)
        {
            return string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? string.Empty
                : $"opId={startupRegistryOpId} ";
        }
    }
}
