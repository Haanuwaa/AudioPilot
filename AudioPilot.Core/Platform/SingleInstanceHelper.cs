using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioPilot.Cli;
using AudioPilot.Constants;
using AudioPilot.Logging;

namespace AudioPilot.Platform
{
    public readonly record struct SingleInstanceCommandResult(
        [property: JsonRequired] int ExitCode,
        string? Output = null,
        string? ErrorCode = null,
        string? ErrorMessage = null,
        [property: JsonRequired] int ProtocolVersion = SingleInstanceHelper.ResponseProtocolVersion)
    {
        [JsonIgnore]
        internal Action? AfterResponse { get; init; }
    }

    internal static class SingleInstanceCommandResultParser
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowDuplicateProperties = false,
        };

        internal static bool TryParse(ReadOnlySpan<byte> payload, out SingleInstanceCommandResult result)
        {
            result = default;
            try
            {
                result = JsonSerializer.Deserialize<SingleInstanceCommandResult>(payload, SerializerOptions);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static byte[] Serialize(SingleInstanceCommandResult response) =>
            JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);
    }

    internal enum SingleInstanceSignalFailureKind
    {
        None = 0,
        ConnectionFailed = 1,
        InvalidResponse = 2,
        OutcomeUnknown = 3,
        AcquisitionFailed = 4,
    }

    internal enum SingleInstanceAcquireDisposition
    {
        Acquired = 0,
        ExistingHealthyInstance = 1,
        ExistingUnresponsiveInstance = 2,
        AcquisitionFailed = 3,
    }

    internal readonly record struct SingleInstanceAcquireResult(
        SingleInstanceAcquireDisposition Disposition,
        SingleInstanceSignalFailureKind FailureKind = SingleInstanceSignalFailureKind.None,
        int? ResponseExitCode = null,
        string? ResponseErrorCode = null)
    {
        internal bool Acquired => Disposition == SingleInstanceAcquireDisposition.Acquired;
        internal bool ExistingHealthy => Disposition == SingleInstanceAcquireDisposition.ExistingHealthyInstance;
        internal bool ExistingUnresponsive => Disposition == SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance;
        internal bool ExistingRequestSucceeded => ExistingHealthy && ResponseExitCode.GetValueOrDefault() == 0;
    }

    public partial class SingleInstanceHelper : IDisposable, IAsyncDisposable
    {
        private const string ActivationPayload = "ACTIVATE";
        internal const int ResponseProtocolVersion = 1;
        private const int MaxConcurrentRequests = 8;
        private const int MaxRequestBytes = 64 * 1024;
        private const int MaxResponseBytes = 4 * 1024 * 1024;
        private readonly Lock _listenerAcceptLock = new();
        private Mutex? _mutex;
        private bool _owned;
        private int _shutdownStarted;
        private int _disposeStarted;
        private readonly Lock _disposeLock = new();
        private Task? _disposeTask;
        private readonly Logger _logger;
        private CancellationTokenSource? _activationListenerCts;
        private Task? _activationListenerTask;
        private Task _activationListenerStopTask = Task.CompletedTask;
        private TaskCompletionSource? _activationListenerReadySource;
        private TaskCompletionSource? _requestReadTimedOutSource;
        private TaskCompletionSource? _requestProcessedSource;
        private TaskCompletionSource? _responseWriteTimedOutSource;
        private readonly string _mutexName;
        private readonly string _activationPipeName;
        private readonly bool _isHeadless;
        private readonly bool _deferActivationListener;
        private Process? _headlessOwnerProcess;
        internal const string HeadlessBusyErrorCode = "headless-command-running";
        private static readonly SingleInstanceCommandResult HeadlessBusyResponse = new(3, ErrorCode: HeadlessBusyErrorCode,
            ErrorMessage: "A command-line operation is still running. Try again after it finishes.", ProtocolVersion: ResponseProtocolVersion);
        private readonly int _responseTimeoutMs;
        private readonly int _requestReadTimeoutMs;
        private readonly int _responseWriteTimeoutMs;
        private readonly int _commandTimeoutMs;
        private readonly int _activationResponseTimeoutMs;
        private readonly int _activationCommandTimeoutMs;
        private readonly TimeProvider _timeProvider;

        private readonly struct PipeDeadline : IDisposable
        {
            private readonly CancellationTokenSource? _timer;
            internal CancellationTokenSource Source { get; }
            internal CancellationToken Token => Source.Token;
            internal bool IsCancellationRequested => Source.IsCancellationRequested;

            internal PipeDeadline(int timeoutMs, TimeProvider timeProvider, CancellationToken lifetime)
            {
                if (ReferenceEquals(timeProvider, TimeProvider.System))
                {
                    _timer = null;
                    Source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    Source.CancelAfter(timeoutMs);
                }
                else
                {
                    _timer = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), timeProvider);
                    Source = CancellationTokenSource.CreateLinkedTokenSource(lifetime, _timer.Token);
                }
            }

            public void Dispose()
            {
                Source.Dispose();
                _timer?.Dispose();
            }
        }

        public event EventHandler? ActivationRequested;
        internal event Func<CancellationToken, Task<bool>>? ActivationRequestedAsync;
        public event Func<string, CancellationToken, Task<SingleInstanceCommandResult>>? CommandRequested;
        public bool ExistingInstanceDetected { get; private set; }
        public bool LastSignalExistingSucceeded { get; private set; }
        public int? LastSignalExitCode { get; private set; }
        public string? LastSignalOutput { get; private set; }
        public string? LastSignalErrorCode { get; private set; }
        public string? LastSignalErrorMessage { get; private set; }
        public int? LastSignalProtocolVersion { get; private set; }
        internal SingleInstanceSignalFailureKind LastSignalFailureKind { get; private set; }
        internal Task ActivationListenerReadyForTests => _activationListenerReadySource?.Task ?? Task.CompletedTask;
        internal Task RequestReadTimedOutForTests => _requestReadTimedOutSource?.Task ?? Task.CompletedTask;
        internal Task RequestProcessedForTests => _requestProcessedSource?.Task ?? Task.CompletedTask;
        internal Task ResponseWriteTimedOutForTests => _responseWriteTimedOutSource?.Task ?? Task.CompletedTask;
        internal bool HasActivationRequestedSubscribersForTests => ActivationRequested != null || ActivationRequestedAsync != null;
        internal bool HasCommandRequestedSubscribersForTests => CommandRequested != null;
        internal bool IsShutdownStartedForTests => Volatile.Read(ref _shutdownStarted) != 0;

        public SingleInstanceHelper() : this(isHeadless: false) { }

        internal SingleInstanceHelper(bool isHeadless, bool deferActivationListener = false)
            : this(SingleInstanceScope.Current.Qualify(DiagnosticSession.InstanceName),
                $"{SingleInstanceScope.Current.Qualify(DiagnosticSession.InstanceName)}.Activation",
                isHeadless: isHeadless,
                deferActivationListener: deferActivationListener)
        {
        }

        internal SingleInstanceHelper(
            string mutexName,
            string activationPipeName,
            int responseTimeoutMs = AppConstants.Timing.SingleInstanceResponseTimeoutMs,
            int requestReadTimeoutMs = AppConstants.Timing.SingleInstanceRequestReadTimeoutMs,
            int responseWriteTimeoutMs = AppConstants.Timing.SingleInstanceResponseWriteTimeoutMs,
            int? commandTimeoutMs = null,
            bool isHeadless = false,
            TimeProvider? timeProvider = null,
            bool deferActivationListener = false,
            int? activationTimeoutMs = null)
        {
            _logger = Logger.Instance;
            _mutexName = mutexName;
            _activationPipeName = activationPipeName;
            _isHeadless = isHeadless;
            _deferActivationListener = deferActivationListener;
            _responseTimeoutMs = Math.Max(1, responseTimeoutMs);
            _requestReadTimeoutMs = Math.Max(1, requestReadTimeoutMs);
            _responseWriteTimeoutMs = Math.Max(1, responseWriteTimeoutMs);
            _commandTimeoutMs = Math.Max(1, commandTimeoutMs ?? responseTimeoutMs - 1000);
            int defaultActivationResponseMs = Math.Max(1, activationTimeoutMs ?? AppConstants.Timing.SingleInstanceActivationResponseTimeoutMs);
            _activationResponseTimeoutMs = Math.Min(_responseTimeoutMs, defaultActivationResponseMs);
            int defaultActivationCommandMs = activationTimeoutMs.HasValue
                ? Math.Max(1, activationTimeoutMs.Value)
                : Math.Max(1, AppConstants.Timing.SingleInstanceActivationResponseTimeoutMs - 500);
            _activationCommandTimeoutMs = Math.Min(_commandTimeoutMs, defaultActivationCommandMs);
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Attempts to acquire the single-instance mutex for the current process.
        /// </summary>
        /// <remarks>
        /// On success, the helper starts the background activation listener used for later UI activation or CLI
        /// forwarding (unless deferred until handlers are attached). On failure, the helper marks that another instance
        /// exists and optionally signals that instance with either an activation payload or a forwarded command payload.
        /// </remarks>
        public bool TryAcquire(string? payloadToExistingInstance = null)
        {
            return TryAcquireDetailed(payloadToExistingInstance).Acquired;
        }

        // The owner probe never activates the GUI and still identifies a short-lived CLI owner.
        internal SingleInstanceAcquireResult TryAcquireForLogon() =>
            TryAcquireDetailedCore(OwnerProbePayload, showUserErrors: false, signalExistingInstance: true, CancellationToken.None);

        internal Task<SingleInstanceAcquireResult> TryAcquireForLogonAsync(CancellationToken cancellationToken = default) =>
            Task.Run(
                () => TryAcquireDetailedCore(OwnerProbePayload, showUserErrors: false, signalExistingInstance: true, cancellationToken),
                cancellationToken);

        internal SingleInstanceAcquireResult TryAcquireDetailed(
            string? payloadToExistingInstance = null,
            bool showUserErrors = true,
            bool signalExistingInstance = true) =>
            TryAcquireDetailedCore(payloadToExistingInstance, showUserErrors, signalExistingInstance, CancellationToken.None);

        internal Task<SingleInstanceAcquireResult> TryAcquireDetailedAsync(
            string? payloadToExistingInstance = null,
            bool showUserErrors = true,
            bool signalExistingInstance = true,
            CancellationToken cancellationToken = default) =>
            Task.Run(
                () => TryAcquireDetailedCore(payloadToExistingInstance, showUserErrors, signalExistingInstance, cancellationToken),
                cancellationToken);

        private SingleInstanceAcquireResult TryAcquireDetailedCore(
            string? payloadToExistingInstance,
            bool showUserErrors,
            bool signalExistingInstance,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0 || Volatile.Read(ref _shutdownStarted) != 0,
                this);
            cancellationToken.ThrowIfCancellationRequested();

            if (_headlessOwnerProcess is { HasExited: false })
                return new(SingleInstanceAcquireDisposition.ExistingHealthyInstance,
                    ResponseExitCode: HeadlessBusyResponse.ExitCode, ResponseErrorCode: HeadlessBusyErrorCode);
            Interlocked.Exchange(ref _headlessOwnerProcess, null)?.Dispose();

            bool isCliForward = !string.IsNullOrWhiteSpace(payloadToExistingInstance);
            ExistingInstanceDetected = false;
            LastSignalExistingSucceeded = false;
            LastSignalExitCode = null;
            LastSignalOutput = null;
            LastSignalErrorCode = null;
            LastSignalErrorMessage = null;
            LastSignalProtocolVersion = null;
            LastSignalFailureKind = SingleInstanceSignalFailureKind.None;

            if (_owned && _mutex != null)
            {
                return new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired);
            }

            try
            {
                bool createdNew;
                try
                {
                    _mutex = new Mutex(true, _mutexName, out createdNew);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // An elevated instance in the same session owns the named mutex with a restricted DACL.
                    _logger.Debug("SingleInstanceHelper", () => $"single-instance-mutex-access-denied | mutex={_mutexName} error={ex.GetType().Name}");
                    createdNew = false;
                }
                _owned = createdNew;

                if (!_owned)
                {
                    ExistingInstanceDetected = true;
                    _mutex?.Dispose();
                    _mutex = null;

                    // Recovery may check ownership without replaying an activation or an uncertain command.
                    if (!signalExistingInstance)
                        return new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance);

                    if (isCliForward && _logger.IsEnabled(LogLevel.Info))
                    {
                        _logger.Info("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardStart} | targetPipe={_activationPipeName} mode=existing-instance requestProtocolVersion={GetRequestProtocolVersion(payloadToExistingInstance)} requestAction={GetRequestAction(payloadToExistingInstance)}");
                    }

                    LastSignalExistingSucceeded = SignalExistingInstance(payloadToExistingInstance ?? ActivationPayload, cancellationToken);
                    if (!LastSignalExistingSucceeded)
                    {
                        if (!isCliForward && showUserErrors)
                        {
                            _logger.Warning("SingleInstanceHelper", "Interactive single-instance recovery is delegated to the application startup coordinator.");
                        }

                        return new SingleInstanceAcquireResult(
                            SingleInstanceAcquireDisposition.ExistingUnresponsiveInstance,
                            LastSignalFailureKind);
                    }

                    return new SingleInstanceAcquireResult(
                        SingleInstanceAcquireDisposition.ExistingHealthyInstance,
                        ResponseExitCode: LastSignalExitCode,
                        ResponseErrorCode: LastSignalErrorCode);
                }

                if (!_deferActivationListener)
                {
                    StartActivationListener();
                }

                return new SingleInstanceAcquireResult(SingleInstanceAcquireDisposition.Acquired);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ResetFailedAcquisition();
                _logger.Warning("SingleInstanceHelper", "Failed to check for running instances", nameof(TryAcquire), ex);
                if (!isCliForward && showUserErrors)
                {
                    _logger.Warning("SingleInstanceHelper", "Interactive single-instance acquisition failure is delegated to the application startup coordinator.");
                }
                LastSignalFailureKind = SingleInstanceSignalFailureKind.AcquisitionFailed;
                return new SingleInstanceAcquireResult(
                    SingleInstanceAcquireDisposition.AcquisitionFailed,
                    LastSignalFailureKind);
            }
        }

        internal void EnsureActivationListenerStarted()
        {
            lock (_disposeLock)
            {
                if (Volatile.Read(ref _disposeStarted) != 0 || Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return;
                }

                if (_owned)
                {
                    StartActivationListener();
                }
            }
        }

        public void Dispose()
        {
            _ = StartDisposal();
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StartDisposal()
                    .WaitAsync(TimeSpan.FromSeconds(AppConstants.Timing.SingleInstanceListenerStopTimeoutSeconds), _timeProvider)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Ownership remains held until accepted callbacks actually stop.
            }
            GC.SuppressFinalize(this);
        }

        private Task StartDisposal()
        {
            lock (_disposeLock)
            {
                if (_disposeTask != null) return _disposeTask;
                Interlocked.Exchange(ref _disposeStarted, 1);
                BeginShutdown();
                return _disposeTask = ReleaseMutexAfterListenerAsync();
            }
        }

        private async Task ReleaseMutexAfterListenerAsync()
        {
            try
            {
                await Volatile.Read(ref _activationListenerStopTask).ConfigureAwait(false);
            }
            finally
            {
                ReleaseMutex();
            }
        }

        internal void BeginShutdown()
        {
            lock (_disposeLock)
            {
                if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
                {
                    return;
                }

                ActivationRequested = null;
                ActivationRequestedAsync = null;
                OwnerStateRequested = null;
                CommandRequested = null;
                Volatile.Write(ref _activationListenerStopTask, StopActivationListenerAsync());
            }
        }

        private void ResetFailedAcquisition()
        {
            _ = StopActivationListenerAsync();
            ReleaseMutex();
        }

        private void ReleaseMutex()
        {
            Interlocked.Exchange(ref _headlessOwnerProcess, null)?.Dispose();
            Mutex? mutex = Interlocked.Exchange(ref _mutex, null);

            if (mutex != null)
            {
                try
                {
                    mutex.Dispose();
                }
                catch
                {
                }
            }

            _owned = false;
        }

        private void StartActivationListener()
        {
            if (_activationListenerCts != null)
            {
                return;
            }

            var activationListenerCts = new CancellationTokenSource();
            _activationListenerCts = activationListenerCts;
            _activationListenerReadySource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _requestReadTimedOutSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _requestProcessedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _responseWriteTimedOutSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken activationToken = activationListenerCts.Token;
            _activationListenerTask = Task.Run(() => ActivationListenerLoopAsync(activationToken), activationToken);
        }

        private async Task StopActivationListenerAsync()
        {
            using CancellationTokenSource? listenerCts = Interlocked.Exchange(ref _activationListenerCts, null);
            Task? listenerTask = Interlocked.Exchange(ref _activationListenerTask, null);

            if (listenerCts == null)
            {
                return;
            }

            try
            {
                lock (_listenerAcceptLock)
                {
                    listenerCts.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation listener stop failed", nameof(StopActivationListenerAsync), ex);
            }
            finally
            {
                _activationListenerReadySource = null;
                _requestReadTimedOutSource = null;
                _requestProcessedSource = null;
                _responseWriteTimedOutSource = null;
            }

            if (listenerTask == null)
            {
                return;
            }

            try
            {
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(AppConstants.Timing.SingleInstanceListenerStopTimeoutSeconds), _timeProvider);
                Task completedTask = await Task.WhenAny(listenerTask, timeoutTask).ConfigureAwait(false);
                if (completedTask != listenerTask)
                {
                    _logger.Warning("SingleInstanceHelper", "Activation listener stop timed out", nameof(StopActivationListenerAsync));
                }

                await listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation listener stop failed", nameof(StopActivationListenerAsync), ex);
            }
        }

        /// <summary>
        /// Accepts activation requests independently of command execution. Cancellation closes the accepting pipe
        /// before the primary mutex is released; accepted requests retain their streams, gate, and listener token until drained.
        /// </summary>
        private async Task ActivationListenerLoopAsync(CancellationToken cancellationToken)
        {
            using var commandGate = new SemaphoreSlim(1, 1);
            using var presentationGate = new SemaphoreSlim(1, 1);
            var requests = new HashSet<Task>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    requests.RemoveWhere(static request => request.IsCompleted);
                    if (requests.Count >= MaxConcurrentRequests)
                    {
                        await Task.WhenAny(requests).WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    NamedPipeServerStream? server = null;
                    try
                    {
                        CancellationTokenRegistration closeAccept;
                        lock (_listenerAcceptLock)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            server = new NamedPipeServerStream(
                                _activationPipeName,
                                PipeDirection.InOut,
                                MaxConcurrentRequests,
                                PipeTransmissionMode.Byte,
                                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                            closeAccept = cancellationToken.Register(static state => ((NamedPipeServerStream)state!).Dispose(), server);
                        }
                        using (closeAccept)
                        {
                            _activationListenerReadySource?.TrySetResult();
                            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        }

                        NamedPipeServerStream connectedServer = server;
                        requests.Add(Task.Run(() => ProcessConnectionAsync(connectedServer, commandGate, presentationGate, cancellationToken), CancellationToken.None));
                        server = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("SingleInstanceHelper", "Activation listener error", nameof(ActivationListenerLoopAsync), ex);
                        await Task.Delay(TimeSpan.FromMilliseconds(AppConstants.Timing.SingleInstanceListenerRetryDelayMs), _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (server != null)
                            await server.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                try
                {
                    await Task.WhenAll(requests).ConfigureAwait(false);
                }
                finally
                {
                    await DrainActivationAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessConnectionAsync(NamedPipeServerStream server, SemaphoreSlim commandGate, SemaphoreSlim presentationGate, CancellationToken cancellationToken)
        {
            try
            {
                await using var ownedServer = server;
                string? payload;
                using (var readDeadline = new PipeDeadline(_requestReadTimeoutMs, _timeProvider, cancellationToken))
                {
                    try
                    {
                        byte[]? bytes = await ReadFramedPayloadAsync(server, MaxRequestBytes, readDeadline.Token).ConfigureAwait(false);
                        payload = bytes == null ? null : Encoding.UTF8.GetString(bytes);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _requestReadTimedOutSource?.TrySetResult();
                        _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ActivationSignalIgnored} | reason=request-read-timeout timeoutMs={_requestReadTimeoutMs}");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(payload))
                    return;

                _requestProcessedSource?.TrySetResult();
                if (string.Equals(payload, ActivationPayload, StringComparison.Ordinal))
                {
                    await ExecuteActivationConnectionAsync(server, cancellationToken).ConfigureAwait(false);
                }
                else if (string.Equals(payload, OwnerProbePayload, StringComparison.Ordinal))
                {
                    await RespondToOwnerProbeAsync(server, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    SemaphoreSlim gate = ShellActivation.TryParsePayload(payload, out _) ? presentationGate : commandGate;
                    await ExecuteCommandConnectionAsync(server, payload, gate, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.Warning("SingleInstanceHelper", "Activation request failed", nameof(ProcessConnectionAsync), ex);
            }
        }

        private static Task WriteResponseAsync(NamedPipeServerStream server, SingleInstanceCommandResult response, CancellationToken cancellationToken) =>
            WriteFramedPayloadAsync(server, SingleInstanceCommandResultParser.Serialize(response), cancellationToken);

        internal const int MaxDeviceWaitTimeoutMs = 600_000;

        internal static int ResolveCommandTimeoutMs(string payload, int transportBudgetMs)
        {
            if (ShellActivation.TryParsePayload(payload, out _)) return transportBudgetMs;
            if (CliCommand.TryFromPipePayload(payload, out var command) && command.Action == CliAction.WaitForDevice &&
                int.TryParse(command.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int waitMs) && waitMs > 0)
            {
                int boundedWaitMs = Math.Min(waitMs, MaxDeviceWaitTimeoutMs);
                return (int)Math.Min(int.MaxValue, (long)transportBudgetMs + boundedWaitMs);
            }
            return transportBudgetMs;
        }

        private bool SignalExistingInstance(string payloadText, CancellationToken cancellationToken = default)
        {
            bool submitted = false;
            try
            {
                using var client = new NamedPipeClientStream(".", _activationPipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
                client.ConnectAsync(AppConstants.Timing.SingleInstanceConnectTimeoutMs, cancellationToken).GetAwaiter().GetResult();
                bool isActivation = string.Equals(payloadText, ActivationPayload, StringComparison.Ordinal);
                bool isShellAction = ShellActivation.TryParsePayload(payloadText, out _);
                uint activationServerProcessId = 0;
                if (isActivation || isShellAction)
                {
                    activationServerProcessId = TryGrantForegroundPermission(client);
                }

                int responseTimeoutMs = isActivation || isShellAction
                    ? _activationResponseTimeoutMs
                    : ResolveCommandTimeoutMs(payloadText, _responseTimeoutMs);
                using var responseTimeout = new PipeDeadline(responseTimeoutMs, _timeProvider, cancellationToken);
                if (Encoding.UTF8.GetByteCount(payloadText) > MaxRequestBytes)
                {
                    throw new InvalidDataException("The forwarded request exceeds the IPC message limit.");
                }

                submitted = true;
                WriteFramedPayloadAsync(client, Encoding.UTF8.GetBytes(payloadText), responseTimeout.Token).GetAwaiter().GetResult();

                if (!TryReadResponse(client, responseTimeout.Token, out SingleInstanceCommandResult response))
                {
                    LastSignalFailureKind = SingleInstanceSignalFailureKind.InvalidResponse;
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardResponseInvalid} | targetPipe={_activationPipeName}");
                    }
                    return false;
                }

                if (response.ProtocolVersion != ResponseProtocolVersion)
                    response = new(6, ErrorCode: "forwarded-protocol-mismatch",
                        ErrorMessage: "The running AudioPilot instance uses an incompatible CLI forwarding protocol.",
                        ProtocolVersion: response.ProtocolVersion);

                LastSignalExitCode = response.ExitCode;
                LastSignalOutput = response.Output;
                LastSignalErrorCode = response.ErrorCode;
                LastSignalErrorMessage = response.ErrorMessage;
                LastSignalProtocolVersion = response.ProtocolVersion;
                LastSignalFailureKind = SingleInstanceSignalFailureKind.None;
                if (string.Equals(response.ErrorCode, HeadlessBusyErrorCode, StringComparison.Ordinal))
                {
                    if (activationServerProcessId == 0)
                        _ = GetNamedPipeServerProcessId(client.SafePipeHandle, out activationServerProcessId);
                    Process? owner = null;
                    try
                    {
                        if (activationServerProcessId != 0)
                        {
                            owner = Process.GetProcessById(checked((int)activationServerProcessId));
                            owner.EnableRaisingEvents = true;
                            _headlessOwnerProcess = owner;
                            owner = null;
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        // Do not latch observation failure: subsequent handoff polls can still retry mutex acquisition.
                        _logger.Debug("SingleInstanceHelper", () => $"headless-owner-observation-unavailable | reason={ex.GetType().Name}");
                    }
                    finally { owner?.Dispose(); }
                }

                if (_logger.IsEnabled(LogLevel.Info))
                {
                    _logger.Info("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardResponseReceived} | targetPipe={_activationPipeName} exitCode={response.ExitCode} errorCode={(response.ErrorCode ?? "none")} protocolVersion={response.ProtocolVersion}");
                }

                if (isActivation && response.ExitCode == 0)
                {
                    TryActivateServerWindow(activationServerProcessId);
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                LastSignalFailureKind = submitted ? SingleInstanceSignalFailureKind.OutcomeUnknown : SingleInstanceSignalFailureKind.ConnectionFailed;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | targetPipe={_activationPipeName} exceptionType={ex.GetType().Name}");
                }
                _logger.Trace("SingleInstanceHelper", () => $"Failed to signal existing instance: {ex.GetType().Name}");
                return false;
            }
            catch (OperationCanceledException ex)
            {
                LastSignalFailureKind = submitted ? SingleInstanceSignalFailureKind.OutcomeUnknown : SingleInstanceSignalFailureKind.ConnectionFailed;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | targetPipe={_activationPipeName} exceptionType={ex.GetType().Name}");
                }
                _logger.Trace("SingleInstanceHelper", () => $"Failed to signal existing instance: {ex.GetType().Name}");
                return false;
            }
            catch (Exception ex)
            {
                LastSignalFailureKind = submitted ? SingleInstanceSignalFailureKind.OutcomeUnknown : SingleInstanceSignalFailureKind.ConnectionFailed;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.Warning("SingleInstanceHelper", () => $"{AppConstants.Audio.LogEvents.SingleInstance.ForwardConnectFailed} | targetPipe={_activationPipeName} exceptionType={ex.GetType().Name}");
                }
                _logger.Trace("SingleInstanceHelper", () => $"Failed to signal existing instance: {ex.GetType().Name}");
                return false;
            }
        }

        private async Task<bool> RaiseActivationRequestedAsync(CancellationToken cancellationToken)
        {
            Func<CancellationToken, Task<bool>>? asyncHandler = ActivationRequestedAsync;
            if (asyncHandler != null)
            {
                bool handled = false;
                foreach (Func<CancellationToken, Task<bool>> activationHandler in asyncHandler.GetInvocationList().Cast<Func<CancellationToken, Task<bool>>>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    handled |= await activationHandler(cancellationToken).ConfigureAwait(false);
                }

                return handled;
            }

            EventHandler? handler = ActivationRequested;
            if (handler == null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            handler(this, EventArgs.Empty);
            return true;
        }

        private uint TryGrantForegroundPermission(NamedPipeClientStream client)
        {
            try
            {
                var pipeHandle = client.SafePipeHandle;
                if (pipeHandle.IsInvalid || !GetNamedPipeServerProcessId(pipeHandle, out uint serverProcessId))
                {
                    _logger.Debug("SingleInstanceHelper", "activation-foreground-grant-skipped | reason=server-process-unavailable");
                    return 0;
                }

                if (!AllowSetForegroundWindow(serverProcessId))
                {
                    _logger.Debug("SingleInstanceHelper", "activation-foreground-grant-skipped | reason=permission-denied");
                }

                return serverProcessId;
            }
            catch (Exception ex)
            {
                _logger.Debug("SingleInstanceHelper", () => $"activation-foreground-grant-skipped | reason={ex.GetType().Name}");
                return 0;
            }
        }

        private void TryActivateServerWindow(uint serverProcessId)
        {
            if (serverProcessId == 0 || serverProcessId > int.MaxValue)
            {
                return;
            }

            try
            {
                using Process serverProcess = Process.GetProcessById((int)serverProcessId);
                serverProcess.Refresh();
                nint windowHandle = serverProcess.MainWindowHandle;
                if (windowHandle == nint.Zero)
                {
                    _logger.Debug("SingleInstanceHelper", "activation-window-handoff-skipped | reason=window-unavailable");
                    return;
                }

                if (IsIconic(windowHandle))
                {
                    _ = ShowWindowAsync(windowHandle, ShowWindowRestore);
                }

                if (!SetForegroundWindow(windowHandle))
                {
                    _logger.Debug("SingleInstanceHelper", "activation-window-handoff-skipped | reason=foreground-denied");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("SingleInstanceHelper", () => $"activation-window-handoff-skipped | reason={ex.GetType().Name}");
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipeHandle, out uint serverProcessId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AllowSetForegroundWindow(uint processId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(nint windowHandle);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(nint windowHandle);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindowAsync(nint windowHandle, int command);

        private const int ShowWindowRestore = 9;

        private static bool TryReadResponse(
            NamedPipeClientStream client,
            CancellationToken cancellationToken,
            out SingleInstanceCommandResult response)
        {
            byte[]? payload = ReadFramedPayloadAsync(client, MaxResponseBytes, cancellationToken, requireComplete: true).GetAwaiter().GetResult();
            response = default;
            return payload != null && SingleInstanceCommandResultParser.TryParse(payload, out response);
        }

        private static string GetRequestProtocolVersion(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "activation";
            }

            if (ShellActivation.TryParsePayload(payload, out _)) return "shell-v1";
            return TryReadJsonProperty(payload, "protocolVersion") ?? "unknown";
        }

        private static string GetRequestAction(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "activation";
            }

            if (ShellActivation.TryParsePayload(payload, out ShellAction action)) return "shell-" + ShellActivation.GetName(action);
            string? rawAction = TryReadJsonProperty(payload, "action");
            if (string.IsNullOrWhiteSpace(rawAction))
            {
                return "unknown";
            }

            // Only emit defined CLI action tokens verbatim; reject/mask arbitrary strings so untrusted payloads cannot inject log lines.
            return Enum.TryParse(rawAction, ignoreCase: true, out CliAction parsedAction) && Enum.IsDefined(parsedAction)
                ? rawAction
                : "<invalid>";
        }

        private static string? TryReadJsonProperty(string payload, string propertyName)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty(propertyName, out JsonElement property))
                {
                    return null;
                }

                return property.ValueKind switch
                {
                    JsonValueKind.String => property.GetString(),
                    JsonValueKind.Number => property.GetRawText(),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task WriteFramedPayloadAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
        {
            var lengthBytes = BitConverter.GetBytes(payload.Length);
            await stream.WriteAsync(lengthBytes.AsMemory(0, lengthBytes.Length), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<byte[]?> ReadFramedPayloadAsync(Stream stream, int maxPayloadBytes, CancellationToken cancellationToken, bool requireComplete = false)
        {
            var lengthBytes = new byte[sizeof(int)];
            if (!await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false))
            {
                if (requireComplete) throw new EndOfStreamException("The peer disconnected before sending the response header.");
                return null;
            }

            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > maxPayloadBytes)
            {
                return null;
            }

            var payloadBytes = new byte[length];
            if (!await ReadExactlyAsync(stream, payloadBytes, cancellationToken).ConfigureAwait(false))
            {
                if (requireComplete) throw new EndOfStreamException("The peer disconnected before sending the complete response.");
                return null;
            }

            return payloadBytes;
        }

        private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }
    }
}
