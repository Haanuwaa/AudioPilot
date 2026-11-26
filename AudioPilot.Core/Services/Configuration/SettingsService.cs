using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioPilot.Constants;
using AudioPilot.Localization;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Configuration
{
    public class SettingsService
    {
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private string _primarySettingsPath;
        private string _primaryDevicesPath;
        private string _fallbackSettingsPath;
        private string _fallbackDevicesPath;
        private string _activeSettingsPath;
        private string _activeDevicesPath;
        private bool _useFallback;
        private bool _useLocalDeviceReferences;
        private readonly string _settingsMutexName;
        private readonly Logger _logger;
        private LocalizedText? _lastLoadUserWarning;
        private readonly DeviceReferenceFileWriter _deviceReferenceWriter;
        private sealed record SaveAttempt(DateTimeOffset CompletedUtc, bool Succeeded, string? Error);
        private SaveAttempt? _lastSaveAttempt;

        internal SettingsHealthSnapshot CaptureHealth(bool autoSaveEnabled, bool savePending)
        {
            SaveAttempt? attempt = Volatile.Read(ref _lastSaveAttempt);
            return new(autoSaveEnabled, savePending, _useFallback, attempt?.CompletedUtc,
                attempt?.Succeeded, attempt?.Error, _lastLoadUserWarning != null);
        }

        internal void RecordSaveFailure(Exception error) =>
            Volatile.Write(ref _lastSaveAttempt, new(DateTimeOffset.UtcNow, false, error.GetType().Name));

        public string? LastLoadUserWarning => _lastLoadUserWarning == null ? null : LocalizationService.English.Format(_lastLoadUserWarning);
        internal LocalizedText? LastLoadWarningResource => _lastLoadUserWarning;

        public SettingsService()
            : this(null, null)
        {
        }

        public SettingsService(string? primaryBaseDir, string? fallbackBaseDir)
            : this(primaryBaseDir, fallbackBaseDir, logger: null)
        {
        }

        internal SettingsService(string? primaryBaseDir, string? fallbackBaseDir, Logger? logger, string? deviceReferenceRoot = null)
        {
            _logger = logger ?? Logger.Instance;
            _deviceReferenceWriter = new DeviceReferenceFileWriter((path, text) => WriteFileAtomic(path, text, createBackup: false));
            string baseDir = string.IsNullOrWhiteSpace(primaryBaseDir)
                ? AppDataPaths.GetPrimaryDataRoot()
                : primaryBaseDir;
            _primarySettingsPath = Path.Combine(baseDir, AppConstants.Files.SettingsFileName);
            if (deviceReferenceRoot == null && string.IsNullOrWhiteSpace(primaryBaseDir) && string.IsNullOrWhiteSpace(fallbackBaseDir)
                && !DiagnosticSession.IsActive && AppDataPaths.TryGetInstalledDataRoot(out _))
                deviceReferenceRoot = AppDataPaths.GetLocalStateRoot();
            _useLocalDeviceReferences = deviceReferenceRoot != null;
            _primaryDevicesPath = Path.Combine(deviceReferenceRoot ?? baseDir, AppConstants.Files.DeviceReferenceFileName);

            string fallbackRoot = string.IsNullOrWhiteSpace(fallbackBaseDir)
                ? AppDataPaths.GetFallbackDataRoot()
                : fallbackBaseDir;
            _fallbackSettingsPath = Path.Combine(fallbackRoot, AppConstants.Files.SettingsFileName);
            _fallbackDevicesPath = Path.Combine(deviceReferenceRoot ?? fallbackRoot, AppConstants.Files.DeviceReferenceFileName);

            _activeSettingsPath = _primarySettingsPath;
            _activeDevicesPath = _primaryDevicesPath;
            _settingsMutexName = BuildSettingsMutexName(_primarySettingsPath, _fallbackSettingsPath);
            if (HasFallbackLocationMarker())
            {
                _useFallback = true;
                SetActivePath(_fallbackSettingsPath);
            }

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
            _useLocalDeviceReferences = false;
            _primaryDevicesPath = Path.Combine(Path.GetDirectoryName(primarySettingsPath) ?? string.Empty, AppConstants.Files.DeviceReferenceFileName);
            _fallbackSettingsPath = fallbackSettingsPath;
            _fallbackDevicesPath = Path.Combine(Path.GetDirectoryName(fallbackSettingsPath) ?? string.Empty, AppConstants.Files.DeviceReferenceFileName);
            _useFallback = useFallback;
            SetActivePath(useFallback ? _fallbackSettingsPath : _primarySettingsPath);
        }

        public string GetSettingsPath() => _activeSettingsPath;

        private static Func<bool>? _updateStartupBlockedCheck;

        internal static void RegisterUpdateStartupBlockedCheck(Func<bool> check) => _updateStartupBlockedCheck = check;

        public Settings LoadSettings() => LoadSettingsCore(persistOnLoad: _updateStartupBlockedCheck?.Invoke() != true);

        /// <summary>Loads normalized or recovered settings in memory without creating or repairing settings files.</summary>
        internal Settings LoadSettingsReadOnly(bool allowRecovery = true) => LoadSettingsCore(persistOnLoad: false, allowRecovery);

        private Settings LoadSettingsCore(bool persistOnLoad, bool allowRecovery = true)
        {
            return ExecuteWithSettingsLock(() =>
            {
                _lastLoadUserWarning = null;

                string settingsPath = GetExistingSettingsPath() ?? (HasFallbackLocationMarker() ? _fallbackSettingsPath : _primarySettingsPath);
                SetActivePath(settingsPath);
                _useFallback = string.Equals(settingsPath, _fallbackSettingsPath, StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (!File.Exists(settingsPath))
                    {
                        if (!allowRecovery) throw SettingsFailure.WithPresentation(
                            new FileNotFoundException("Settings file is unavailable.", settingsPath), "Validation_SettingsUnavailable");
                        _logger.Info("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.LoadDefaults} | reason=file-missing");
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.LoadDefaultsPath} | origin={(_useFallback ? "fallback" : "primary")}");
                        }

                        Settings? recoveredFromBackup = TryLoadSettingsFromAnyBackup(settingsPath);
                        if (recoveredFromBackup != null)
                        {
                            if (persistOnLoad) TryPersistSettingsOnLoad(recoveredFromBackup, "restore-missing-from-backup");
                            _lastLoadUserWarning ??= new("Recovery_SettingsMissingRestored");
                            return recoveredFromBackup;
                        }

                        var defaults = new Settings();
                        if (persistOnLoad) TryPersistSettingsOnLoad(defaults, "create-defaults-on-missing");
                        _lastLoadUserWarning = null;
                        return defaults;
                    }

                    string json = ReadBoundedSettingsText(settingsPath);
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;
                    if (GetNewerSchemaVersion(root) is { } newerSchema)
                        return LoadNewerSettings(root, newerSchema, settingsPath, allowRecovery);

                    var settings = root.Deserialize<Settings>(SettingsJson.Options);
                    if (settings == null && !allowRecovery) throw SettingsFailure.WithPresentation(
                        new JsonException("Settings file must contain a settings object."), "Validation_SettingsObject");
                    settings ??= new Settings();

                    NormalizeCurrentSettings(settings);
                    CanonicalRewriteAnalysis rewriteAnalysis = persistOnLoad ? AnalyzeCanonicalRewrite(json, root, settings) : CanonicalRewriteAnalysis.None;
                    if (rewriteAnalysis.RequiresRewrite)
                    {
                        _logger.Info(
                            "SettingsService",
                            () => $"{AppConstants.Audio.LogEvents.Settings.SaveOnLoad} | reason=canonical-rewrite unknownKeys={rewriteAnalysis.UnknownKeyCount} missingKeys={rewriteAnalysis.MissingKeyCount} valueChanges={rewriteAnalysis.HasValueOnlyChanges}");

                        TryPersistSettingsOnLoad(settings, "canonical-rewrite");
                    }

                    _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.LoadSuccess} | outputCycleCount={settings.DeviceSwitching.Output.CycleDevices.Count} inputCycleCount={settings.DeviceSwitching.Input.CycleDevices.Count} runAtStartup={settings.Startup.Enabled} theme={settings.App.Theme}");

                    return settings;
                }
                catch (JsonException ex) when (allowRecovery)
                {
                    _logger.Error("SettingsService", "JSON deserialization error - file may be corrupted", nameof(LoadSettings), ex);
                    Settings? fallbackRecovered = TryLoadSettingsFromFallbackFile(settingsPath);
                    if (fallbackRecovered != null)
                    {
                        _lastLoadUserWarning ??= new("Recovery_SettingsInvalidFallback");
                        return fallbackRecovered;
                    }

                    Settings? recovered = TryLoadSettingsFromAnyBackup(settingsPath);
                    if (recovered != null)
                    {
                        _lastLoadUserWarning ??= new("Recovery_SettingsInvalidRestored");
                        return recovered;
                    }

                    _lastLoadUserWarning = new("Recovery_SettingsFileUnreadableDefaults");
                    return new Settings();
                }
                catch (JsonException ex) when (ex.GetType() != typeof(JsonException))
                {
                    throw new JsonException(ex.Message, ex.Path, ex.LineNumber, ex.BytePositionInLine, ex);
                }
                catch (InvalidDataException ex) when (allowRecovery)
                {
                    _logger.Error("SettingsService", "Settings file schema is unsupported", nameof(LoadSettings), ex);
                    Settings? fallbackRecovered = TryLoadSettingsFromFallbackFile(settingsPath);
                    if (fallbackRecovered != null)
                    {
                        _lastLoadUserWarning ??= new("Recovery_SettingsSchemaFallback");
                        return fallbackRecovered;
                    }

                    Settings? recovered = TryLoadSettingsFromAnyBackup(settingsPath);
                    if (recovered != null)
                    {
                        _lastLoadUserWarning ??= new("Recovery_SettingsSchemaRestored");
                        return recovered;
                    }

                    _lastLoadUserWarning = new("Recovery_SettingsSchemaDefaults");
                    return new Settings();
                }
                catch (IOException ex) when (allowRecovery)
                {
                    _logger.Error("SettingsService", "IO error loading settings file", nameof(LoadSettings), ex);
                    Settings? fallbackRecovered = TryLoadSettingsFromFallbackFile(settingsPath);
                    if (fallbackRecovered != null)
                    {
                        _lastLoadUserWarning ??= new("Recovery_SettingsUnreadableFallback");
                        return fallbackRecovered;
                    }

                    Settings? recovered = TryLoadSettingsFromAnyBackup(settingsPath);
                    if (recovered != null)
                    {
                        _lastLoadUserWarning ??= new("Recovery_SettingsReadErrorRestored");
                        return recovered;
                    }

                    _lastLoadUserWarning = new("Recovery_SettingsUnreadableDefaults");
                    return new Settings();
                }
                catch (Exception ex) when (allowRecovery)
                {
                    _logger.Error("SettingsService", "Unexpected error loading settings", nameof(LoadSettings), ex);
                    _lastLoadUserWarning = new("Recovery_SettingsLoadDefaults");
                    return new Settings();
                }
            });
        }

        public void SaveSettings(Settings settings)
        {
            try
            {
                ExecuteWithSettingsLock(() =>
                {
                    SaveSettingsInternal(settings);
                    Volatile.Write(ref _lastSaveAttempt, new(DateTimeOffset.UtcNow, true, null));
                    return 0;
                });
            }
            catch (Exception ex) { RecordSaveFailure(ex); throw; }
        }

        internal string ReadTextFileWithSettingsLock(string path)
        {
            return ExecuteWithSettingsLock(() => SettingsTransferService.ReadTextFile(path));
        }

        private void SaveSettingsInternal(Settings settings)
        {
            try
            {
                SettingsMigrationService.EnsureWritableVersion(settings.SchemaVersion);
                _ = SettingsMigrationService.MigrateToCurrent(settings);
                SettingsValidationService.Normalize(settings);
                bool fallbackIsAuthoritative = HasFallbackLocationMarker();
                if (fallbackIsAuthoritative) _useFallback = true;
                _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveStart} | outputCycleCount={settings.DeviceSwitching.Output.CycleDevices.Count} inputCycleCount={settings.DeviceSwitching.Input.CycleDevices.Count} runAtStartup={settings.Startup.Enabled} theme={settings.App.Theme}");
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    string targetPath = _useFallback ? _fallbackSettingsPath : _primarySettingsPath;
                    _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveTargetPath} | target={GetSettingsFileLogTarget(targetPath)}");
                }

                string json = JsonSerializer.Serialize(
                    settings,
                    SettingsJson.Options);
                EnsureSaveSizeAllowed(json);

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
                    }
                    catch (IOException ex) when ((ex.HResult & 0xFFFF) is not (ErrorSharingViolation or ErrorLockViolation))
                    {
                        _logger.Warning(
                            "SettingsService",
                            () => $"settings-save-primary-io-fallback | target={GetSettingsFileLogTarget(_primarySettingsPath)} error={ex.GetType().Name}",
                            nameof(SaveSettingsInternal),
                            ex);
                    }
                }

                if (!fallbackIsAuthoritative && File.Exists(_primarySettingsPath))
                {
                    // An older fallback may have been loaded before the primary file changed.
                    string? primaryContent = null;
                    bool primaryOversized = false;
                    try { primaryContent = ReadBoundedSettingsText(_primarySettingsPath); }
                    catch (InvalidDataException) { primaryOversized = true; }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException ex) when ((ex.HResult & 0xFFFF) is not (ErrorSharingViolation or ErrorLockViolation)) { }
                    if (primaryContent != null) EnsureFileSchemaIsWritable(primaryContent);
                    else if (primaryOversized) EnsureOversizedFileSchemaIsWritable(_primarySettingsPath);
                }

                WriteFileAtomic(_fallbackSettingsPath, json, createBackup: true);
                string markerPath = GetFallbackLocationMarkerPath();
                if (!File.Exists(markerPath)) WriteFileAtomic(markerPath, "fallback\n", createBackup: false);
                _useFallback = true;
                SetActivePath(_fallbackSettingsPath);
                _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSuccess} | mode=fallback");
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.Trace("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSuccessPath} | target={GetSettingsFileLogTarget(_fallbackSettingsPath)}");
                }
            }
            catch (InvalidDataException ex)
            {
                _logger.Warning("SettingsService", () => $"settings-save-refused | reason={ex.Message}", nameof(SaveSettings), ex);
                throw;
            }
            catch (IOException ex)
            {
                _logger.Error("SettingsService", "IO error saving settings file", nameof(SaveSettings), ex);
                throw SettingsFailure.WithPresentation(new IOException("Failed to save settings.", ex),
                    "Dialog_GuidanceAndDetails", new LocalizedText("Message_FailedToSaveSettings"), ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error("SettingsService", "Access denied when saving settings file", nameof(SaveSettings), ex);
                throw SettingsFailure.WithPresentation(new IOException("Failed to save settings.", ex),
                    "Dialog_GuidanceAndDetails", new LocalizedText("Message_FailedToSaveSettings"), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("SettingsService", "Unexpected error saving settings", nameof(SaveSettings), ex);
                throw SettingsFailure.WithPresentation(new IOException("Failed to save settings.", ex),
                    "Dialog_GuidanceAndDetails", new LocalizedText("Message_FailedToSaveSettings"), ex.Message);
            }
        }

        public bool SettingsFileExists()
        {
            return ExecuteWithSettingsLock(() =>
            {
                string? settingsPath = GetExistingSettingsPath();
                bool exists = settingsPath != null;
                if (exists)
                {
                    SetActivePath(settingsPath!);
                    _useFallback = string.Equals(settingsPath, _fallbackSettingsPath, StringComparison.OrdinalIgnoreCase);
                }
                else if (!HasFallbackLocationMarker() && HasBackupSettingsFile(_primarySettingsPath))
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
            });
        }

        public void GenerateDeviceReferenceFile(
            IEnumerable<CycleDevice> outputDevices,
            IEnumerable<CycleDevice> inputDevices,
            bool anonymizeIds = false)
        {
            try
            {
                bool written = _deviceReferenceWriter.WriteIfChanged(_activeDevicesPath, outputDevices, inputDevices, anonymizeIds);
                _logger.Debug("SettingsService", () => written
                    ? $"{AppConstants.Audio.LogEvents.Settings.GenerateDeviceReferenceSuccess} | target={GetDevicesFileLogTarget()}"
                    : $"{AppConstants.Audio.LogEvents.ViewModel.App.DeviceReferenceSkip} | reason=content-unchanged");
            }
            catch (Exception ex)
            {
                _logger.Warning("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.GenerateDeviceReferenceFailed} | target={GetDevicesFileLogTarget()}", nameof(GenerateDeviceReferenceFile), ex);
            }
        }

        private string GetDevicesFileLogTarget()
        {
            string fileName = GetFileNameForLog(_activeDevicesPath);
            string origin = _useLocalDeviceReferences ? "local" : GetPathOrigin(_activeDevicesPath, _fallbackDevicesPath);
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
                string[] settingsPaths = [.. new[] { _primarySettingsPath, _fallbackSettingsPath }
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                List<(string Original, string Recovery)> snapshots = [];
                bool deletionStarted = false;
                bool committed = false;
                try
                {
                    foreach (string path in settingsPaths.SelectMany(path => new[] { path }.Concat(EnumerateBackupCandidates(path)))
                        .Append(GetFallbackLocationMarkerPath()))
                    {
                        if (!File.Exists(path)) continue;
                        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                            throw SettingsFailure.WithPresentation(new UnauthorizedAccessException("A settings file is read-only."),
                                "Validation_SettingsReadOnly");
                        string recoveryPath = path + $".reset-{Guid.NewGuid():N}.tmp";
                        snapshots.Add((path, recoveryPath));
                        File.Copy(path, recoveryPath);
                    }

                    deletionStarted = true;
                    foreach ((string original, _) in snapshots) File.Delete(original);
                    committed = true;
                    _useFallback = false;
                    SetActivePath(_primarySettingsPath);
                    return 0;
                }
                catch (Exception ex)
                {
                    List<Exception> failures = [ex];
                    foreach ((string original, string recovery) in snapshots)
                    {
                        try
                        {
                            if (deletionStarted && !File.Exists(original) && File.Exists(recovery)) File.Move(recovery, original);
                        }
                        catch (Exception restoreException)
                        {
                            failures.Add(restoreException);
                            _logger.Warning("SettingsService", () => $"settings-reset-restore-failed | target={GetSettingsFileLogTarget(original)} recovery={GetSettingsFileLogTarget(recovery)}", nameof(DeleteSettingsFiles), restoreException);
                        }
                    }
                    throw SettingsFailure.WithPresentation(new AggregateException("Settings reset failed; removed files were restored where possible.", failures),
                        "Recovery_SettingsResetFailed");
                }
                finally
                {
                    foreach ((string original, string recovery) in snapshots)
                    {
                        if (!committed && !File.Exists(original)) continue;
                        try { File.Delete(recovery); }
                        catch (Exception ex)
                        {
                            _logger.Warning("SettingsService", () => $"settings-reset-recovery-cleanup-failed | target={GetSettingsFileLogTarget(recovery)}", nameof(DeleteSettingsFiles), ex);
                        }
                    }
                    foreach (string path in settingsPaths) RemoveEmptySettingsBackupDirectory(path);
                }
            });
        }

        private string? GetExistingSettingsPath()
        {
            if (HasFallbackLocationMarker())
                return File.Exists(_fallbackSettingsPath) ? _fallbackSettingsPath : null;
            if (File.Exists(_primarySettingsPath))
                return _primarySettingsPath;
            if (File.Exists(_fallbackSettingsPath))
                return _fallbackSettingsPath;
            return null;
        }

        /// <summary>Scopes fallback authority to this pair of locations without persisting either directory path.</summary>
        private string GetFallbackLocationMarkerPath()
            => _fallbackSettingsPath + "." + BuildSettingsMutexName(_primarySettingsPath, _fallbackSettingsPath)[^16..] + ".location";

        private bool HasFallbackLocationMarker() => File.Exists(GetFallbackLocationMarkerPath());

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
                    Settings? settings = ReadRecoverySettings(backupPath);
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
                Settings? settings = ReadRecoverySettings(_fallbackSettingsPath);
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

        private static string ReadBoundedSettingsText(string path)
        {
            int maxBytes = AppConstants.Limits.MaxSettingsImportFileBytes;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.CanSeek && stream.Length > maxBytes)
            {
                throw new InvalidDataException($"Settings file exceeds maximum supported size of {maxBytes} bytes.");
            }

            using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, maxBytes) : 4096);
            byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                int totalRead = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    totalRead += read;
                    if (totalRead > maxBytes)
                    {
                        throw new InvalidDataException($"Settings file exceeds maximum supported size of {maxBytes} bytes.");
                    }
                    buffer.Write(chunk, 0, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
            }

            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static void EnsureOversizedFileSchemaIsWritable(string path)
        {
            string? schemaVersion = TryReadTopLevelSchemaVersionStreaming(path);
            if (!string.IsNullOrWhiteSpace(schemaVersion))
            {
                SettingsMigrationService.EnsureWritableVersion(schemaVersion);
            }
        }

        private static string? TryReadTopLevelSchemaVersionStreaming(string path)
        {
            const int maxTokenBufferBytes = 256 * 1024;
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var state = new JsonReaderState(new JsonReaderOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                int validBytes = 0;
                bool isFinalBlock = false;
                bool strippedBom = false;
                bool awaitingSchemaVersion = false;
                bool rootStarted = false;

                while (true)
                {
                    int read = stream.Read(buffer, validBytes, buffer.Length - validBytes);
                    if (read == 0)
                    {
                        isFinalBlock = true;
                    }
                    else
                    {
                        validBytes += read;
                    }

                    ReadOnlySpan<byte> span = buffer.AsSpan(0, validBytes);
                    if (!strippedBom && (validBytes >= 3 || isFinalBlock))
                    {
                        strippedBom = true;
                        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                        {
                            span = span[3..];
                            span.CopyTo(buffer);
                            validBytes = span.Length;
                            span = buffer.AsSpan(0, validBytes);
                        }
                    }

                    if (!ScanChunkForSchemaVersion(span, isFinalBlock, ref state, ref rootStarted, ref awaitingSchemaVersion, out string? foundVersion, out int bytesConsumed))
                    {
                        return null;
                    }

                    if (foundVersion != null)
                    {
                        return foundVersion;
                    }

                    if (isFinalBlock)
                    {
                        return null;
                    }

                    int remaining = validBytes - bytesConsumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, bytesConsumed, buffer, 0, remaining);
                    }
                    validBytes = remaining;

                    if (validBytes == buffer.Length)
                    {
                        if (buffer.Length >= maxTokenBufferBytes)
                        {
                            return null;
                        }

                        byte[] grown = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, maxTokenBufferBytes));
                        Buffer.BlockCopy(buffer, 0, grown, 0, validBytes);
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                        buffer = grown;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool ScanChunkForSchemaVersion(
            ReadOnlySpan<byte> span,
            bool isFinalBlock,
            ref JsonReaderState state,
            ref bool rootStarted,
            ref bool awaitingSchemaVersion,
            out string? foundVersion,
            out int bytesConsumed)
        {
            foundVersion = null;
            var reader = new Utf8JsonReader(span, isFinalBlock, state);
            while (reader.Read())
            {
                if (!rootStarted)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        bytesConsumed = (int)reader.BytesConsumed;
                        state = reader.CurrentState;
                        return false;
                    }

                    rootStarted = true;
                    continue;
                }

                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                {
                    awaitingSchemaVersion = reader.ValueTextEquals("SchemaVersion");
                }
                else if (awaitingSchemaVersion)
                {
                    awaitingSchemaVersion = false;
                    if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.String)
                    {
                        foundVersion = reader.GetString();
                        bytesConsumed = (int)reader.BytesConsumed;
                        state = reader.CurrentState;
                        return true;
                    }
                }
            }

            bytesConsumed = (int)reader.BytesConsumed;
            state = reader.CurrentState;
            return true;
        }

        private Settings? ReadRecoverySettings(string sourcePath)
        {
            using JsonDocument document = JsonDocument.Parse(ReadBoundedSettingsText(sourcePath));
            JsonElement root = document.RootElement;
            if (GetNewerSchemaVersion(root) is { } newerSchema)
                return LoadNewerSettings(root, newerSchema, sourcePath, allowRecovery: true);

            Settings? settings = root.Deserialize<Settings>(SettingsJson.Options);
            if (settings != null) NormalizeCurrentSettings(settings);
            return settings;
        }

        private Settings LoadNewerSettings(JsonElement root, string schemaVersion, string sourcePath, bool allowRecovery)
        {
            var warning = new LocalizedText("Recovery_SettingsNewerSchema", schemaVersion);
            if (!allowRecovery)
                throw SettingsFailure.WithPresentation(new InvalidDataException(LocalizationService.English.Format(warning)),
                    warning.Key, schemaVersion);

            _lastLoadUserWarning = warning;
            _logger.Warning("SettingsService",
                () => $"settings-schema-newer-than-supported | source={GetSettingsFileLogTarget(sourcePath)} sourceSchema={schemaVersion} supported={Settings.CurrentSchemaVersion}");

            Settings? settings = null;
            try { settings = root.Deserialize<Settings>(SettingsJson.Options); }
            catch (JsonException) { }
            settings ??= new Settings();
            settings.SchemaVersion = schemaVersion;
            SettingsValidationService.EnsureRequiredStructure(settings);
            return settings;
        }

        private static void NormalizeCurrentSettings(Settings settings)
        {
            _ = SettingsMigrationService.MigrateToCurrent(settings);
            SettingsValidationService.Normalize(settings);
            settings.ExtensionData?.Clear();
        }

        private void SetActivePath(string settingsPath)
        {
            _activeSettingsPath = settingsPath;
            _activeDevicesPath = string.Equals(settingsPath, _primarySettingsPath, StringComparison.OrdinalIgnoreCase)
                ? _primaryDevicesPath
                : _fallbackDevicesPath;
        }

        /// <summary>
        /// Commits settings with a fixed pending recovery snapshot before rotating history. A blocked rotation
        /// leaves that snapshot recoverable without failing an already committed save or creating unbounded files.
        /// </summary>
        private void WriteFileAtomic(string path, string content, bool createBackup)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (createBackup && File.Exists(path))
            {
                string? currentContent = null;
                bool currentOversized = false;
                try { currentContent = ReadBoundedSettingsText(path); }
                catch (InvalidDataException) { currentOversized = true; }
                if (currentContent != null)
                {
                    EnsureFileSchemaIsWritable(currentContent);
                    if (string.Equals(currentContent, content, StringComparison.Ordinal))
                    {
                        _logger.Debug("SettingsService", () => $"{AppConstants.Audio.LogEvents.Settings.SaveSkipped} | reason=content-unchanged target={GetSettingsFileLogTarget(path)}");
                        return;
                    }
                }
                else if (currentOversized)
                {
                    EnsureOversizedFileSchemaIsWritable(path);
                }
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
                        string pendingBackupPath = backupPath + ".pending";
                        File.Replace(tempPath, path, pendingBackupPath, true);
                        try
                        {
                            RotateBackups(path, AppConstants.Files.SettingsBackupRetentionCount);
                            File.Move(pendingBackupPath, backupPath, overwrite: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.Warning("SettingsService", "settings-backup-rotation-failed | recoverySnapshot=pending settingsCommitted=true", nameof(WriteFileAtomic), ex);
                        }
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

        /// <summary>Protects newer schemas before replacement; malformed files still follow normal backup-and-replace recovery.</summary>
        private static void EnsureFileSchemaIsWritable(string json)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                if (GetNewerSchemaVersion(document.RootElement) is { } newerSchema)
                    SettingsMigrationService.EnsureWritableVersion(newerSchema);
            }
        }

        private static string? GetNewerSchemaVersion(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.Equals(nameof(Settings.SchemaVersion), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    SettingsMigrationService.IsNewerVersion(property.Value.GetString()))
                    return property.Value.GetString();
            }
            return null;
        }

        private static IEnumerable<string> EnumerateBackupCandidates(string path)
        {
            yield return GetBackupPath(path) + ".pending";
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

            File.Move(source, destination, overwrite: true);
        }

        private void RemoveEmptySettingsBackupDirectory(string baseSettingsPath)
        {
            string? directory = Path.GetDirectoryName(baseSettingsPath);
            string backupDirectory = Path.Combine(directory ?? string.Empty, AppConstants.Files.BackupFolderName);
            try
            {
                if (Directory.Exists(backupDirectory)
                    && !Directory.EnumerateFileSystemEntries(backupDirectory).Any())
                    Directory.Delete(backupDirectory, recursive: false);
            }
            catch (Exception ex)
            {
                _logger.Warning("SettingsService", "Failed to remove empty settings backup directory", nameof(DeleteSettingsFiles), ex);
            }
        }

        private static void EnsureSaveSizeAllowed(string payload)
        {
            const int limit = AppConstants.Limits.MaxSettingsImportFileBytes;
            if (Encoding.UTF8.GetByteCount(payload) <= limit) return;

            const int limitMiB = limit / (1024 * 1024);
            throw SettingsFailure.WithPresentation(
                new InvalidDataException($"Saving would exceed the {limitMiB} MiB settings limit. Remove some routines or settings entries and try again."),
                "Message_SettingsSaveSizeLimit", limitMiB);
        }

        private static CanonicalRewriteAnalysis AnalyzeCanonicalRewrite(string originalJson, JsonElement originalToken, Settings settings)
        {
            try
            {
                string canonicalJson = JsonSerializer.Serialize(
                    settings,
                    SettingsJson.Options);

                if (string.Equals(originalJson, canonicalJson, StringComparison.Ordinal))
                {
                    return CanonicalRewriteAnalysis.None;
                }

                using JsonDocument canonicalDocument = JsonDocument.Parse(canonicalJson);
                JsonElement canonicalToken = canonicalDocument.RootElement;

                bool requiresRewrite = !JsonElement.DeepEquals(originalToken, canonicalToken);
                if (!requiresRewrite)
                {
                    return CanonicalRewriteAnalysis.None;
                }

                if (originalToken.ValueKind != JsonValueKind.Object || canonicalToken.ValueKind != JsonValueKind.Object)
                {
                    return new CanonicalRewriteAnalysis(
                        RequiresRewrite: true,
                        UnknownKeyCount: 0,
                        MissingKeyCount: 0,
                        HasValueOnlyChanges: true);
                }

                var originalKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in originalToken.EnumerateObject())
                {
                    originalKeys.Add(property.Name);
                }

                var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in canonicalToken.EnumerateObject())
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
                    throw SettingsFailure.WithPresentation(new IOException("Timed out waiting for settings file lock."),
                        "Validation_SettingsLockTimeout");
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
