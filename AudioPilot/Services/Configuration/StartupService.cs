using System.IO;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Services.Configuration
{
    public class StartupService
    {
        private static readonly Lock RegistrationLock = new();
        private readonly IStartupTaskStore _taskStore;
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
            string? startupExecutablePath = null,
            IStartupTaskStore? taskStore = null)
        {
            _logger = logger ?? Logger.Instance;
            _registry = registry;
            _startupRegistryPath = startupRegistryPath;
            _startupValueName = startupValueName;
            _startupExecutablePath = startupExecutablePath;
            _taskStore = taskStore ?? new WindowsStartupTaskStore(startupValueName);
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

        public void AddToStartup(string? startupRegistryOpId = null, bool useScheduledTask = false, bool preserveDisabledTask = false)
        {
            ApplyRegistration(true, useScheduledTask, startupRegistryOpId: startupRegistryOpId, preserveDisabledTask: preserveDisabledTask);
        }

        public void RemoveFromStartup(string? startupRegistryOpId = null)
        {
            ApplyRegistration(false, false, startupRegistryOpId: startupRegistryOpId);
        }

        /// <summary>
        /// Changes the startup mechanism and optionally persists settings as one operation. If either step fails,
        /// restores the previous task and Run value, including a manually disabled task.
        /// </summary>
        internal void ApplyRegistration(bool enabled, bool useScheduledTask, Action? persistSettings = null, string? startupRegistryOpId = null, bool preserveDisabledTask = false)
        {
            string opId = startupRegistryOpId ?? $"startup:{Guid.NewGuid():N}";
            lock (RegistrationLock)
            {
                object? previousRun = null;
                string? previousTask = null;
                bool runTouched = false;
                bool taskTouched = false;
                try
                {
                    string? executable = enabled ? GetStartupExecutablePath() : null;
                    previousRun = _registry.GetValue(_startupRegistryPath, _startupValueName);
                    previousTask = _taskStore.Read();
                    if (enabled && useScheduledTask)
                    {
                        bool taskEnabled = !preserveDisabledTask || previousTask == null || StartupTaskDefinition.IsEnabled(previousTask);
                        string definition = StartupTaskDefinition.Create(executable!, _taskStore.UserSid, taskEnabled);
                        if (!StartupTaskDefinition.Matches(previousTask, executable!, _taskStore.UserSid, includeDisabled: preserveDisabledTask))
                        {
                            taskTouched = true;
                            _taskStore.Write(definition);
                        }
                        if (previousRun != null)
                        {
                            runTouched = true;
                            _registry.DeleteValue(_startupRegistryPath, _startupValueName);
                        }
                    }
                    else
                    {
                        if (enabled)
                        {
                            _logger.Info("StartupService", () => $"add-startup-start | opId={opId}");
                            _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.AddStartupPath} | opId={opId} exeFile={Path.GetFileName(executable)}");
                            string command = BuildStartupCommand(executable!);
                            if (!string.Equals(previousRun as string, command, StringComparison.Ordinal))
                            {
                                runTouched = true;
                                _registry.SetValue(_startupRegistryPath, _startupValueName, command);
                            }
                        }
                        if (previousTask != null)
                        {
                            taskTouched = true;
                            _taskStore.Delete();
                        }
                        if (!enabled && previousRun != null)
                        {
                            runTouched = true;
                            _registry.DeleteValue(_startupRegistryPath, _startupValueName);
                        }
                    }
                    persistSettings?.Invoke();
                    _logger.Info("StartupService", () => $"startup-registration-applied | opId={opId} enabled={enabled} mode={(!enabled ? "disabled" : useScheduledTask ? "scheduled-task" : "run-key")}");
                }
                catch (Exception ex)
                {
                    List<Exception> failures = [ex];
                    if (taskTouched)
                    {
                        try
                        {
                            if (previousTask == null) _taskStore.Delete();
                            else _taskStore.Write(previousTask);
                        }
                        catch (Exception rollbackException) { failures.Add(rollbackException); }
                    }
                    if (runTouched)
                    {
                        try
                        {
                            if (previousRun == null) _registry.DeleteValue(_startupRegistryPath, _startupValueName);
                            else _registry.SetValue(_startupRegistryPath, _startupValueName, previousRun);
                        }
                        catch (Exception rollbackException) { failures.Add(rollbackException); }
                    }
                    _logger.Error("StartupService", () => $"startup-registration-failed | opId={opId} rollbackSucceeded={failures.Count == 1}", nameof(ApplyRegistration), ex);
                    if (failures.Count > 1) throw new AggregateException("Startup registration and restoration failed.", failures);
                    throw;
                }
            }
        }

        public bool IsInStartup(string? startupRegistryOpId = null)
        {
            try
            {
                return _registry.GetValue(_startupRegistryPath, _startupValueName) != null || _taskStore.Read() != null;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", () => $"startup-registration-check-failed | opId={startupRegistryOpId}", nameof(IsInStartup), ex);
                return false;
            }
        }

        public bool IsInStartupWithValidPath(string? startupRegistryOpId = null, bool? useScheduledTask = null, bool includeDisabled = false)
        {
            try
            {
                if (!useScheduledTask.HasValue)
                {
                    return IsInStartupWithValidPath(startupRegistryOpId, false, includeDisabled)
                        || IsInStartupWithValidPath(startupRegistryOpId, true, includeDisabled);
                }
                if (useScheduledTask.Value)
                {
                    return StartupTaskDefinition.Matches(_taskStore.Read(), GetStartupExecutablePath(), _taskStore.UserSid, includeDisabled)
                        && _registry.GetValue(_startupRegistryPath, _startupValueName) == null;
                }
                object? value = _registry.GetValue(_startupRegistryPath, _startupValueName);
                if (value == null || !TryParseStartupCommand(value.ToString() ?? string.Empty, out string path)) return false;
                bool matches = string.Equals(Path.GetFullPath(path), GetStartupExecutablePath(), StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    _logger.Warning("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.IsInStartupValidPath} | {FormatStartupRegistryOpIdPrefix(startupRegistryOpId)}result=false reason=path-mismatch");
                }
                return matches;
            }
            catch (Exception ex)
            {
                _logger.Error("StartupService", "Failed to validate startup registration", nameof(IsInStartupWithValidPath), ex);
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

        public void ValidateAndUpdateStartupPath(string? startupRegistryOpId = null, bool useScheduledTask = false)
        {
            if (IsInStartupWithValidPath(startupRegistryOpId, useScheduledTask, includeDisabled: true)) return;
            if (IsInStartup(startupRegistryOpId))
            {
                _logger.Info("StartupService", () => $"validate-startup-path-update | opId={startupRegistryOpId}");
                _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.ValidateStartupPathValues} | opId={startupRegistryOpId}");
                AddToStartup(startupRegistryOpId, useScheduledTask, preserveDisabledTask: true);
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

        private string GetStartupExecutablePath()
        {
            string? path = _startupExecutablePath ?? Environment.ProcessPath;

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

        private static string FormatStartupRegistryOpIdPrefix(string? startupRegistryOpId)
        {
            return string.IsNullOrWhiteSpace(startupRegistryOpId)
                ? string.Empty
                : $"opId={startupRegistryOpId} ";
        }
    }
}
