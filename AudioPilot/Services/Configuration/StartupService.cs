using System.IO;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.UpdateProtocol;

namespace AudioPilot.Services.Configuration
{
    internal readonly record struct StartupRegistrationSnapshot(
        bool RunExists, bool RunMatches, bool TaskExists, bool TaskMatches, bool TaskEnabled, bool Available)
    {
        internal bool Matches(bool useScheduledTask) => useScheduledTask ? TaskMatches && !RunExists : RunMatches;
    }

    internal enum StartupRegistrationOutcome
    {
        Applied,
        SchedulerUnavailable,
    }

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
            if (ApplyRegistration(false, false, startupRegistryOpId: startupRegistryOpId, allowIncompleteDisable: true)
                == StartupRegistrationOutcome.SchedulerUnavailable)
            {
                throw new InvalidOperationException("The Windows Run entry is off, but AudioPilot could not check or remove its scheduled task.");
            }
        }

        /// <summary>
        /// Changes startup registration and optionally persists settings. An explicit disable can still remove a
        /// verified Run entry and save the disabled preference when Task Scheduler is unavailable. A settings
        /// write failure rolls back registration changes.
        /// </summary>
        internal StartupRegistrationOutcome ApplyRegistration(bool enabled, bool useScheduledTask, Action? persistSettings = null,
            string? startupRegistryOpId = null, bool preserveDisabledTask = false, bool allowIncompleteDisable = false)
        {
            if (DiagnosticSession.IsActive)
            {
                if (enabled) throw new InvalidOperationException("Startup registration is disabled in diagnostic sessions.");
                persistSettings?.Invoke();
                return StartupRegistrationOutcome.Applied;
            }
            string opId = startupRegistryOpId ?? $"startup:{Guid.NewGuid():N}";
            lock (RegistrationLock)
            {
                object? previousRun = null;
                string? previousTask = null;
                Exception? schedulerFailure = null;
                bool runTouched = false;
                bool taskTouched = false;
                try
                {
                    string? executable = enabled ? GetStartupExecutablePath() : null;
                    previousRun = _registry.GetValue(_startupRegistryPath, _startupValueName);
                    bool CanDisableRunIndependently() =>
                        !enabled && allowIncompleteDisable && (previousRun == null || RunMatchesExecutable(previousRun));
                    try { previousTask = _taskStore.Read(); }
                    catch (Exception ex) when (CanDisableRunIndependently()) { schedulerFailure = ex; }
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
                            try { _taskStore.Delete(); }
                            catch (Exception ex) when (CanDisableRunIndependently()) { schedulerFailure = ex; }
                        }
                        if (!enabled && previousRun != null)
                        {
                            runTouched = true;
                            _registry.DeleteValue(_startupRegistryPath, _startupValueName);
                        }
                    }
                    persistSettings?.Invoke();
                    if (schedulerFailure != null)
                    {
                        _logger.Warning("StartupService", () => $"startup-disable-incomplete | opId={opId} reason=task-scheduler-unavailable",
                            nameof(ApplyRegistration), schedulerFailure);
                        return StartupRegistrationOutcome.SchedulerUnavailable;
                    }
                    _logger.Info("StartupService", () => $"startup-registration-applied | opId={opId} enabled={enabled} mode={(!enabled ? "disabled" : useScheduledTask ? "scheduled-task" : "run-key")}");
                    return StartupRegistrationOutcome.Applied;
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

        internal StartupRegistrationSnapshot ReadRegistration(bool skipTaskIfRunMatches, string? startupRegistryOpId)
        {
            if (DiagnosticSession.IsActive) return new(false, false, false, false, false, Available: true);

            lock (RegistrationLock)
            {
                try
                {
                    object? run = _registry.GetValue(_startupRegistryPath, _startupValueName);
                    bool runMatches = run != null && RunMatchesExecutable(run);
                    if (skipTaskIfRunMatches && runMatches)
                        return new(true, true, false, false, false, Available: true);

                    string? task = _taskStore.Read();
                    if (task == null) return new(run != null, runMatches, false, false, false, Available: true);
                    bool taskMatches = StartupTaskDefinition.Matches(task, GetStartupExecutablePath(), _taskStore.UserSid, includeDisabled: true);
                    return new(run != null, runMatches, true, taskMatches, StartupTaskDefinition.IsEnabled(task), Available: true);
                }
                catch (Exception ex)
                {
                    _logger.Warning("StartupService", () => $"startup-registration-check-failed | opId={startupRegistryOpId}", nameof(ReadRegistration), ex);
                    return default;
                }
            }
        }

        public bool IsInStartup(string? startupRegistryOpId = null)
        {
            if (DiagnosticSession.IsActive) return false;
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
            if (DiagnosticSession.IsActive) return false;
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
                if (value == null) return false;
                bool matches = RunMatchesExecutable(value);
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

        private bool RunMatchesExecutable(object value) =>
            value is string command && TryParseStartupCommand(command, out string path)
            && string.Equals(Path.GetFullPath(path), GetStartupExecutablePath(), StringComparison.OrdinalIgnoreCase);

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
            StartupRegistrationSnapshot registration = ReadRegistration(!useScheduledTask, startupRegistryOpId);
            if (!registration.Available || registration.Matches(useScheduledTask) || (!registration.RunExists && !registration.TaskExists)) return;

            _logger.Info("StartupService", () => $"validate-startup-path-update | opId={startupRegistryOpId}");
            _logger.Trace("StartupService", () => $"{AppConstants.Audio.LogEvents.Startup.ValidateStartupPathValues} | opId={startupRegistryOpId}");
            AddToStartup(startupRegistryOpId, useScheduledTask, preserveDisabledTask: true);
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

            return PortableLayout.GetLaunchExecutable(fullPath);
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
