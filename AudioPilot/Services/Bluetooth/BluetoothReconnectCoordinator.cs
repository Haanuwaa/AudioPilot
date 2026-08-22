using System.Collections.Concurrent;
using System.Diagnostics;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Services.Bluetooth
{
    public enum BluetoothReconnectDeviceKind
    {
        Output,
        Input,
    }

    public readonly record struct BluetoothReconnectAttemptResult(
        bool Attempted,
        bool Connected,
        int Attempts,
        int CooldownSkips);

    public sealed class BluetoothReconnectCoordinator(IBluetoothReconnectService reconnectService, Logger logger)
    {
        private readonly IBluetoothReconnectService _reconnectService = reconnectService;
        private readonly Logger _logger = logger;
        private readonly ConcurrentDictionary<string, DateTime> _lastAttemptByDeviceIdUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TimeoutCircuitState> _timeoutCircuitByDeviceId = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReferenceCountedAsyncKeyedGatePool _reconnectGatesByDeviceId = new();
        private readonly Func<Func<double>> _createElapsedMillisecondsProvider = CreateStopwatchElapsedMillisecondsProvider;
        private int _pruneCounter;

        internal BluetoothReconnectCoordinator(
            IBluetoothReconnectService reconnectService,
            Logger logger,
            Func<Func<double>> createElapsedMillisecondsProvider)
            : this(reconnectService, logger)
        {
            _createElapsedMillisecondsProvider = createElapsedMillisecondsProvider
                ?? throw new ArgumentNullException(nameof(createElapsedMillisecondsProvider));
        }

        private sealed record TimeoutCircuitState
        {
            public int ConsecutiveTimeouts { get; init; }
            public DateTime OpenUntilUtc { get; init; }
        }

        private sealed class ReferenceCountedAsyncKeyedGatePool
        {
            private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

            private sealed class Entry
            {
                public readonly Lock Sync = new();
                public readonly SemaphoreSlim Semaphore = new(1, 1);
                public int References;
                public bool Retired;
            }

            public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
            {
                while (true)
                {
                    Entry entry = _entries.GetOrAdd(key, static _ => new Entry());
                    lock (entry.Sync)
                    {
                        if (entry.Retired) continue;
                        entry.References++;
                    }

                    try
                    {
                        await entry.Semaphore.WaitAsync(cancellationToken);
                        return new Lease(this, key, entry);
                    }
                    catch
                    {
                        ReleaseReference(key, entry);
                        throw;
                    }
                }
            }

            private void Release(string key, Entry entry)
            {
                entry.Semaphore.Release();
                ReleaseReference(key, entry);
            }

            private void ReleaseReference(string key, Entry entry)
            {
                bool retire;
                lock (entry.Sync)
                {
                    entry.References--;
                    retire = entry.References == 0;
                    if (retire) entry.Retired = true;
                }

                if (retire && _entries.TryRemove(key, out Entry? removed) && ReferenceEquals(removed, entry))
                {
                    entry.Semaphore.Dispose();
                }
            }

            private sealed class Lease(ReferenceCountedAsyncKeyedGatePool owner, string key, Entry entry) : IDisposable
            {
                private ReferenceCountedAsyncKeyedGatePool? _owner = owner;
                public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
            }
        }

        public async Task<bool> TryReconnectAsync(
            IReadOnlyList<CycleDevice> configuredCycle,
            IReadOnlyCollection<string> activeDeviceIds,
            BluetoothReconnectDeviceKind deviceKind,
            BluetoothReconnectOptions options,
            string opId,
            Action<int, int, string>? onAttemptProgress = null,
            CancellationToken cancellationToken = default)
        {
            BluetoothReconnectAttemptResult result = await TryReconnectDetailedAsync(
                configuredCycle,
                activeDeviceIds,
                deviceKind,
                options,
                opId,
                onAttemptProgress,
                cancellationToken);

            return result.Connected;
        }

        public async Task<BluetoothReconnectAttemptResult> TryReconnectDetailedAsync(
            IReadOnlyList<CycleDevice> configuredCycle,
            IReadOnlyCollection<string> activeDeviceIds,
            BluetoothReconnectDeviceKind deviceKind,
            BluetoothReconnectOptions options,
            string opId,
            Action<int, int, string>? onAttemptProgress = null,
            CancellationToken cancellationToken = default)
        {
            TryPruneReconnectState(DateTime.UtcNow);

            if (!options.Enabled)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Skip} | opId={opId} kind={ToKind(deviceKind)} reason=disabled");
                }

                return new BluetoothReconnectAttemptResult(Attempted: false, Connected: false, Attempts: 0, CooldownSkips: 0);
            }

            var activeIds = new HashSet<string>(activeDeviceIds, StringComparer.OrdinalIgnoreCase);
            var disconnected = new List<CycleDevice>(configuredCycle.Count);
            for (int index = 0; index < configuredCycle.Count; index++)
            {
                CycleDevice? device = configuredCycle[index];
                if (device == null || string.IsNullOrWhiteSpace(device.Id) || activeIds.Contains(device.Id))
                {
                    continue;
                }

                disconnected.Add(device);
            }

            if (disconnected.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Skip} | opId={opId} kind={ToKind(deviceKind)} reason=no-disconnected-candidates");
                }

                return new BluetoothReconnectAttemptResult(Attempted: false, Connected: false, Attempts: 0, CooldownSkips: 0);
            }

            var eligible = new List<CycleDevice>(disconnected.Count);
            for (int index = 0; index < disconnected.Count; index++)
            {
                CycleDevice device = disconnected[index];
                if (!options.OnlyLikelyBluetoothEndpoints || IsLikelyBluetoothEndpoint(device))
                {
                    eligible.Add(device);
                }
            }

            if (eligible.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Skip} | opId={opId} kind={ToKind(deviceKind)} reason=no-eligible-candidates disconnected={disconnected.Count}");
                }

                return new BluetoothReconnectAttemptResult(Attempted: false, Connected: false, Attempts: 0, CooldownSkips: 0);
            }

            _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Start} | opId={opId} kind={ToKind(deviceKind)} disconnected={disconnected.Count} eligible={eligible.Count} maxAttempts={options.MaxAttempts} timeoutMs={options.AttemptTimeoutMs}");

            int attempts = 0;
            int cooldownSkips = 0;
            Func<double> elapsedMilliseconds = _createElapsedMillisecondsProvider();

            foreach (CycleDevice candidate in eligible)
            {
                using (await _reconnectGatesByDeviceId.AcquireAsync(candidate.Id, cancellationToken))
                {
                    DateTime candidateNowUtc = DateTime.UtcNow;

                    if (attempts >= options.MaxAttempts)
                    {
                        break;
                    }

                    if (TryGetTimeoutCircuitRemainingMs(candidate.Id, candidateNowUtc, out int remainingMs))
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Skip} | opId={opId} kind={ToKind(deviceKind)} reason=timeout-circuit-open remainingMs={remainingMs}");
                        }

                        continue;
                    }

                    if (IsCooldownActive(candidate.Id, options.CooldownMs, candidateNowUtc))
                    {
                        cooldownSkips++;
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Skip} | opId={opId} kind={ToKind(deviceKind)} reason=cooldown-active");
                        }

                        continue;
                    }

                    while (attempts < options.MaxAttempts)
                    {
                        attempts++;
                        double attemptStartMs = elapsedMilliseconds();
                        double pairMs = 0;
                        double fallbackMs = 0;
                        string phaseResult = "failed";
                        string phaseSource = "none";
                        int pairPhaseBudgetMs = ResolvePairPhaseBudgetMs(options.AttemptTimeoutMs);
                        onAttemptProgress?.Invoke(attempts, options.MaxAttempts, candidate.Name);
                        _lastAttemptByDeviceIdUtc[candidate.Id] = DateTime.UtcNow;
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Attempt} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts}");

                        using var attemptTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        attemptTimeoutCts.CancelAfter(options.AttemptTimeoutMs);
                        using var pairPhaseTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(attemptTimeoutCts.Token);
                        pairPhaseTimeoutCts.CancelAfter(pairPhaseBudgetMs);

                        bool success;
                        double pairStartMs = 0;
                        try
                        {
                            pairStartMs = elapsedMilliseconds();
                            success = await _reconnectService.TryReconnectPairedAudioDeviceAsync(
                                candidate.Name,
                                candidate.Id,
                                opId,
                                ToKind(deviceKind),
                                pairPhaseTimeoutCts.Token);
                            pairMs += elapsedMilliseconds() - pairStartMs;
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            pairMs += elapsedMilliseconds() - pairStartMs;
                            int fallbackRemainingBudgetMs = ResolveFallbackRemainingBudgetMs(
                                options.AttemptTimeoutMs,
                                elapsedMilliseconds() - attemptStartMs,
                                pairPhaseBudgetMs);
                            KsFallbackAttemptResult fallbackAttempt = await TryReconnectUsingKsFallbackAsync(
                                candidate.Name,
                                opId,
                                deviceKind,
                                attempts,
                                options.AttemptTimeoutMs,
                                fallbackRemainingBudgetMs,
                                elapsedMilliseconds,
                                cancellationToken);
                            fallbackMs += fallbackAttempt.ElapsedMs;

                            if (fallbackAttempt.Connected)
                            {
                                ResetTimeoutState(candidate.Id);
                                phaseResult = "success";
                                phaseSource = "ks-fallback";
                                LogAttemptPhases(opId, deviceKind, attempts, pairMs, fallbackMs, elapsedMilliseconds() - attemptStartMs, phaseResult, phaseSource);
                                _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Success} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts} source=ks-fallback");
                                _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Summary} | opId={opId} kind={ToKind(deviceKind)} attempts={attempts} cooldownSkips={cooldownSkips} result=success");
                                return new BluetoothReconnectAttemptResult(Attempted: true, Connected: true, Attempts: attempts, CooldownSkips: cooldownSkips);
                            }

                            DateTime timeoutNowUtc = DateTime.UtcNow;
                            TimeoutCircuitUpdate timeoutUpdate = RegisterTimeout(candidate.Id, timeoutNowUtc);
                            _logger.Warning("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Timeout} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts} timeoutMs={options.AttemptTimeoutMs} remainingBudgetMs={fallbackRemainingBudgetMs} fullBudgetMs={options.AttemptTimeoutMs}");
                            if (timeoutUpdate.OpenedCircuit)
                            {
                                _logger.Warning("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.CircuitOpen} | opId={opId} kind={ToKind(deviceKind)} streak={timeoutUpdate.ConsecutiveTimeouts} openMs={RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs}");
                            }

                            phaseResult = "timeout";
                            phaseSource = "pair";
                            LogAttemptPhases(opId, deviceKind, attempts, pairMs, fallbackMs, elapsedMilliseconds() - attemptStartMs, phaseResult, phaseSource);

                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            ResetTimeoutState(candidate.Id);
                            phaseResult = ex.GetType().Name;
                            phaseSource = "pair";
                            LogAttemptPhases(opId, deviceKind, attempts, pairMs, fallbackMs, elapsedMilliseconds() - attemptStartMs, phaseResult, phaseSource);
                            _logger.Warning("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts} reason=exception", nameof(TryReconnectAsync), ex);
                            continue;
                        }

                        if (!success)
                        {
                            int fallbackRemainingBudgetMs = ResolveFallbackRemainingBudgetMs(options.AttemptTimeoutMs, elapsedMilliseconds() - attemptStartMs);
                            KsFallbackAttemptResult fallbackAttempt = await TryReconnectUsingKsFallbackAsync(
                                candidate.Name,
                                opId,
                                deviceKind,
                                attempts,
                                options.AttemptTimeoutMs,
                                fallbackRemainingBudgetMs,
                                elapsedMilliseconds,
                                cancellationToken);
                            fallbackMs += fallbackAttempt.ElapsedMs;

                            if (fallbackAttempt.Connected)
                            {
                                ResetTimeoutState(candidate.Id);
                                phaseResult = "success";
                                phaseSource = "ks-fallback";
                                LogAttemptPhases(opId, deviceKind, attempts, pairMs, fallbackMs, elapsedMilliseconds() - attemptStartMs, phaseResult, phaseSource);
                                _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Success} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts} source=ks-fallback");
                                _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Summary} | opId={opId} kind={ToKind(deviceKind)} attempts={attempts} cooldownSkips={cooldownSkips} result=success");
                                return new BluetoothReconnectAttemptResult(Attempted: true, Connected: true, Attempts: attempts, CooldownSkips: cooldownSkips);
                            }

                            ResetTimeoutState(candidate.Id);
                            phaseResult = "no-match-or-not-connected";
                            phaseSource = fallbackMs > 0 ? "ks-fallback" : "pair";
                            LogAttemptPhases(opId, deviceKind, attempts, pairMs, fallbackMs, elapsedMilliseconds() - attemptStartMs, phaseResult, phaseSource);
                            _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Failed} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts} reason=no-match-or-not-connected");
                            break;
                        }

                        ResetTimeoutState(candidate.Id);
                        phaseResult = "success";
                        phaseSource = "pair";
                        LogAttemptPhases(opId, deviceKind, attempts, pairMs, fallbackMs, elapsedMilliseconds() - attemptStartMs, phaseResult, phaseSource);
                        _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Success} | opId={opId} kind={ToKind(deviceKind)} attempt={attempts}");
                        _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Summary} | opId={opId} kind={ToKind(deviceKind)} attempts={attempts} cooldownSkips={cooldownSkips} result=success");
                        return new BluetoothReconnectAttemptResult(Attempted: true, Connected: true, Attempts: attempts, CooldownSkips: cooldownSkips);
                    }
                }
            }

            _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Summary} | opId={opId} kind={ToKind(deviceKind)} attempts={attempts} cooldownSkips={cooldownSkips} result=not-connected");
            return new BluetoothReconnectAttemptResult(Attempted: attempts > 0, Connected: false, Attempts: attempts, CooldownSkips: cooldownSkips);
        }

        private async Task<KsFallbackAttemptResult> TryReconnectUsingKsFallbackAsync(
            string deviceName,
            string opId,
            BluetoothReconnectDeviceKind deviceKind,
            int attempt,
            int attemptTimeoutMs,
            int fallbackRemainingBudgetMs,
            Func<double> elapsedMilliseconds,
            CancellationToken cancellationToken)
        {
            double fallbackStartMs = elapsedMilliseconds();

            if (fallbackRemainingBudgetMs <= 0)
            {
                _logger.Warning("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Timeout} | opId={opId} kind={ToKind(deviceKind)} attempt={attempt} stage=ks-fallback remainingBudgetMs={fallbackRemainingBudgetMs} fullBudgetMs={attemptTimeoutMs}");
                return new KsFallbackAttemptResult(
                    Connected: false,
                    ElapsedMs: elapsedMilliseconds() - fallbackStartMs);
            }

            using var fallbackTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            fallbackTimeoutCts.CancelAfter(fallbackRemainingBudgetMs);

            try
            {
                bool connected = await _reconnectService.TryReconnectUsingAudioEndpointControlAsync(
                    deviceName,
                    opId,
                    ToKind(deviceKind),
                    fallbackTimeoutCts.Token);

                return new KsFallbackAttemptResult(
                    Connected: connected,
                    ElapsedMs: elapsedMilliseconds() - fallbackStartMs);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Warning("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Timeout} | opId={opId} kind={ToKind(deviceKind)} attempt={attempt} stage=ks-fallback remainingBudgetMs={fallbackRemainingBudgetMs} fullBudgetMs={attemptTimeoutMs}");
                return new KsFallbackAttemptResult(
                    Connected: false,
                    ElapsedMs: elapsedMilliseconds() - fallbackStartMs);
            }
        }

        private static Func<double> CreateStopwatchElapsedMillisecondsProvider()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            return () => stopwatch.Elapsed.TotalMilliseconds;
        }

        private readonly record struct KsFallbackAttemptResult(bool Connected, double ElapsedMs);

        internal static int ResolvePairPhaseBudgetMs(int attemptTimeoutMs)
        {
            if (attemptTimeoutMs <= 1)
            {
                return 1;
            }

            int reservedFallbackBudgetMs = Math.Min(
                AppConstants.Timing.BluetoothReconnectFallbackReservedBudgetMs,
                Math.Max(1, attemptTimeoutMs / 2));

            return Math.Max(1, attemptTimeoutMs - reservedFallbackBudgetMs);
        }

        internal static int ResolveFallbackRemainingBudgetMs(int attemptTimeoutMs, double elapsedAttemptMs, int elapsedBudgetCapMs = int.MaxValue)
        {
            double boundedElapsedAttemptMs = Math.Min(Math.Max(0, elapsedAttemptMs), Math.Max(0, elapsedBudgetCapMs));
            int remainingBudgetMs = attemptTimeoutMs - (int)Math.Ceiling(boundedElapsedAttemptMs);
            return Math.Max(0, remainingBudgetMs);
        }

        internal static bool IsLikelyBluetoothEndpoint(CycleDevice device)
        {
            if (device == null)
            {
                return false;
            }

            string id = device.Id ?? string.Empty;
            string name = device.Name ?? string.Empty;
            bool hasStrongNegativeSignal = HasStrongNegativeBluetoothSignal(name);
            bool hasBluetoothIdSignal = HasBluetoothIdSignal(id);
            bool hasBluetoothNameSignal = HasBluetoothNameSignal(name);
            bool hasBluetoothModelSignal = HasBluetoothModelSignal(name);
            bool hasBluetoothRoleModelPattern = HasBluetoothRoleModelPattern(name);
            bool hasGenericNeutralAudioName = HasGenericNeutralAudioName(name);

            if (hasStrongNegativeSignal)
            {
                return false;
            }

            if (hasBluetoothNameSignal || hasBluetoothModelSignal || hasBluetoothRoleModelPattern)
            {
                return true;
            }

            if (hasBluetoothIdSignal)
            {
                return !hasGenericNeutralAudioName;
            }

            return false;
        }

        private static bool HasBluetoothIdSignal(string id)
        {
            return id.Contains("bthenum", StringComparison.OrdinalIgnoreCase)
                || id.Contains("bluetooth", StringComparison.OrdinalIgnoreCase)
                || id.Contains("bth", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasBluetoothNameSignal(string name)
        {
            return name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase)
                || name.Contains("hands-free", StringComparison.OrdinalIgnoreCase)
                || name.Contains("a2dp", StringComparison.OrdinalIgnoreCase)
                || name.Contains("hfp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasBluetoothModelSignal(string name)
        {
            return name.Contains("airpods", StringComparison.OrdinalIgnoreCase)
                || name.Contains("galaxy buds", StringComparison.OrdinalIgnoreCase)
                || name.Contains("buds", StringComparison.OrdinalIgnoreCase)
                || name.Contains("earbuds", StringComparison.OrdinalIgnoreCase)
                || name.Contains("true wireless", StringComparison.OrdinalIgnoreCase)
                || name.Contains("bt ", StringComparison.OrdinalIgnoreCase)
                || name.Contains("wf-", StringComparison.OrdinalIgnoreCase)
                || name.Contains("wh-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasBluetoothRoleModelPattern(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(')'))
            {
                return false;
            }

            return name.StartsWith("Headphones (", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Headset (", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Earbuds (", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Speaker (", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasStrongNegativeBluetoothSignal(string name)
        {
            return name.Contains("usb", StringComparison.OrdinalIgnoreCase)
                || name.Contains("hdmi", StringComparison.OrdinalIgnoreCase)
                || name.Contains("display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("line in", StringComparison.OrdinalIgnoreCase)
                || name.Contains("aux", StringComparison.OrdinalIgnoreCase)
                || name.Contains("virtual", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericNeutralAudioName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            string normalized = name.Trim();
            return normalized.Equals("Headphones", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Headset", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Speaker", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Speakers", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Speakerphone", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Earphones", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Earphone", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Microphone", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Mic", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Studio Output", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Studio Input", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Output", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Input", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Audio Output", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Audio Input", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCooldownActive(string deviceId, int cooldownMs, DateTime nowUtc)
        {
            if (!_lastAttemptByDeviceIdUtc.TryGetValue(deviceId, out DateTime lastAttemptUtc))
            {
                return false;
            }

            return (nowUtc - lastAttemptUtc).TotalMilliseconds < cooldownMs;
        }

        private bool TryGetTimeoutCircuitRemainingMs(string deviceId, DateTime nowUtc, out int remainingMs)
        {
            remainingMs = 0;

            if (!_timeoutCircuitByDeviceId.TryGetValue(deviceId, out TimeoutCircuitState? state))
            {
                return false;
            }

            if (state.OpenUntilUtc <= nowUtc)
            {
                if (state.ConsecutiveTimeouts <= 0)
                {
                    _timeoutCircuitByDeviceId.TryRemove(deviceId, out _);
                }
                else
                {
                    _timeoutCircuitByDeviceId.TryUpdate(
                        deviceId,
                        state with { OpenUntilUtc = DateTime.MinValue },
                        state);
                }

                return false;
            }

            remainingMs = Math.Max(1, (int)Math.Ceiling((state.OpenUntilUtc - nowUtc).TotalMilliseconds));
            return true;
        }

        private void LogAttemptPhases(
            string opId,
            BluetoothReconnectDeviceKind deviceKind,
            int attempt,
            double pairMs,
            double fallbackMs,
            double totalMs,
            string result,
            string source)
        {
            if (!_logger.IsEnabled(LogLevel.Debug))
            {
                return;
            }

            _logger.Debug(
                "BluetoothReconnect",
                () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.Phases} | opId={opId} kind={ToKind(deviceKind)} attempt={attempt} pairMs={pairMs:F1} fallbackMs={fallbackMs:F1} totalMs={totalMs:F1} result={result} source={source}");
        }

        private readonly record struct TimeoutCircuitUpdate(int ConsecutiveTimeouts, bool OpenedCircuit);

        private TimeoutCircuitUpdate RegisterTimeout(string deviceId, DateTime nowUtc)
        {
            TimeoutCircuitState state = _timeoutCircuitByDeviceId.AddOrUpdate(
                deviceId,
                _ => CreateTimeoutCircuitState(1, nowUtc),
                (_, existing) => CreateTimeoutCircuitState(existing.ConsecutiveTimeouts + 1, nowUtc));

            bool openedCircuit = state.OpenUntilUtc > nowUtc;
            return new TimeoutCircuitUpdate(state.ConsecutiveTimeouts, openedCircuit);
        }

        private static TimeoutCircuitState CreateTimeoutCircuitState(int consecutiveTimeouts, DateTime nowUtc)
        {
            DateTime openUntilUtc = consecutiveTimeouts >= RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitThreshold
                ? nowUtc.AddMilliseconds(RuntimeTuningConfig.BluetoothReconnectTimeoutCircuitOpenMs)
                : DateTime.MinValue;
            return new TimeoutCircuitState
            {
                ConsecutiveTimeouts = consecutiveTimeouts,
                OpenUntilUtc = openUntilUtc,
            };
        }

        private void ResetTimeoutState(string deviceId)
        {
            _timeoutCircuitByDeviceId.TryRemove(deviceId, out _);
        }

        private void TryPruneReconnectState(DateTime nowUtc)
        {
            const int PruneEveryNCalls = 32;
            int pruneTick = Interlocked.Increment(ref _pruneCounter);
            if ((pruneTick % PruneEveryNCalls) != 0)
            {
                return;
            }

            DateTime staleAttemptBeforeUtc = nowUtc.AddMinutes(-AppConstants.Timing.RetryStateTtlMinutes);

            foreach ((string deviceId, DateTime lastAttemptUtc) in _lastAttemptByDeviceIdUtc)
            {
                if (lastAttemptUtc > staleAttemptBeforeUtc)
                {
                    continue;
                }

                bool hasOpenCircuit = _timeoutCircuitByDeviceId.TryGetValue(deviceId, out TimeoutCircuitState? state)
                    && state.OpenUntilUtc > nowUtc;

                if (hasOpenCircuit)
                {
                    continue;
                }

                _lastAttemptByDeviceIdUtc.TryRemove(deviceId, out _);
                _timeoutCircuitByDeviceId.TryRemove(deviceId, out _);
            }

            foreach ((string deviceId, TimeoutCircuitState state) in _timeoutCircuitByDeviceId)
            {
                if (state.OpenUntilUtc > nowUtc)
                {
                    continue;
                }

                _timeoutCircuitByDeviceId.TryRemove(deviceId, out _);
            }
        }

        private static string ToKind(BluetoothReconnectDeviceKind kind)
        {
            return kind == BluetoothReconnectDeviceKind.Output ? "output" : "input";
        }
    }
}
