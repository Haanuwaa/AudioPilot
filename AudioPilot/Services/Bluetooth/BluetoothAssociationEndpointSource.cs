using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioPilot.Constants;
using AudioPilot.Logging;
using Windows.Devices.Enumeration;

namespace AudioPilot.Services.Bluetooth
{
    internal readonly record struct BluetoothAssociationEndpointPairAttempt(
        bool Connected,
        string Status,
        int HResult = 0,
        string? ExceptionType = null);

    internal sealed record BluetoothAssociationEndpointCandidate(
        string Id,
        string Name,
        bool IsPaired,
        bool IsConnected,
        Func<CancellationToken, Task<BluetoothAssociationEndpointPairAttempt>> TryPairAsync,
        string DeviceAddress = "");

    internal interface IBluetoothAssociationEndpointSource
    {
        Task<IReadOnlyList<BluetoothAssociationEndpointCandidate>> GetAssociationEndpointsAsync(
            string opId,
            string kind,
            CancellationToken cancellationToken);

        Task<BluetoothAssociationEndpointCandidate?> TryGetAssociationEndpointByIdAsync(
            string endpointId,
            string opId,
            string kind,
            CancellationToken cancellationToken);
    }

    internal delegate Task<IReadOnlyList<BluetoothAssociationEndpointCandidate>> EnumerateAssociationEndpointCandidatesAsync(
        bool useMinimalProperties,
        CancellationToken cancellationToken);

    internal sealed class BluetoothAssociationEndpointSource(
        Logger logger,
        EnumerateAssociationEndpointCandidatesAsync? enumerateAssociationEndpointCandidatesAsync = null,
        bool preferWatcherCache = true) : IBluetoothAssociationEndpointSource
    {
        private const int MinimalPropertiesRetryBudgetMs = AppConstants.Timing.BluetoothAssociationEndpointMinimalPropertiesRetryBudgetMs;
        private static readonly AssociationEndpointWatcherCache WatcherCache = new();
        private readonly Logger _logger = logger;
        private readonly EnumerateAssociationEndpointCandidatesAsync _enumerateAssociationEndpointCandidatesAsync = enumerateAssociationEndpointCandidatesAsync ?? EnumerateAssociationEndpointCandidatesWithDeviceInformationAsync;
        private readonly bool _preferWatcherCache = preferWatcherCache;

        internal static void DisposeWatcherCache(Logger logger)
        {
            WatcherCache.Dispose(logger);
        }

        public async Task<IReadOnlyList<BluetoothAssociationEndpointCandidate>> GetAssociationEndpointsAsync(
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            if (_preferWatcherCache)
            {
                WatcherCache.EnsureStarted(_logger);
            }

            var stopwatch = Stopwatch.StartNew();
            if (_preferWatcherCache && WatcherCache.TryGetSnapshot(out IReadOnlyList<BluetoothAssociationEndpointCandidate>? cachedSnapshot))
            {
                LogEndpointSourceDiagnostics(_logger, opId, kind, mode: "snapshot", source: "watcher-cache", stopwatch.ElapsedMilliseconds, cachedSnapshot.Count);
                return cachedSnapshot;
            }

            IReadOnlyList<BluetoothAssociationEndpointCandidate> enumerated = await EnumerateEndpointsAsync(opId, kind, cancellationToken);
            LogEndpointSourceDiagnostics(_logger, opId, kind, mode: "snapshot", source: "find-all", stopwatch.ElapsedMilliseconds, enumerated.Count);
            return enumerated;
        }

        public async Task<BluetoothAssociationEndpointCandidate?> TryGetAssociationEndpointByIdAsync(
            string endpointId,
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                return null;
            }

            if (_preferWatcherCache)
            {
                WatcherCache.EnsureStarted(_logger);
            }

            var stopwatch = Stopwatch.StartNew();
            if (_preferWatcherCache && WatcherCache.TryGetById(endpointId, out BluetoothAssociationEndpointCandidate? candidate))
            {
                LogEndpointSourceDiagnostics(_logger, opId, kind, mode: "by-id", source: "watcher-cache", stopwatch.ElapsedMilliseconds, candidateCount: null, found: candidate != null);
                return candidate;
            }

            IReadOnlyList<BluetoothAssociationEndpointCandidate> snapshot = await EnumerateEndpointsAsync(opId, kind, cancellationToken);
            BluetoothAssociationEndpointCandidate? matchedCandidate = snapshot.FirstOrDefault(candidate => string.Equals(candidate.Id, endpointId, StringComparison.OrdinalIgnoreCase));
            LogEndpointSourceDiagnostics(_logger, opId, kind, mode: "by-id", source: "find-all", stopwatch.ElapsedMilliseconds, snapshot.Count, found: matchedCandidate != null);
            return matchedCandidate;
        }

        internal static void LogEndpointSourceDiagnostics(
            Logger logger,
            string opId,
            string kind,
            string mode,
            string source,
            long elapsedMs,
            int? candidateCount,
            bool? found = null)
        {
            if (!logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            logger.Debug(
                "BluetoothReconnect",
                () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Source} | opId={opId} kind={kind} mode={mode} source={source} elapsedMs={elapsedMs}" +
                    (candidateCount.HasValue ? $" candidates={candidateCount.Value}" : string.Empty) +
                    (found.HasValue ? $" found={found.Value}" : string.Empty));
        }

        private async Task<IReadOnlyList<BluetoothAssociationEndpointCandidate>> EnumerateEndpointsAsync(
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<BluetoothAssociationEndpointCandidate> devices;
            var enumerationStopwatch = Stopwatch.StartNew();
            try
            {
                devices = await _enumerateAssociationEndpointCandidatesAsync(useMinimalProperties: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex) when (BluetoothReconnectService.IsPropertyKeySyntaxFailure(ex))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={kind} reason=enumeration-property-key-invalid exceptionType={ex.GetType().Name} hresult=0x{ex.HResult:X8} action=retry-with-minimal-properties");
                }

                int retryBudgetMs = ResolveMinimalPropertiesRetryBudgetMs(enumerationStopwatch.ElapsedMilliseconds);
                if (retryBudgetMs <= 0)
                {
                    _logger.Warning(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={kind} reason=enumeration-retry-budget-exhausted elapsedMs={enumerationStopwatch.ElapsedMilliseconds} budgetMs={MinimalPropertiesRetryBudgetMs}");
                    return [];
                }

                try
                {
                    using CancellationTokenSource retryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    retryCts.CancelAfter(retryBudgetMs);
                    devices = await _enumerateAssociationEndpointCandidatesAsync(useMinimalProperties: true, retryCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.Warning(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={kind} reason=enumeration-retry-timeout elapsedMs={enumerationStopwatch.ElapsedMilliseconds} budgetMs={retryBudgetMs}");
                    return [];
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception retryEx)
                {
                    string retryPackageContext = BluetoothReconnectService.GetPackageIdentityContext();
                    _logger.Warning(
                        "BluetoothReconnect",
                        () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={kind} reason=enumeration-retry-failed exceptionType={retryEx.GetType().Name} hresult=0x{retryEx.HResult:X8} package={retryPackageContext}",
                        nameof(GetAssociationEndpointsAsync),
                        retryEx);
                    return [];
                }
            }
            catch (Exception ex)
            {
                string packageContext = BluetoothReconnectService.GetPackageIdentityContext();
                _logger.Warning(
                    "BluetoothReconnect",
                    () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={kind} reason=enumeration-failed exceptionType={ex.GetType().Name} hresult=0x{ex.HResult:X8} package={packageContext}",
                    nameof(GetAssociationEndpointsAsync),
                    ex);
                return [];
            }

            return devices;
        }

        internal static int ResolveMinimalPropertiesRetryBudgetMs(long elapsedMs)
        {
            if (elapsedMs >= MinimalPropertiesRetryBudgetMs)
            {
                return 0;
            }

            return MinimalPropertiesRetryBudgetMs - (int)elapsedMs;
        }

        private static async Task<IReadOnlyList<BluetoothAssociationEndpointCandidate>> EnumerateAssociationEndpointCandidatesWithDeviceInformationAsync(
            bool useMinimalProperties,
            CancellationToken cancellationToken)
        {
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                BluetoothReconnectService.BluetoothAssociationEndpointSelector,
                useMinimalProperties ? [] : BluetoothReconnectService.RequestedProperties,
                DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken);

            WatcherCache.PublishSnapshot(devices);
            return BuildCandidates(devices);
        }

        private static IReadOnlyList<BluetoothAssociationEndpointCandidate> BuildCandidates(IEnumerable<DeviceInformation> devices)
        {
            return BuildCandidates(devices.Select(static device => CreateSnapshot(device)));
        }

        private static IReadOnlyList<BluetoothAssociationEndpointCandidate> BuildCandidates(IEnumerable<AssociationEndpointDeviceSnapshot> devices)
        {
            return
            [
                .. devices.Select(static device => CreateCandidate(device))
            ];
        }

        private static BluetoothAssociationEndpointCandidate CreateCandidate(AssociationEndpointDeviceSnapshot device)
        {
            return new BluetoothAssociationEndpointCandidate(
                device.Id,
                device.Name,
                device.IsPaired,
                device.IsConnected,
                cancellationToken => TryPairDeviceAsync(device.Id, cancellationToken),
                device.DeviceAddress);
        }

        internal static bool ShouldTreatPairStatusAsConnected(DevicePairingResultStatus status)
        {
            return status == DevicePairingResultStatus.Paired;
        }

        private static async Task<BluetoothAssociationEndpointPairAttempt> TryPairDeviceAsync(
            string deviceId,
            CancellationToken cancellationToken)
        {
            try
            {
                DeviceInformation device = await DeviceInformation.CreateFromIdAsync(
                    deviceId,
                    BluetoothReconnectService.RequestedProperties,
                    DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken);
                var pairOperation = device.Pairing.Custom.PairAsync(
                    DevicePairingKinds.ConfirmOnly,
                    DevicePairingProtectionLevel.None);
                DevicePairingResult result = await pairOperation.AsTask(cancellationToken);

                bool connected = ShouldTreatPairStatusAsConnected(result.Status);

                return new BluetoothAssociationEndpointPairAttempt(
                    connected,
                    result.Status.ToString());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (COMException ex)
            {
                return new BluetoothAssociationEndpointPairAttempt(
                    Connected: false,
                    Status: "exception",
                    HResult: ex.HResult,
                    ExceptionType: ex.GetType().Name);
            }
            catch (Exception ex)
            {
                return new BluetoothAssociationEndpointPairAttempt(
                    Connected: false,
                    Status: "exception",
                    HResult: ex.HResult,
                    ExceptionType: ex.GetType().Name);
            }
        }

        private sealed record AssociationEndpointDeviceSnapshot(
            string Id,
            string Name,
            bool IsPaired,
            bool IsConnected,
            string DeviceAddress);

        private static AssociationEndpointDeviceSnapshot CreateSnapshot(DeviceInformation device)
        {
            return new AssociationEndpointDeviceSnapshot(
                device.Id,
                device.Name ?? string.Empty,
                BluetoothReconnectService.IsPaired(device),
                BluetoothReconnectService.IsConnected(device),
                TryGetStringProperty(device.Properties, "System.Devices.Aep.DeviceAddress", out string address) ? address : string.Empty);
        }

        private static AssociationEndpointDeviceSnapshot MergeSnapshot(
            AssociationEndpointDeviceSnapshot existing,
            DeviceInformationUpdate update)
        {
            bool isPaired = TryGetBooleanProperty(
                update.Properties,
                "System.Devices.Aep.IsPaired",
                out bool pairedValue)
                ? pairedValue
                : existing.IsPaired;
            bool isConnected = TryGetBooleanProperty(
                update.Properties,
                "System.Devices.Aep.IsConnected",
                out bool connectedValue)
                ? connectedValue
                : existing.IsConnected;
            string deviceAddress = TryGetStringProperty(
                update.Properties,
                "System.Devices.Aep.DeviceAddress",
                out string addressValue)
                ? addressValue
                : existing.DeviceAddress;

            return existing with
            {
                IsPaired = isPaired,
                IsConnected = isConnected,
                DeviceAddress = deviceAddress,
            };
        }

        private static bool TryGetStringProperty(IReadOnlyDictionary<string, object> properties, string key, out string value)
        {
            if (properties.TryGetValue(key, out object? rawValue) && rawValue != null)
            {
                value = rawValue.ToString()?.Trim() ?? string.Empty;
                return value.Length > 0;
            }

            value = string.Empty;
            return false;
        }

        private static bool TryGetBooleanProperty(
            IReadOnlyDictionary<string, object> properties,
            string key,
            out bool value)
        {
            if (properties.TryGetValue(key, out object? rawValue))
            {
                switch (rawValue)
                {
                    case bool boolValue:
                        value = boolValue;
                        return true;
                    case IConvertible convertible:
                        try
                        {
                            value = convertible.ToBoolean(null);
                            return true;
                        }
                        catch
                        {
                        }

                        break;
                }
            }

            value = false;
            return false;
        }

        private sealed class AssociationEndpointWatcherCache
        {
            private readonly Lock _gate = new();
            private Dictionary<string, AssociationEndpointDeviceSnapshot> _devices = new(StringComparer.OrdinalIgnoreCase);
            private DeviceWatcher? _watcher;
            private Logger? _logger;
            private bool _enumerationCompleted;

            public void EnsureStarted(Logger logger)
            {
                lock (_gate)
                {
                    if (_watcher != null)
                    {
                        return;
                    }

                    DeviceWatcher watcher = DeviceInformation.CreateWatcher(
                        BluetoothReconnectService.BluetoothAssociationEndpointSelector,
                        BluetoothReconnectService.RequestedProperties,
                        DeviceInformationKind.AssociationEndpoint);
                    _logger = logger;
                    _enumerationCompleted = false;
                    _devices.Clear();
                    Attach(watcher);
                    _watcher = watcher;

                    try
                    {
                        watcher.Start();
                    }
                    catch (Exception ex)
                    {
                        Detach(watcher);
                        _watcher = null;
                        logger.Warning(
                            "BluetoothReconnect",
                            () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId=n/a kind=unknown reason=watcher-start-failed exceptionType={ex.GetType().Name} hresult=0x{ex.HResult:X8}",
                            nameof(EnsureStarted),
                            ex);
                    }
                }
            }

            public void Dispose(Logger logger)
            {
                DeviceWatcher? watcher;
                lock (_gate)
                {
                    watcher = _watcher;
                    _watcher = null;
                    _enumerationCompleted = false;
                    _devices.Clear();
                    if (watcher != null) Detach(watcher);
                }

                if (watcher == null) return;
                try
                {
                    watcher.Stop();
                }
                catch (Exception ex)
                {
                    logger.Warning("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId=n/a kind=unknown reason=watcher-stop-failed exceptionType={ex.GetType().Name} hresult=0x{ex.HResult:X8}", nameof(Dispose), ex);
                }
            }

            public bool TryGetSnapshot(out IReadOnlyList<BluetoothAssociationEndpointCandidate> snapshot)
            {
                lock (_gate)
                {
                    if (!_enumerationCompleted)
                    {
                        snapshot = [];
                        return false;
                    }

                    snapshot = BuildCandidates(_devices.Values.ToArray());
                    return true;
                }
            }

            public bool TryGetById(string endpointId, out BluetoothAssociationEndpointCandidate? candidate)
            {
                lock (_gate)
                {
                    if (_devices.TryGetValue(endpointId, out AssociationEndpointDeviceSnapshot? device) && device != null)
                    {
                        candidate = CreateCandidate(device);
                        return true;
                    }
                }

                candidate = null;
                return false;
            }

            public void PublishSnapshot(IEnumerable<DeviceInformation> devices)
            {
                PublishSnapshot(devices.Select(static device => CreateSnapshot(device)));
            }

            private void PublishSnapshot(IEnumerable<AssociationEndpointDeviceSnapshot> devices)
            {
                var replacement = new Dictionary<string, AssociationEndpointDeviceSnapshot>(StringComparer.OrdinalIgnoreCase);
                foreach (AssociationEndpointDeviceSnapshot device in devices)
                {
                    replacement[device.Id] = device;
                }

                lock (_gate)
                {
                    _devices = replacement;
                    _enumerationCompleted = true;
                }
            }

            private void Attach(DeviceWatcher watcher)
            {
                watcher.Added += OnAdded;
                watcher.Updated += OnUpdated;
                watcher.Removed += OnRemoved;
                watcher.EnumerationCompleted += OnEnumerationCompleted;
                watcher.Stopped += OnStopped;
            }

            private void Detach(DeviceWatcher watcher)
            {
                watcher.Added -= OnAdded;
                watcher.Updated -= OnUpdated;
                watcher.Removed -= OnRemoved;
                watcher.EnumerationCompleted -= OnEnumerationCompleted;
                watcher.Stopped -= OnStopped;
            }

            private void OnAdded(DeviceWatcher sender, DeviceInformation device)
            {
                try
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(sender, _watcher)) _devices[device.Id] = CreateSnapshot(device);
                    }
                }
                catch (Exception ex) { LogCallbackFailure(nameof(OnAdded), ex); }
            }

            private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
            {
                try
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(sender, _watcher) && _devices.TryGetValue(update.Id, out AssociationEndpointDeviceSnapshot? existing))
                            _devices[update.Id] = MergeSnapshot(existing, update);
                    }
                }
                catch (Exception ex) { LogCallbackFailure(nameof(OnUpdated), ex); }
            }

            private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
            {
                try
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(sender, _watcher)) _devices.Remove(update.Id);
                    }
                }
                catch (Exception ex) { LogCallbackFailure(nameof(OnRemoved), ex); }
            }

            private void OnEnumerationCompleted(DeviceWatcher sender, object args)
            {
                try
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(sender, _watcher)) _enumerationCompleted = true;
                    }
                }
                catch (Exception ex) { LogCallbackFailure(nameof(OnEnumerationCompleted), ex); }
            }

            private void OnStopped(DeviceWatcher sender, object args)
            {
                try
                {
                    lock (_gate)
                    {
                        if (!ReferenceEquals(sender, _watcher)) return;
                        Detach(sender);
                        _watcher = null;
                        _enumerationCompleted = false;
                    }
                }
                catch (Exception ex) { LogCallbackFailure(nameof(OnStopped), ex); }
            }

            private void LogCallbackFailure(string callback, Exception exception)
            {
                _logger?.Warning("BluetoothReconnect", () => $"watcher-callback-failed | callback={callback} error={exception.GetType().Name}", callback, exception);
            }
        }
    }
}
