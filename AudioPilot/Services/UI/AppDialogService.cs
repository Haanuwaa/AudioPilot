using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AudioPilot.Logging;

namespace AudioPilot.Services.UI
{
    internal sealed class AppDialogService : IAppDialogService
    {
        private enum PresenterOperationKind
        {
            UpdateAcknowledgement,
            CloseAbandoned,
        }

        private sealed class DialogWorkItem(AppDialogRequest request)
        {
            public AppDialogRequest Request { get; set; } = request;
            public int RepetitionCount { get; set; } = 1;
            public long UpdateRevision { get; set; }
            public bool Abandoned { get; set; }
            public List<TaskCompletionSource<AppDialogResult>> Callers { get; } = [];
        }

        private readonly record struct PresenterOperation(
            PresenterOperationKind Kind,
            DialogWorkItem Item,
            AppDialogRequest? Request = null,
            int RepetitionCount = 0,
            long Revision = 0);

        private sealed record CallerCancellationState(
            AppDialogService Service,
            DialogWorkItem Item,
            TaskCompletionSource<AppDialogResult> Caller);

        private static IAppDialogPresenter? _defaultPresenterOverrideForTests;

        private readonly Lock _stateLock = new();
        private readonly Queue<DialogWorkItem> _queue = new();
        private readonly Queue<PresenterOperation> _presenterOperations = new();
        private readonly IAppDialogPresenter _presenter;
        private readonly INativeAppDialogFallback _fallback;
        private readonly IAppDialogSoundPlayer _soundPlayer;
        private readonly Func<Dispatcher?> _applicationDispatcherProvider;
        private readonly bool _presenterRunsWithoutApplication;
        private readonly Logger _logger;
        private readonly CancellationTokenSource _shutdownCts = new();
        private DialogWorkItem? _active;
        private TaskCompletionSource<object?>? _pumpCompletion;
        private TaskCompletionSource<object?>? _presenterPumpCompletion;
        private bool _pumpRunning;
        private bool _presenterPumpRunning;
        private bool _disposed;
        private int _soundsEnabled = 1;

        internal AppDialogService(
            Logger? logger = null,
            IAppDialogPresenter? presenter = null,
            INativeAppDialogFallback? fallback = null,
            Func<Dispatcher?>? applicationDispatcherProvider = null,
            IAppDialogSoundPlayer? soundPlayer = null)
        {
            _logger = logger ?? Logger.Instance;
            IAppDialogPresenter? configuredPresenter = presenter
                ?? Volatile.Read(ref _defaultPresenterOverrideForTests);
            _presenterRunsWithoutApplication = configuredPresenter != null;
            _presenter = configuredPresenter ?? new AppDialogWindowPresenter();
            _fallback = fallback ?? new NativeAppDialogFallback(_logger);
            _soundPlayer = soundPlayer ?? new WindowsAppDialogSoundPlayer();
            _applicationDispatcherProvider = applicationDispatcherProvider ?? (static () => Application.Current?.Dispatcher);
        }

        internal static void SetDefaultPresenterForTests(IAppDialogPresenter presenter)
        {
            Volatile.Write(ref _defaultPresenterOverrideForTests, presenter ?? throw new ArgumentNullException(nameof(presenter)));
        }

        public void SetSoundsEnabled(bool enabled)
        {
            Volatile.Write(ref _soundsEnabled, enabled ? 1 : 0);
        }

        public Task<AppDialogResult> ShowAsync(AppDialogRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(AppDialogResult.Cancelled);
            }

            var completion = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool startPump = false;
            DialogWorkItem item;
            TaskCompletionSource<object?>? pumpCompletion = null;
            TaskCompletionSource<object?>? presenterPumpCompletion = null;

            lock (_stateLock)
            {
                if (_disposed)
                {
                    return Task.FromResult(AppDialogResult.Cancelled);
                }

                if (request.IsAcknowledgement
                    && _active is { Abandoned: false }
                    && _active.Request.IsAcknowledgement)
                {
                    bool identical = IsSameAcknowledgement(_active.Request, request);
                    _active.Request = request;
                    _active.RepetitionCount = identical ? _active.RepetitionCount + 1 : 1;
                    _active.UpdateRevision++;
                    _active.Callers.Add(completion);
                    item = _active;
                    presenterPumpCompletion = EnqueuePresenterOperationLocked(new PresenterOperation(
                        PresenterOperationKind.UpdateAcknowledgement,
                        item,
                        request,
                        item.RepetitionCount,
                        item.UpdateRevision));
                }
                else if (request.IsAcknowledgement && TryGetPendingAcknowledgement(out DialogWorkItem? pendingAcknowledgement))
                {
                    DialogWorkItem pending = pendingAcknowledgement!;
                    bool identical = IsSameAcknowledgement(pending.Request, request);
                    pending.Request = request;
                    pending.RepetitionCount = identical ? pending.RepetitionCount + 1 : 1;
                    pending.Callers.Add(completion);
                    item = pending;
                }
                else
                {
                    item = new DialogWorkItem(request);
                    item.Callers.Add(completion);
                    _queue.Enqueue(item);
                }

                if (!_pumpRunning)
                {
                    _pumpRunning = true;
                    _pumpCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pumpCompletion = _pumpCompletion;
                    startPump = true;
                }
            }

            Task<AppDialogResult> callerTask;
            if (!cancellationToken.CanBeCanceled)
            {
                callerTask = completion.Task;
            }
            else
            {
                CancellationTokenRegistration registration = cancellationToken.Register(
                    static state =>
                    {
                        var cancellation = (CallerCancellationState)state!;
                        cancellation.Service.CancelCaller(cancellation.Item, cancellation.Caller);
                    },
                    new CallerCancellationState(this, item, completion));
                callerTask = AwaitCallerCompletionAsync(completion.Task, registration);
            }

            StartPresenterPumpIfNeeded(presenterPumpCompletion);
            if (startPump)
            {
                StartDialogPump(pumpCompletion!);
            }

            return callerTask;
        }

        public Task<AppDialogResult> ShowInformationAsync(string message, string caption = DialogText.Captions.Information, Window? owner = null, CancellationToken cancellationToken = default)
            => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Information, owner), cancellationToken);

        public Task<AppDialogResult> ShowSuccessAsync(string message, string caption = DialogText.Captions.Success, Window? owner = null, CancellationToken cancellationToken = default)
            => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Success, owner), cancellationToken);

        public Task<AppDialogResult> ShowWarningAsync(string message, string caption = DialogText.Captions.Warning, Window? owner = null, CancellationToken cancellationToken = default)
            => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Warning, owner), cancellationToken);

        public Task<AppDialogResult> ShowErrorAsync(string message, string caption = DialogText.Captions.Error, Window? owner = null, CancellationToken cancellationToken = default)
            => ShowAsync(AppDialogRequest.Acknowledge(message, caption, AppDialogKind.Error, owner, allowCopy: true), cancellationToken);

        public async Task<bool> ConfirmAsync(
            string message,
            string caption,
            AppDialogKind kind,
            string confirmLabel,
            string declineLabel,
            AppDialogActionStyle confirmStyle = AppDialogActionStyle.Primary,
            Window? owner = null,
            CancellationToken cancellationToken = default)
        {
            AppDialogResult result = await ShowAsync(
                AppDialogRequest.Confirm(message, caption, kind, confirmLabel, declineLabel, confirmStyle, owner),
                cancellationToken).ConfigureAwait(false);
            return result == AppDialogResult.Confirmed;
        }

        public async ValueTask DisposeAsync()
        {
            List<DialogWorkItem> pending;
            DialogWorkItem? active;
            Task pumpCompletion;
            Task presenterPumpCompletion;
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                active = _active;
                pending = [.. _queue];
                _queue.Clear();
                _presenterOperations.Clear();
                pumpCompletion = _pumpCompletion?.Task ?? Task.CompletedTask;
                presenterPumpCompletion = _presenterPumpCompletion?.Task ?? Task.CompletedTask;
            }

            _shutdownCts.Cancel();

            foreach (DialogWorkItem item in pending)
            {
                CompleteCallers(item, AppDialogResult.Cancelled);
            }

            await InvokePresenterSafelyAsync(
                () => _presenter.CloseActive(AppDialogResult.Cancelled),
                "shutdown-close").ConfigureAwait(false);
            if (active != null)
            {
                CompleteCallers(active, AppDialogResult.Cancelled);
            }

            try
            {
                await Task.WhenAll(pumpCompletion, presenterPumpCompletion)
                    .WaitAsync(TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.Warning("AppDialog", "shutdown-drain-timeout", nameof(DisposeAsync), ex);
            }

            _shutdownCts.Dispose();
        }

        private async Task PumpAsync(TaskCompletionSource<object?> completion)
        {
            try
            {
                while (true)
                {
                    DialogWorkItem? item;
                    lock (_stateLock)
                    {
                        if (_disposed || _queue.Count == 0)
                        {
                            _active = null;
                            _pumpRunning = false;
                            return;
                        }

                        item = _queue.Dequeue();
                        _active = item;
                    }

                    long started = Stopwatch.GetTimestamp();
                    AppDialogResult result;
                    string ownership = item.Request.Owner == null ? "automatic" : "explicit";
                    try
                    {
                        result = await PresentAsync(
                            item.Request,
                            kind => TryPlaySound(item, kind),
                            _shutdownCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        result = AppDialogResult.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("AppDialog", "custom-presentation-failed", nameof(PumpAsync), ex);
                        result = ShowFallback(item.Request, ex.GetType().Name);
                    }

                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_active, item))
                        {
                            _active = null;
                        }
                    }

                    CompleteCallers(item, result);
                    double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _logger.Info(
                        "AppDialog",
                        () => $"completed | kind={item.Request.Kind} result={result} repetitions={item.RepetitionCount} owner={ownership} elapsedMs={elapsedMs:0}");
                }
            }
            finally
            {
                completion.TrySetResult(null);
            }
        }

        private void StartDialogPump(TaskCompletionSource<object?> completion)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await PumpAsync(completion).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error(
                        "AppDialog",
                        () => $"dialog-pump-failed | error={ex.GetType().Name}",
                        nameof(StartDialogPump),
                        ex);
                }
            });
        }

        private async Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            Action<AppDialogKind> onPresented,
            CancellationToken cancellationToken)
        {
            if (_presenterRunsWithoutApplication)
            {
                return await _presenter.PresentAsync(request, cancellationToken, onPresented).ConfigureAwait(false);
            }

            Dispatcher? dispatcher = _applicationDispatcherProvider();
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return ShowFallback(request, "dispatcher-unavailable");
            }

            if (dispatcher.CheckAccess())
            {
                return await _presenter.PresentAsync(request, cancellationToken, onPresented);
            }

            return await dispatcher.InvokeAsync(
                () => _presenter.PresentAsync(request, cancellationToken, onPresented)).Task.Unwrap();
        }

        private void TryPlaySound(DialogWorkItem item, AppDialogKind kind)
        {
            if (Volatile.Read(ref _soundsEnabled) == 0)
            {
                return;
            }

            lock (_stateLock)
            {
                if (_disposed || item.Abandoned || !ReferenceEquals(_active, item))
                {
                    return;
                }
            }

            try
            {
                _soundPlayer.Play(kind);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "AppDialog",
                    () => $"sound-playback-failed | kind={kind} exceptionType={ex.GetType().Name}");
            }
        }

        private AppDialogResult ShowFallback(AppDialogRequest request, string reason)
        {
            try
            {
                return _fallback.Show(request, reason);
            }
            catch (Exception ex)
            {
                _logger.Error("AppDialog", () => $"native-fallback-failed | reason={reason}", nameof(ShowFallback), ex);
                return request.SafeCloseResult;
            }
        }

        private TaskCompletionSource<object?>? EnqueuePresenterOperationLocked(PresenterOperation operation)
        {
            _presenterOperations.Enqueue(operation);
            if (_presenterPumpRunning)
            {
                return null;
            }

            _presenterPumpRunning = true;
            _presenterPumpCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _presenterPumpCompletion;
        }

        private void StartPresenterPumpIfNeeded(TaskCompletionSource<object?>? completion)
        {
            if (completion != null)
            {
                _ = PumpPresenterOperationsAsync(completion);
            }
        }

        private async Task PumpPresenterOperationsAsync(TaskCompletionSource<object?> completion)
        {
            try
            {
                while (true)
                {
                    PresenterOperation operation;
                    lock (_stateLock)
                    {
                        if (_disposed || _presenterOperations.Count == 0)
                        {
                            _presenterPumpRunning = false;
                            return;
                        }

                        operation = _presenterOperations.Dequeue();
                        bool isCurrent = ReferenceEquals(_active, operation.Item);
                        bool shouldRun = operation.Kind switch
                        {
                            PresenterOperationKind.UpdateAcknowledgement =>
                                isCurrent
                                && !operation.Item.Abandoned
                                && operation.Item.UpdateRevision == operation.Revision,
                            PresenterOperationKind.CloseAbandoned => isCurrent && operation.Item.Abandoned,
                            _ => false,
                        };
                        if (!shouldRun)
                        {
                            continue;
                        }
                    }

                    switch (operation.Kind)
                    {
                        case PresenterOperationKind.UpdateAcknowledgement:
                            await InvokePresenterSafelyAsync(
                                () => _presenter.TryUpdateAcknowledgement(
                                    operation.Request!,
                                    operation.RepetitionCount),
                                "acknowledgement-update").ConfigureAwait(false);
                            break;
                        case PresenterOperationKind.CloseAbandoned:
                            await InvokePresenterSafelyAsync(
                                () => _presenter.CloseActive(AppDialogResult.Cancelled),
                                "caller-cancel-close").ConfigureAwait(false);
                            break;
                    }
                }
            }
            finally
            {
                completion.TrySetResult(null);
            }
        }

        private static bool IsSameAcknowledgement(AppDialogRequest left, AppDialogRequest right)
        {
            return left.Kind == right.Kind
                && string.Equals(left.Caption, right.Caption, StringComparison.Ordinal)
                && string.Equals(left.Message, right.Message, StringComparison.Ordinal);
        }

        private bool TryGetPendingAcknowledgement(out DialogWorkItem? acknowledgement)
        {
            acknowledgement = _queue.LastOrDefault(static item => !item.Abandoned && item.Request.IsAcknowledgement);
            return acknowledgement != null;
        }

        private void CancelCaller(
            DialogWorkItem item,
            TaskCompletionSource<AppDialogResult> caller)
        {
            TaskCompletionSource<object?>? presenterPumpCompletion = null;
            bool removed;
            lock (_stateLock)
            {
                removed = item.Callers.Remove(caller);
                if (removed && item.Callers.Count == 0 && !_disposed)
                {
                    item.Abandoned = true;
                    item.UpdateRevision++;
                    if (ReferenceEquals(_active, item))
                    {
                        presenterPumpCompletion = EnqueuePresenterOperationLocked(new PresenterOperation(
                            PresenterOperationKind.CloseAbandoned,
                            item));
                    }
                    else
                    {
                        RemoveQueuedItemLocked(item);
                    }
                }
            }

            if (!removed)
            {
                return;
            }

            caller.TrySetResult(AppDialogResult.Cancelled);
            StartPresenterPumpIfNeeded(presenterPumpCompletion);
        }

        private void RemoveQueuedItemLocked(DialogWorkItem target)
        {
            int count = _queue.Count;
            for (int index = 0; index < count; index++)
            {
                DialogWorkItem item = _queue.Dequeue();
                if (!ReferenceEquals(item, target))
                {
                    _queue.Enqueue(item);
                }
            }
        }

        private void CompleteCallers(DialogWorkItem item, AppDialogResult result)
        {
            TaskCompletionSource<AppDialogResult>[] callers;
            lock (_stateLock)
            {
                callers = [.. item.Callers];
                item.Callers.Clear();
            }

            foreach (TaskCompletionSource<AppDialogResult> caller in callers)
            {
                caller.TrySetResult(result);
            }
        }

        private static async Task<AppDialogResult> AwaitCallerCompletionAsync(
            Task<AppDialogResult> completion,
            CancellationTokenRegistration registration)
        {
            try
            {
                return await completion.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        private async Task InvokePresenterAsync(Action action)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                if (_presenterRunsWithoutApplication)
                {
                    action();
                }

                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action);
        }

        private async Task InvokePresenterSafelyAsync(Action action, string operation)
        {
            try
            {
                await InvokePresenterAsync(action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "AppDialog",
                    () => $"presenter-operation-failed | operation={operation} error={ex.GetType().Name}",
                    nameof(InvokePresenterSafelyAsync),
                    ex);
            }
        }
    }
}
