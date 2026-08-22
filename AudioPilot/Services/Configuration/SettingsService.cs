using System.IO;
using System.Security.Cryptography;
using System.Text;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AudioPilot.Services.Configuration
{
    public class SettingsService
    {
        private string _primarySettingsPath;
        private string _primaryDevicesPath;
        private string _fallbackSettingsPath;
        private string _fallbackDevicesPath;
        private string _activeSettingsPath;
        private string _activeDevicesPath;
        private bool _useFallback;
        private readonly string _settingsMutexName;
        private readonly Logger _logger;
        private string? _lastLoadUserWarning;

        public string? LastLoadUserWarning => _lastLoadUserWarning;

        public SettingsService()
            : this(null, null)
        {
        }

        public SettingsService(string? primaryBaseDir, string? fallbackBaseDir)
            : this(primaryBaseDir, fallbackBaseDir, logger: null)
        {
        }

        internal SettingsService(string? primaryBaseDir, string? fallbackBaseDir, Logger? logger)
        {
            _logger = logger ?? Logger.Instance;
            string baseDir = string.IsNullOrWhiteSpace(primaryBaseDir)
                ? AppDataPaths.GetPrimaryDataRoot()
                : primaryBaseDir;
            _primarySettingsPath = Path.Combine(baseDir, AppConstants.Files.SettingsFileName);
            _primaryDevicesPath = Path.Combine(baseDir, "DEVICES.txt");

            string fallbackRoot = string.IsNullOrWhiteSpace(fallbackBaseDir)
                ? AppDataPaths.GetFallbackDataRoot()
                : fallbackBaseDir;
            _fallbackSettingsPath = Path.Combine(fallbackRoot, AppConstants.Files.SettingsFileName);
            _fallbackDevicesPath = Path.Combine(fallbackRoot, "DEVICES.txt");

            _activeSettingsPath = _primarySettingsPath;
            _activeDevicesPath = _primaryDevicesPath;
            _settingsMutexName = BuildSettingsMutexName(_primarySettingsPath, _fallbackSettingsPath);

            _logger.Debug("SettingsService", "Initialized settings paths");
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.InitPaths} | primarySettingsPath=configured fallbackSettingsPath=configured");
            }
        }

        internal void OverrideWriteTargetsForTests(string primarySettingsPath, string fallbackSettingsPath, bool useFallback = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(primarySettingsPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSettingsPath);

            _primarySettingsPath = primarySettingsPath;
            _primaryDevicesPath = Path.Combine(Path.GetDirectoryName(primarySettingsPath) ?? string.Empty, "DEVICES.txt");
            _fallbackSettingsPath = fallbackSettingsPath;
            _fallbackDevicesPath = Path.Combine(Path.GetDirectoryName(fallbackSettingsPath) ?? string.Empty, "DEVICES.txt");
            _useFallback = useFallback;
            SetActivePath(useFallback ? _fallbackSettingsPath : _primarySettingsPath);
        }

        public string GetSettingsPath() => _activeSettingsPath;

        public Settings LoadSettings()
        {
            return ExecuteWithSettingsLock(() =>
            {
                _lastLoadUserWarning = null;

                string settingsPath = GetExistingSettingsPath() ?? _primarySettingsPath;
                SetActivePath(settingsPath);
                _useFallback = string.Equals(settingsPath, _fallbackSettingsPath, StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (!File.Exists(settingsPath))
                    {
                        _logger.Info("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.LoadDefaults} | reason=file-missing");
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.LoadDefaultsPath} | origin={(_useFallback ? "fallback" : "primary")}");
                        }

                        Settings? recoveredFromBackup = TryLoadSettingsFromAnyBackup(settingsPath);
                        if (recoveredFromBackup != null)
                        {
                            TryPersistSettingsOnLoad(recoveredFromBackup, "restore-missing-from-backup");
                            _lastLoadUserWarning = "Settings file was missing and the latest backup was restored.";
                            return recoveredFromBackup;
                        }

                        var defaults = new Settings();
                        TryPersistSettingsOnLoad(defaults, "create-defaults-on-missing");
                        _lastLoadUserWarning = null;
                        return defaults;
                    }

                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<Settings>(
                        json,
                        new JsonSerializerSettings
                        {
                            ObjectCreationHandling = ObjectCreationHandling.Replace,
                        }) ?? new Settings();

                    SettingsMigrationResult migrationResult = SettingsMigrationService.MigrateToCurrent(settings);
                    if (migrationResult.IsSourceSchemaNewerThanCurrent)
                    {
                        SettingsValidationService.EnsureRequiredStructure(settings);
                    }
                    else
                    {
                        SettingsValidationService.Normalize(settings);
                    }
                    if (!migrationResult.IsSourceSchemaNewerThanCurrent)
                    {
                        settings.ExtensionData?.Clear();
                    }

                    if (migrationResult.IsSourceSchemaNewerThanCurrent)
                    {
                        _logger.Warning(
                            "SettingsService",
                            () => $"settings-schema-newer-than-supported | source={migrationResult.OriginalSchemaVersion} supported={Settings.CurrentSchemaVersion} rewriteOnLoad=false");
                    }

                    CanonicalRewriteAnalysis rewriteAnalysis = AnalyzeCanonicalRewrite(json, settings);
                    bool shouldRewriteOnLoad =
                        !migrationResult.IsSourceSchemaNewerThanCurrent
                        && rewriteAnalysis.RequiresRewrite;

                    if (shouldRewriteOnLoad)
                    {
                        _logger.Info(
                            "SettingsService",
                            () => $"{AppConstants.Audio.LogEvents.Settings.SaveOnLoad} | reason=canonical-rewrite unknownKeys={rewriteAnalysis.UnknownKeyCount} missingKeys={rewriteAnalysis.MissingKeyCount} valueChanges={rewriteAnalysis.HasValueOnlyChanges}");

                        SaveSettingsInternal(settings);
                    }

                    _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.LoadSuccess} | outputCycleCount={settings.DeviceSwitching.Output.CycleDevices.Count} inputCycleCount={settings.DeviceSwitching.Input.CycleDevices.Count} runAtStartup={settings.RunAtStartup} theme={settings.Theme}");

                    return settings;
                }
                catch (JsonException ex)
                {
                    _logger.Error("SettingsService", "JSON deserialization error - file may be corrupted", nameof(LoadSettings), ex);
                    Settings? fallbackRecovered = TryLoadSettingsFromFallbackFile(settingsPath);
                    if (fallbackRecovered != null)
                    {
                        _lastLoadUserWarning = "Primary settings were invalid; fallback settings were loaded.";
                        return fallbackRecovered;
                    }

                    Settings? recovered = TryLoadSettingsFromAnyBackup(settingsPath);
                    if (recovered != null)
                    {
                        _lastLoadUserWarning = "Settings were recovered from backup because the main settings file was invalid.";
                        return recovered;
                    }

                    _lastLoadUserWarning = "Settings file could not be read and defaults were loaded.";
                    return new Settings();
                }
                catch (InvalidDataException ex)
                {
                    _logger.Error("SettingsService", "Settings file schema is unsupported", nameof(LoadSettings), ex);
                    Settings? fallbackRecovered = TryLoadSettingsFromFallbackFile(settingsPath);
                    if (fallbackRecovered != null)
                    {
                        _lastLoadUserWarning = "Primary settings used an unsupported schema; fallback settings were loaded.";
                        return fallbackRecovered;
                    }

                    Settings? recovered = TryLoadSettingsFromAnyBackup(settingsPath);
                    if (recovered != null)
                    {
                        _lastLoadUserWarning = "Settings were recovered from backup because the main settings schema was unsupported.";
                        return recovered;
                    }

                    _lastLoadUserWarning = "Settings schema was unsupported and defaults were loaded.";
                    return new Settings();
                }
                catch (IOException ex)
                {
                    _logger.Error("SettingsService", "IO error loading settings file", nameof(LoadSettings), ex);
                    Settings? fallbackRecovered = TryLoadSettingsFromFallbackFile(settingsPath);
                    if (fallbackRecovered != null)
                    {
                        _lastLoadUserWarning = "Primary settings could not be read; fallback settings were loaded.";
                        return fallbackRecovered;
                    }

                    Settings? recovered = TryLoadSettingsFromAnyBackup(settingsPath);
                    if (recovered != null)
                    {
                        _lastLoadUserWarning = "Settings were recovered from backup after a settings file read error.";
                        return recovered;
                    }

                    _lastLoadUserWarning = "Settings could not be read and defaults were loaded.";
                    return new Settings();
                }
                catch (Exception ex)
                {
                    _logger.Error("SettingsService", "Unexpected error loading settings", nameof(LoadSettings), ex);
                    _lastLoadUserWarning = "Settings could not be loaded and defaults were applied.";
                    return new Settings();
                }
            });
        }

        public void SaveSettings(Settings settings)
        {
            ExecuteWithSettingsLock(() =>
            {
                SaveSettingsInternal(settings);
                return 0;
            });
        }

        internal string ReadTextFileWithSettingsLock(string path)
        {
            return ExecuteWithSettingsLock(() => File.ReadAllText(path));
        }

        private void SaveSettingsInternal(Settings settings)
        {
            SettingsValidationService.Normalize(settings);
            _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveStart} | outputCycleCount={settings.DeviceSwitching.Output.CycleDevices.Count} inputCycleCount={settings.DeviceSwitching.Input.CycleDevices.Count} runAtStartup={settings.RunAtStartup} theme={settings.Theme}");
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                string targetPath = _useFallback ? _fallbackSettingsPath : _primarySettingsPath;
                _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveTargetPath} | target={GetSettingsFileLogTarget(targetPath)}");
            }

            try
            {
                string json = JsonConvert.SerializeObject(
                    settings,
                    Formatting.Indented);

                if (!_useFallback)
                {
                    try
                    {
                        WriteFileAtomic(_primarySettingsPath, json, createBackup: true);
                        SetActivePath(_primarySettingsPath);
                        _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSuccess} | mode=primary");
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSuccessPath} | target={GetSettingsFileLogTarget(_primarySettingsPath)}");
                        }
                        return;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.Warning(
                            "SettingsService",
                            () => $"settings-save-primary-access-denied-fallback | target={GetSettingsFileLogTarget(_primarySettingsPath)} error={ex.GetType().Name}",
                            nameof(SaveSettingsInternal),
                            ex);
                        _useFallback = true;
                    }
                    catch (IOException ex)
                    {
                        _logger.Warning(
                            "SettingsService",
                            () => $"settings-save-primary-io-fallback | target={GetSettingsFileLogTarget(_primarySettingsPath)} error={ex.GetType().Name}",
                            nameof(SaveSettingsInternal),
                            ex);
                        _useFallback = true;
                    }
                }

                WriteFileAtomic(_fallbackSettingsPath, json, createBackup: true);
                SetActivePath(_fallbackSettingsPath);
                _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSuccess} | mode=fallback");
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSuccessPath} | target={GetSettingsFileLogTarget(_fallbackSettingsPath)}");
                }
            }
            catch (IOException ex)
            {
                _logger.Error("SettingsService", "IO error saving settings file", nameof(SaveSettings), ex);
                throw new IOException("Failed to save settings.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("SettingsService", "Access denied when saving settings file", nameof(SaveSettings), ex);
                throw new IOException("Failed to save settings.", ex);
            }
            catch (Exception ex)
            {
                _logger.Error("SettingsService", "Unexpected error saving settings", nameof(SaveSettings), ex);
                throw new IOException("Failed to save settings.", ex);
            }
        }

        public bool SettingsFileExists()
        {
            string? settingsPath = GetExistingSettingsPath();
            bool exists = settingsPath != null;
            if (exists)
            {
                SetActivePath(settingsPath!);
                _useFallback = string.Equals(settingsPath, _fallbackSettingsPath, StringComparison.OrdinalIgnoreCase);
            }
            else if (HasBackupSettingsFile(_primarySettingsPath))
            {
                exists = true;
                _useFallback = false;
                SetActivePath(_primarySettingsPath);
            }
            else if (HasBackupSettingsFile(_fallbackSettingsPath))
            {
                exists = true;
                _useFallback = true;
                SetActivePath(_fallbackSettingsPath);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.ExistsCheck} | exists={exists} activeTarget={GetSettingsFileLogTarget(_activeSettingsPath)} useFallback={_useFallback}");
            }
            return exists;
        }

        public void GenerateDeviceReferenceFile(
            IEnumerable<MMDevice> outputDevices,
            IEnumerable<MMDevice> inputDevices,
            bool anonymizeIds = false)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[OUTPUT DEVICES]");
                sb.AppendLine();
                foreach (var device in outputDevices)
                {
                    sb.AppendLine($"{FormatDeviceReferenceId(device.ID, anonymizeIds)} | {device.FriendlyName}");
                }

                sb.AppendLine();
                sb.AppendLine("[INPUT DEVICES]");
                sb.AppendLine();
                foreach (var device in inputDevices)
                {
                    sb.AppendLine($"{FormatDeviceReferenceId(device.ID, anonymizeIds)} | {device.FriendlyName}");
                }

                WriteFileAtomic(_activeDevicesPath, sb.ToString(), createBackup: false);
                _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.GenerateDeviceReferenceSuccess} | target={GetDevicesFileLogTarget()}");
            }
            catch (Exception ex)
            {
                _logger.Warning("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.GenerateDeviceReferenceFailed} | target={GetDevicesFileLogTarget()}", nameof(GenerateDeviceReferenceFile), ex);
            }
        }

        public void GenerateDeviceReferenceFile(
            IEnumerable<CycleDevice> outputDevices,
            IEnumerable<CycleDevice> inputDevices,
            bool anonymizeIds = false)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[OUTPUT DEVICES]");
                sb.AppendLine();
                foreach (var device in outputDevices)
                {
                    if (device == null || string.IsNullOrWhiteSpace(device.Id))
                    {
                        continue;
                    }

                    sb.AppendLine($"{FormatDeviceReferenceId(device.Id, anonymizeIds)} | {device.Name}");
                }

                sb.AppendLine();
                sb.AppendLine("[INPUT DEVICES]");
                sb.AppendLine();
                foreach (var device in inputDevices)
                {
                    if (device == null || string.IsNullOrWhiteSpace(device.Id))
                    {
                        continue;
                    }

                    sb.AppendLine($"{FormatDeviceReferenceId(device.Id, anonymizeIds)} | {device.Name}");
                }

                WriteFileAtomic(_activeDevicesPath, sb.ToString(), createBackup: false);
                _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.GenerateDeviceReferenceSuccess} | target={GetDevicesFileLogTarget()}");
            }
            catch (Exception ex)
            {
                _logger.Warning("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.GenerateDeviceReferenceFailed} | target={GetDevicesFileLogTarget()}", nameof(GenerateDeviceReferenceFile), ex);
            }
        }

        private static string FormatDeviceReferenceId(string id, bool anonymizeIds)
        {
            if (!anonymizeIds)
            {
                return id;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            return $"sha256:{Convert.ToHexString(hash.AsSpan(0, 10))}";
        }

        private string GetDevicesFileLogTarget()
        {
            string fileName = GetFileNameForLog(_activeDevicesPath);
            string origin = GetPathOrigin(_activeDevicesPath, _fallbackDevicesPath);
            return $"{origin}:{fileName}";
        }

        private string GetSettingsFileLogTarget(string path)
        {
            string fileName = GetFileNameForLog(path);
            string origin = GetPathOrigin(path, _fallbackSettingsPath);
            return $"{origin}:{fileName}";
        }

        private static string GetPathOrigin(string path, string fallbackPath)
        {
            return string.Equals(path, fallbackPath, StringComparison.OrdinalIgnoreCase)
                ? "fallback"
                : "primary";
        }

        private static string GetFileNameForLog(string path)
        {
            string? fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? "<unknown>" : fileName;
        }

        public void DeleteSettingsFiles()
        {
            ExecuteWithSettingsLock(() =>
            {
                try
                {
                    if (File.Exists(_primarySettingsPath))
                        File.Delete(_primarySettingsPath);
                    DeleteSettingsArtifacts(_primarySettingsPath);
                }
                catch (Exception ex)
                {
                    _logger.Warning("SettingsService", () => $"Failed to delete primary settings file: {ex.GetType().Name}");
                }

                try
                {
                    if (File.Exists(_fallbackSettingsPath))
                        File.Delete(_fallbackSettingsPath);
                    DeleteSettingsArtifacts(_fallbackSettingsPath);
                }
                catch (Exception ex)
                {
                    _logger.Warning("SettingsService", () => $"Failed to delete fallback settings file: {ex.GetType().Name}");
                }

                _useFallback = false;
                SetActivePath(_primarySettingsPath);
                return 0;
            });
        }

        private string? GetExistingSettingsPath()
        {
            if (File.Exists(_primarySettingsPath))
                return _primarySettingsPath;
            if (File.Exists(_fallbackSettingsPath))
                return _fallbackSettingsPath;
            return null;
        }

        private Settings? TryLoadSettingsFromBackup(string failedPath)
        {
            foreach (string backupPath in EnumerateBackupCandidates(failedPath))
            {
                if (!File.Exists(backupPath))
                {
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(backupPath);
                    var settings = JsonConvert.DeserializeObject<Settings>(
                        json,
                        new JsonSerializerSettings
                        {
                            ObjectCreationHandling = ObjectCreationHandling.Replace,
                        });

                    settings = PrepareRecoveredSettings(settings, backupPath);
                    if (settings == null)
                    {
                        continue;
                    }

                    _logger.Warning("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.RecoveredFromBackup} | source={GetSettingsFileLogTarget(backupPath)}");
                    return settings;
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "SettingsService",
                        () => $"settings-backup-candidate-failed | source={GetSettingsFileLogTarget(backupPath)} error={ex.GetType().Name}",
                        nameof(TryLoadSettingsFromBackup),
                        ex);
                }
            }

            return null;
        }

        private Settings? TryLoadSettingsFromAnyBackup(string failedPath)
        {
            Settings? recovered = TryLoadSettingsFromBackup(failedPath);
            if (recovered != null)
            {
                return recovered;
            }

            string alternatePath = string.Equals(failedPath, _primarySettingsPath, StringComparison.OrdinalIgnoreCase)
                ? _fallbackSettingsPath
                : _primarySettingsPath;

            return TryLoadSettingsFromBackup(alternatePath);
        }

        private static bool HasBackupSettingsFile(string baseSettingsPath)
        {
            foreach (string backupPath in EnumerateBackupCandidates(baseSettingsPath))
            {
                if (File.Exists(backupPath))
                {
                    return true;
                }
            }

            return false;
        }

        private Settings? TryLoadSettingsFromFallbackFile(string failedPath)
        {
            if (!string.Equals(failedPath, _primarySettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!File.Exists(_fallbackSettingsPath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(_fallbackSettingsPath);
                var settings = JsonConvert.DeserializeObject<Settings>(
                    json,
                    new JsonSerializerSettings
                    {
                        ObjectCreationHandling = ObjectCreationHandling.Replace,
                    });

                settings = PrepareRecoveredSettings(settings, _fallbackSettingsPath);
                if (settings == null)
                {
                    return null;
                }

                _useFallback = true;
                SetActivePath(_fallbackSettingsPath);
                _logger.Warning("SettingsService", () => $"settings-load-fallback-used | reason=primary-load-failed target={GetSettingsFileLogTarget(_fallbackSettingsPath)}");
                return settings;
            }
            catch (Exception ex)
            {
                _logger.Warning("SettingsService", () => $"settings-load-fallback-failed | target={GetSettingsFileLogTarget(_fallbackSettingsPath)} error={ex.GetType().Name}", nameof(TryLoadSettingsFromFallbackFile), ex);
                return null;
            }
        }

        private void TryPersistSettingsOnLoad(Settings settings, string reason)
        {
            try
            {
                SaveSettingsInternal(settings);
                _logger.Info("SettingsService", () => $"settings-persisted-during-load | reason={reason} target={GetSettingsFileLogTarget(_activeSettingsPath)}");
            }
            catch (Exception ex)
            {
                _logger.Warning("SettingsService", () => $"settings-persist-during-load-failed | reason={reason} error={ex.GetType().Name}", nameof(TryPersistSettingsOnLoad), ex);
            }
        }

        private Settings? PrepareRecoveredSettings(Settings? settings, string sourcePath)
        {
            if (settings == null)
            {
                return null;
            }

            SettingsMigrationResult migrationResult = SettingsMigrationService.MigrateToCurrent(settings);
            if (migrationResult.IsSourceSchemaNewerThanCurrent)
            {
                SettingsValidationService.EnsureRequiredStructure(settings);
            }
            else
            {
                SettingsValidationService.Normalize(settings);
            }
            if (!migrationResult.IsSourceSchemaNewerThanCurrent)
            {
                settings.ExtensionData?.Clear();
            }

            if (migrationResult.IsSourceSchemaNewerThanCurrent)
            {
                _logger.Warning(
                    "SettingsService",
                    () => $"settings-recovery-schema-newer-than-supported | source={GetSettingsFileLogTarget(sourcePath)} supported={Settings.CurrentSchemaVersion} sourceSchema={migrationResult.OriginalSchemaVersion}");
            }

            return settings;
        }

        private void SetActivePath(string settingsPath)
        {
            _activeSettingsPath = settingsPath;
            _activeDevicesPath = string.Equals(settingsPath, _primarySettingsPath, StringComparison.OrdinalIgnoreCase)
                ? _primaryDevicesPath
                : _fallbackDevicesPath;
        }

        private void WriteFileAtomic(string path, string content, bool createBackup)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (createBackup && File.Exists(path) && HasUnchangedContent(path, content))
            {
                _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSkipped} | reason=content-unchanged target={GetSettingsFileLogTarget(path)}");
                return;
            }

            string fileName = Path.GetFileName(path);
            string tempPath = Path.Combine(directory ?? string.Empty, $"{fileName}.{Guid.NewGuid():N}.tmp");
            string backupPath = GetBackupPath(path);
            string? backupDirectory = Path.GetDirectoryName(backupPath);
            if (createBackup && !string.IsNullOrWhiteSpace(backupDirectory))
            {
                Directory.CreateDirectory(backupDirectory);
            }

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    if (createBackup)
                    {
                        RotateBackups(path, AppConstants.Files.SettingsBackupRetentionCount);
                        File.Replace(tempPath, path, backupPath, true);
                    }
                    else
                    {
                        File.Replace(tempPath, path, null, true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("SettingsService", () => $"Failed to delete temp settings file '{Path.GetFileName(tempPath)}': {cleanupEx.GetType().Name}");
                        }
                    }
                }
            }
        }

        private static bool HasUnchangedContent(string path, string nextContent)
        {
            try
            {
                string currentContent = File.ReadAllText(path);
                return string.Equals(currentContent, nextContent, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> EnumerateBackupCandidates(string path)
        {
            yield return GetBackupPath(path);

            for (int index = 1; index < AppConstants.Files.SettingsBackupRetentionCount; index++)
            {
                yield return GetBackupPath(path, index);
            }
        }

        private static void RotateBackups(string path, int retentionCount)
        {
            if (retentionCount <= 1)
            {
                return;
            }

            for (int index = retentionCount - 2; index >= 1; index--)
            {
                MoveIfExists(GetBackupPath(path, index), GetBackupPath(path, index + 1));
            }

            MoveIfExists(GetBackupPath(path), GetBackupPath(path, 1));
        }

        private static string GetBackupPath(string path, int? index = null)
        {
            string? directory = Path.GetDirectoryName(path);
            string fileName = Path.GetFileName(path);
            string backupDirectory = Path.Combine(directory ?? string.Empty, AppConstants.Files.BackupFolderName);

            return index is null
                ? Path.Combine(backupDirectory, fileName + ".bak")
                : Path.Combine(backupDirectory, fileName + $".bak.{index.Value}");
        }

        private static void MoveIfExists(string source, string destination)
        {
            if (!File.Exists(source))
            {
                return;
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }

        private static void DeleteSettingsArtifacts(string baseSettingsPath)
        {
            string? directory = Path.GetDirectoryName(baseSettingsPath);
            string backupDirectory = Path.Combine(directory ?? string.Empty, AppConstants.Files.BackupFolderName);
            foreach (string backupPath in EnumerateBackupCandidates(baseSettingsPath))
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }

            if (Directory.Exists(backupDirectory)
                && !Directory.EnumerateFileSystemEntries(backupDirectory).Any())
            {
                Directory.Delete(backupDirectory, recursive: false);
            }
        }

        private static CanonicalRewriteAnalysis AnalyzeCanonicalRewrite(string originalJson, Settings settings)
        {
            try
            {
                string canonicalJson = JsonConvert.SerializeObject(
                    settings,
                    Formatting.Indented);

                if (string.Equals(originalJson, canonicalJson, StringComparison.Ordinal))
                {
                    return CanonicalRewriteAnalysis.None;
                }

                JToken originalToken = JToken.Parse(originalJson);
                JToken canonicalToken = JToken.Parse(canonicalJson);

                bool requiresRewrite = !JToken.DeepEquals(originalToken, canonicalToken);
                if (!requiresRewrite)
                {
                    return CanonicalRewriteAnalysis.None;
                }

                if (originalToken is not JObject originalObject || canonicalToken is not JObject canonicalObject)
                {
                    return new CanonicalRewriteAnalysis(
                        RequiresRewrite: true,
                        UnknownKeyCount: 0,
                        MissingKeyCount: 0,
                        HasValueOnlyChanges: true);
                }

                var originalKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in originalObject.Properties())
                {
                    originalKeys.Add(property.Name);
                }

                var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in canonicalObject.Properties())
                {
                    canonicalKeys.Add(property.Name);
                }

                int unknownKeyCount = 0;
                foreach (var key in originalKeys)
                {
                    if (!canonicalKeys.Contains(key))
                    {
                        unknownKeyCount++;
                    }
                }

                int missingKeyCount = 0;
                foreach (var key in canonicalKeys)
                {
                    if (!originalKeys.Contains(key))
                    {
                        missingKeyCount++;
                    }
                }

                return new CanonicalRewriteAnalysis(
                    RequiresRewrite: true,
                    UnknownKeyCount: unknownKeyCount,
                    MissingKeyCount: missingKeyCount,
                    HasValueOnlyChanges: unknownKeyCount == 0 && missingKeyCount == 0);
            }
            catch
            {
                return CanonicalRewriteAnalysis.None;
            }
        }

        private T ExecuteWithSettingsLock<T>(Func<T> operation)
        {
            bool lockAcquired = false;
            using var mutex = new Mutex(initiallyOwned: false, _settingsMutexName);

            try
            {
                try
                {
                    lockAcquired = mutex.WaitOne(AppConstants.Timing.SettingsIoCrossProcessLockTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    lockAcquired = true;
                    _logger.Warning("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SettingsLockAbandoned} | recovered=true");
                }

                if (!lockAcquired)
                {
                    _logger.Warning(
                        "SettingsService",
                        () => $"{AppConstants.Audio.LogEvents.Settings.SettingsLockTimeout} | timeoutMs={AppConstants.Timing.SettingsIoCrossProcessLockTimeoutMs}");
                    throw new IOException("Timed out waiting for settings file lock.");
                }

                return operation();
            }
            finally
            {
                if (lockAcquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static string BuildSettingsMutexName(string primarySettingsPath, string fallbackSettingsPath)
        {
            string input = $"{primarySettingsPath.Trim().ToUpperInvariant()}|{fallbackSettingsPath.Trim().ToUpperInvariant()}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            string suffix = Convert.ToHexString(hash)[..16];
            return $"Local\\AudioPilot.Settings.{suffix}";
        }

        private readonly record struct CanonicalRewriteAnalysis(
            bool RequiresRewrite,
            int UnknownKeyCount,
            int MissingKeyCount,
            bool HasValueOnlyChanges)
        {
            internal static CanonicalRewriteAnalysis None => new(
                RequiresRewrite: false,
                UnknownKeyCount: 0,
                MissingKeyCount: 0,
                HasValueOnlyChanges: false);
        }

    }
}
