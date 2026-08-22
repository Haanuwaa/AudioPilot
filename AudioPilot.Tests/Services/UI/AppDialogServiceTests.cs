using System.Windows;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class AppDialogServiceTests
{
    [Fact]
    public void Request_RejectsInvalidActionSets()
    {
        Assert.Throws<ArgumentException>(() => new AppDialogRequest(
            "message",
            "caption",
            AppDialogKind.Question,
            [new AppDialogAction("Yes", AppDialogResult.Confirmed)]));

        Assert.Throws<ArgumentException>(() => new AppDialogRequest(
            "message",
            "caption",
            AppDialogKind.Question,
            [
                new AppDialogAction("Yes", AppDialogResult.Confirmed, isDefault: true),
                new AppDialogAction("Again", AppDialogResult.Confirmed),
            ]));

        Assert.Throws<ArgumentException>(() => new AppDialogRequest(
            "message",
            "caption",
            AppDialogKind.Question,
            [
                new AppDialogAction("Continue", AppDialogResult.Confirmed, isDefault: true, isCancel: true),
                new AppDialogAction("Cancel", AppDialogResult.Cancelled, isCancel: true),
            ]));
    }

    [Fact]
    public void StandardFactories_ExposeSafeDefaultAndCloseActions()
    {
        AppDialogRequest acknowledgement = AppDialogRequest.Acknowledge("message", "caption", AppDialogKind.Error);
        AppDialogRequest confirmation = AppDialogRequest.Confirm(
            "message",
            "caption",
            AppDialogKind.Warning,
            "_Reset",
            "_Cancel",
            AppDialogActionStyle.Destructive);

        Assert.True(acknowledgement.IsAcknowledgement);
        Assert.True(acknowledgement.AllowCopy);
        Assert.Equal(AppDialogResult.Acknowledged, acknowledgement.SafeCloseResult);
        Assert.Equal(AppDialogResult.Declined, confirmation.SafeCloseResult);
        Assert.Equal(AppDialogActionStyle.Destructive, confirmation.Actions[0].Style);
    }

    [Fact]
    public async Task Queue_PreservesConfirmationOrder()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-order.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);

        Task<AppDialogResult> first = service.ShowAsync(CreateConfirmation("first"), TestContext.Current.CancellationToken);
        Task<AppDialogResult> second = service.ShowAsync(CreateConfirmation("second"), TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        presenter.CompleteActive(AppDialogResult.Confirmed);
        await presenter.WaitForPresentationCountAsync(2);
        presenter.CompleteActive(AppDialogResult.Declined);

        Assert.Equal(AppDialogResult.Confirmed, await first);
        Assert.Equal(AppDialogResult.Declined, await second);
        Assert.Equal(["first", "second"], presenter.Presented.Select(static request => request.Message));
    }

    [Fact]
    public async Task ShowAsync_ReturnsBeforeSynchronousModalPresentationCompletes()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-async-entry.log", LogLevel.None);
        using var presenter = new SynchronousBlockingPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);

        Task<Task<AppDialogResult>> invocation = Task.Factory.StartNew(
            () => service.ShowInformationAsync("message", cancellationToken: TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        Task<AppDialogResult> dialogTask = await invocation.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await presenter.WaitUntilEnteredAsync();

        Assert.False(dialogTask.IsCompleted);
        presenter.Release();
        Assert.Equal(AppDialogResult.Acknowledged, await dialogTask);
    }

    [Fact]
    public async Task InjectedPresenter_DoesNotDependOnApplicationDispatcher()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-injected-presenter.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(
            loggerScope.Logger,
            presenter,
            applicationDispatcherProvider: static () => throw new InvalidOperationException("dispatcher must not be queried"));

        Task<AppDialogResult> dialog = service.ShowInformationAsync(
            "message",
            cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        presenter.CompleteActive(AppDialogResult.Acknowledged);

        Assert.Equal(AppDialogResult.Acknowledged, await dialog);
    }

    [Fact]
    public async Task IdenticalAcknowledgements_UpdateActiveWindow_AndCompleteTogether()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-coalesce.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);
        AppDialogRequest request = AppDialogRequest.Acknowledge("same", "caption", AppDialogKind.Warning);

        Task<AppDialogResult> first = service.ShowAsync(request, TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> second = service.ShowAsync(request, TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateCountAsync(1);
        presenter.CompleteActive(AppDialogResult.Acknowledged);

        Assert.Equal(AppDialogResult.Acknowledged, await first);
        Assert.Equal(AppDialogResult.Acknowledged, await second);
        Assert.Equal(2, presenter.Updates[0].RepetitionCount);
    }

    [Fact]
    public async Task AcknowledgementsDuringConfirmation_AreCoalescedAndShownAfterward()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-confirmation-coalesce.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);

        Task<AppDialogResult> confirmation = service.ShowAsync(CreateConfirmation("confirm"), TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> oldAcknowledgement = service.ShowWarningAsync("old", "warning", cancellationToken: TestContext.Current.CancellationToken);
        Task<AppDialogResult> latestAcknowledgement = service.ShowErrorAsync("latest", "error", cancellationToken: TestContext.Current.CancellationToken);
        presenter.CompleteActive(AppDialogResult.Declined);
        await presenter.WaitForPresentationCountAsync(2);

        Assert.Equal("latest", presenter.Presented[1].Message);
        presenter.CompleteActive(AppDialogResult.Acknowledged);
        Assert.Equal(AppDialogResult.Declined, await confirmation);
        Assert.Equal(AppDialogResult.Acknowledged, await oldAcknowledgement);
        Assert.Equal(AppDialogResult.Acknowledged, await latestAcknowledgement);
    }

    [Fact]
    public async Task DifferentAcknowledgement_ReplacesContentAndResetsRepetitionCount()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-replacement.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);

        Task<AppDialogResult> first = service.ShowWarningAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> repeated = service.ShowWarningAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateCountAsync(1);
        Task<AppDialogResult> replacement = service.ShowErrorAsync("replacement", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateCountAsync(2);

        Assert.Equal(2, presenter.Updates[0].RepetitionCount);
        Assert.Equal("replacement", presenter.Updates[1].Request.Message);
        Assert.Equal(1, presenter.Updates[1].RepetitionCount);
        presenter.CompleteActive(AppDialogResult.Acknowledged);

        Assert.All(
            await Task.WhenAll(first, repeated, replacement),
            static result => Assert.Equal(AppDialogResult.Acknowledged, result));
    }

    [Fact]
    public async Task PresentationSound_PlaysOnceAndDoesNotReplayForAcknowledgementUpdates()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-sound-once.log", LogLevel.None);
        var presenter = new ControlledPresenter { InvokePresentedCallback = true };
        var soundPlayer = new RecordingSoundPlayer();
        await using var service = new AppDialogService(loggerScope.Logger, presenter, soundPlayer: soundPlayer);

        Task<AppDialogResult> first = service.ShowWarningAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> repeated = service.ShowWarningAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateCountAsync(1);
        Task<AppDialogResult> replacement = service.ShowErrorAsync("replacement", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateCountAsync(2);

        Assert.Equal([AppDialogKind.Warning], soundPlayer.Kinds);
        presenter.CompleteActive(AppDialogResult.Acknowledged);
        await Task.WhenAll(first, repeated, replacement);
    }

    [Fact]
    public async Task PresentationSound_RespectsLivePreferenceAndQueuedPresentationBoundaries()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-sound-preference.log", LogLevel.None);
        var presenter = new ControlledPresenter { InvokePresentedCallback = true };
        var soundPlayer = new RecordingSoundPlayer();
        await using var service = new AppDialogService(loggerScope.Logger, presenter, soundPlayer: soundPlayer);

        service.SetSoundsEnabled(false);
        Task<AppDialogResult> silent = service.ShowAsync(CreateConfirmation("silent"), TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Assert.Empty(soundPlayer.Kinds);

        service.SetSoundsEnabled(true);
        Task<AppDialogResult> queued = service.ShowAsync(CreateConfirmation("audible"), TestContext.Current.CancellationToken);
        presenter.CompleteActive(AppDialogResult.Declined);
        await presenter.WaitForPresentationCountAsync(2);
        Assert.Equal([AppDialogKind.Question], soundPlayer.Kinds);

        presenter.CompleteActive(AppDialogResult.Confirmed);
        Assert.Equal(AppDialogResult.Declined, await silent);
        Assert.Equal(AppDialogResult.Confirmed, await queued);
    }

    [Fact]
    public async Task PresentationSoundFailure_IsContainedAndDialogStillCompletes()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-sound-failure.log", LogLevel.None);
        var presenter = new ControlledPresenter { InvokePresentedCallback = true };
        await using var service = new AppDialogService(
            loggerScope.Logger,
            presenter,
            soundPlayer: new ThrowingSoundPlayer());

        Task<AppDialogResult> dialog = service.ShowInformationAsync(
            "message",
            cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        presenter.CompleteActive(AppDialogResult.Acknowledged);

        Assert.Equal(AppDialogResult.Acknowledged, await dialog);
    }

    [Fact]
    public async Task PresentationSound_DoesNotPlayAfterOnlyCallerCancelsBeforeFirstRender()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-sound-cancelled-before-render.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        var soundPlayer = new RecordingSoundPlayer();
        await using var service = new AppDialogService(loggerScope.Logger, presenter, soundPlayer: soundPlayer);
        using var cancellation = new CancellationTokenSource();

        Task<AppDialogResult> dialog = service.ShowWarningAsync("message", cancellationToken: cancellation.Token);
        await presenter.WaitForPresentationCountAsync(1);
        cancellation.Cancel();
        await presenter.WaitForCloseCountAsync(1);
        presenter.NotifyPresented(AppDialogKind.Warning);

        Assert.Equal(AppDialogResult.Cancelled, await dialog);
        Assert.Empty(soundPlayer.Kinds);
    }

    [Fact]
    public async Task CallerCancellation_CompletesOnlyThatCallerAndLeavesSharedDialogUsable()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-caller-cancellation.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);
        using var callerCancellation = new CancellationTokenSource();

        Task<AppDialogResult> cancelledCaller = service.ShowWarningAsync("shared", cancellationToken: callerCancellation.Token);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> remainingCaller = service.ShowWarningAsync("shared", cancellationToken: TestContext.Current.CancellationToken);
        callerCancellation.Cancel();

        Assert.Equal(AppDialogResult.Cancelled, await cancelledCaller);
        presenter.CompleteActive(AppDialogResult.Acknowledged);
        Assert.Equal(AppDialogResult.Acknowledged, await remainingCaller);
    }

    [Fact]
    public async Task SoleQueuedCallerCancellation_RemovesDialogBeforePresentation()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-queued-cancellation.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);
        using var queuedCancellation = new CancellationTokenSource();

        Task<AppDialogResult> active = service.ShowAsync(CreateConfirmation("active"), TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> cancelled = service.ShowAsync(CreateConfirmation("cancelled"), queuedCancellation.Token);
        queuedCancellation.Cancel();
        Assert.Equal(AppDialogResult.Cancelled, await cancelled);
        Task<AppDialogResult> sentinel = service.ShowAsync(CreateConfirmation("sentinel"), TestContext.Current.CancellationToken);

        presenter.CompleteActive(AppDialogResult.Confirmed);
        await presenter.WaitForPresentationCountAsync(2);
        Assert.Equal(["active", "sentinel"], presenter.Presented.Select(static request => request.Message));
        presenter.CompleteActive(AppDialogResult.Declined);

        Assert.Equal(AppDialogResult.Confirmed, await active);
        Assert.Equal(AppDialogResult.Declined, await sentinel);
    }

    [Fact]
    public async Task FinalActiveCallerCancellation_ClosesDialogAndAdvancesQueue()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-active-cancellation.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);
        using var activeCancellation = new CancellationTokenSource();

        Task<AppDialogResult> cancelled = service.ShowAsync(CreateConfirmation("cancelled"), activeCancellation.Token);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> next = service.ShowAsync(CreateConfirmation("next"), TestContext.Current.CancellationToken);
        activeCancellation.Cancel();

        Assert.Equal(AppDialogResult.Cancelled, await cancelled);
        await presenter.WaitForCloseCountAsync(1);
        await presenter.WaitForPresentationCountAsync(2);
        Assert.Equal("next", presenter.Presented[1].Message);
        presenter.CompleteActive(AppDialogResult.Confirmed);
        Assert.Equal(AppDialogResult.Confirmed, await next);
    }

    [Fact]
    public async Task CoalescedAcknowledgement_CancellingSomeCallersKeepsDialogOpen()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-mixed-cancellation.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        await using var service = new AppDialogService(loggerScope.Logger, presenter);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        Task<AppDialogResult> first = service.ShowWarningAsync("shared", cancellationToken: firstCancellation.Token);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> second = service.ShowWarningAsync("shared", cancellationToken: secondCancellation.Token);
        await presenter.WaitForUpdateCountAsync(1);
        Task<AppDialogResult> survivor = service.ShowWarningAsync("shared", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateCountAsync(2);

        firstCancellation.Cancel();
        secondCancellation.Cancel();
        Assert.Equal(AppDialogResult.Cancelled, await first);
        Assert.Equal(AppDialogResult.Cancelled, await second);
        Assert.Empty(presenter.ClosedResults);

        presenter.CompleteActive(AppDialogResult.Acknowledged);
        Assert.Equal(AppDialogResult.Acknowledged, await survivor);
    }

    [Fact]
    public async Task AcknowledgementUpdateFailure_IsContainedAndCallersStillComplete()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-update-failure.log", LogLevel.None);
        var presenter = new ControlledPresenter { ThrowOnUpdate = true };
        await using var service = new AppDialogService(loggerScope.Logger, presenter);

        Task<AppDialogResult> first = service.ShowWarningAsync("shared", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        Task<AppDialogResult> second = service.ShowWarningAsync("shared", cancellationToken: TestContext.Current.CancellationToken);
        await presenter.WaitForUpdateAttemptCountAsync(1);
        presenter.CompleteActive(AppDialogResult.Acknowledged);

        Assert.Equal(AppDialogResult.Acknowledged, await first);
        Assert.Equal(AppDialogResult.Acknowledged, await second);
    }

    [Fact]
    public async Task PresentationFailure_UsesNativeFallbackBoundaryWithoutLeakingMessageToMetadata()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-fallback.log", LogLevel.None);
        var fallback = new RecordingFallback(AppDialogResult.Acknowledged);
        await using var service = new AppDialogService(loggerScope.Logger, new ThrowingPresenter(), fallback);
        AppDialogRequest request = AppDialogRequest.Acknowledge("private path C:\\ExampleUser\\secret.txt", "caption", AppDialogKind.Error);

        AppDialogResult result = await service.ShowAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(AppDialogResult.Acknowledged, result);
        Assert.Same(request, fallback.Request);
        Assert.Equal(nameof(InvalidOperationException), fallback.Reason);
    }

    [Theory]
    [InlineData(MessageBoxResult.Yes, nameof(AppDialogResult.Retry))]
    [InlineData(MessageBoxResult.No, nameof(AppDialogResult.TerminateExisting))]
    [InlineData(MessageBoxResult.Cancel, nameof(AppDialogResult.Cancelled))]
    [InlineData(MessageBoxResult.None, nameof(AppDialogResult.Cancelled))]
    public void NativeFallback_MapsThreeActionRecoveryResults(MessageBoxResult nativeResult, string expectedName)
    {
        var request = new AppDialogRequest(
            "message",
            "caption",
            AppDialogKind.Warning,
            [
                new AppDialogAction("Retry", AppDialogResult.Retry, isDefault: true),
                new AppDialogAction("Terminate", AppDialogResult.TerminateExisting, AppDialogActionStyle.Destructive),
                new AppDialogAction("Exit", AppDialogResult.Cancelled, isCancel: true),
            ]);

        Assert.Equal(MessageBoxButton.YesNoCancel, NativeAppDialogFallback.GetButtons(request));
        AppDialogResult expected = Enum.Parse<AppDialogResult>(expectedName);
        Assert.Equal(expected, NativeAppDialogFallback.MapResult(request, nativeResult));
    }

    [Fact]
    public void OwnerResolution_PrefersExplicitThenActiveThenVisibleMainWindow()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var explicitOwner = new Window();
            var activeOwner = new Window();
            var mainOwner = new Window();
            var validOwners = new HashSet<Window> { explicitOwner, activeOwner, mainOwner };

            Assert.Same(
                explicitOwner,
                AppDialogWindowPresenter.ResolveOwnerCandidate(
                    explicitOwner,
                    [activeOwner, mainOwner],
                    mainOwner,
                    window => window != null && validOwners.Contains(window),
                    window => ReferenceEquals(window, activeOwner)));

            validOwners.Remove(explicitOwner);
            Assert.Same(
                activeOwner,
                AppDialogWindowPresenter.ResolveOwnerCandidate(
                    explicitOwner,
                    [activeOwner, mainOwner],
                    mainOwner,
                    window => window != null && validOwners.Contains(window),
                    window => ReferenceEquals(window, activeOwner)));

            validOwners.Remove(activeOwner);
            Assert.Same(
                mainOwner,
                AppDialogWindowPresenter.ResolveOwnerCandidate(
                    explicitOwner,
                    [activeOwner],
                    mainOwner,
                    window => window != null && validOwners.Contains(window),
                    static _ => false));

            mainOwner.WindowState = WindowState.Minimized;
            Assert.Null(
                AppDialogWindowPresenter.ResolveOwnerCandidate(
                    explicitOwner,
                    [activeOwner],
                    mainOwner,
                    window => window != null && validOwners.Contains(window),
                    static _ => false));
        });
    }

    [Fact]
    public async Task Dispose_CancelsActiveAndPendingCallers()
    {
        using var loggerScope = new TestLoggerScope(nameof(AppDialogServiceTests), "dialog-dispose.log", LogLevel.None);
        var presenter = new ControlledPresenter();
        var service = new AppDialogService(loggerScope.Logger, presenter);

        Task<AppDialogResult> active = service.ShowAsync(CreateConfirmation("active"), TestContext.Current.CancellationToken);
        Task<AppDialogResult> pending = service.ShowAsync(CreateConfirmation("pending"), TestContext.Current.CancellationToken);
        await presenter.WaitForPresentationCountAsync(1);
        await service.DisposeAsync();

        Assert.Equal(AppDialogResult.Cancelled, await active);
        Assert.Equal(AppDialogResult.Cancelled, await pending);
        Assert.Equal(
            AppDialogResult.Cancelled,
            await service.ShowInformationAsync("after shutdown", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static AppDialogRequest CreateConfirmation(string message) => AppDialogRequest.Confirm(
        message,
        "caption",
        AppDialogKind.Question,
        "_Continue",
        "_Cancel");

    private sealed class ControlledPresenter : IAppDialogPresenter
    {
        private readonly Lock _sync = new();
        private TaskCompletionSource<AppDialogResult>? _active;
        private Action<AppDialogKind>? _activePresentedCallback;
        private TaskCompletionSource<object?> _changed = CreateSignal();

        public List<AppDialogRequest> Presented { get; } = [];
        public List<(AppDialogRequest Request, int RepetitionCount)> Updates { get; } = [];
        public List<AppDialogResult> ClosedResults { get; } = [];
        public bool ThrowOnUpdate { get; init; }
        public bool InvokePresentedCallback { get; init; }
        public int UpdateAttemptCount { get; private set; }

        public Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken,
            Action<AppDialogKind>? onPresented = null)
        {
            lock (_sync)
            {
                Presented.Add(request);
                _active = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activePresentedCallback = onPresented;
                if (InvokePresentedCallback)
                {
                    onPresented?.Invoke(request.Kind);
                }
                SignalChanged();
                return _active.Task;
            }
        }

        public bool TryUpdateAcknowledgement(AppDialogRequest request, int repetitionCount)
        {
            lock (_sync)
            {
                UpdateAttemptCount++;
                if (ThrowOnUpdate)
                {
                    SignalChanged();
                    throw new InvalidOperationException("simulated acknowledgement update failure");
                }

                Updates.Add((request, repetitionCount));
                SignalChanged();
                return true;
            }
        }

        public void CloseActive(AppDialogResult result)
        {
            lock (_sync)
            {
                ClosedResults.Add(result);
                SignalChanged();
            }

            CompleteActive(result);
        }

        public void CompleteActive(AppDialogResult result)
        {
            TaskCompletionSource<AppDialogResult>? active;
            lock (_sync)
            {
                active = _active;
            }

            active?.TrySetResult(result);
        }

        public void NotifyPresented(AppDialogKind kind)
        {
            Action<AppDialogKind>? callback;
            lock (_sync)
            {
                callback = _activePresentedCallback;
            }

            callback?.Invoke(kind);
        }

        public Task WaitForPresentationCountAsync(int count) => WaitForAsync(() => Presented.Count >= count);
        public Task WaitForUpdateCountAsync(int count) => WaitForAsync(() => Updates.Count >= count);
        public Task WaitForUpdateAttemptCountAsync(int count) => WaitForAsync(() => UpdateAttemptCount >= count);
        public Task WaitForCloseCountAsync(int count) => WaitForAsync(() => ClosedResults.Count >= count);

        private async Task WaitForAsync(Func<bool> predicate)
        {
            while (true)
            {
                Task signal;
                lock (_sync)
                {
                    if (predicate())
                    {
                        return;
                    }

                    signal = _changed.Task;
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private void SignalChanged()
        {
            TaskCompletionSource<object?> previous = _changed;
            _changed = CreateSignal();
            previous.TrySetResult(null);
        }

        private static TaskCompletionSource<object?> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingSoundPlayer : IAppDialogSoundPlayer
    {
        public List<AppDialogKind> Kinds { get; } = [];

        public void Play(AppDialogKind kind) => Kinds.Add(kind);
    }

    private sealed class ThrowingSoundPlayer : IAppDialogSoundPlayer
    {
        public void Play(AppDialogKind kind) => throw new InvalidOperationException("simulated sound failure");
    }

    private sealed class ThrowingPresenter : IAppDialogPresenter
    {
        public Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken,
            Action<AppDialogKind>? onPresented = null) =>
            throw new InvalidOperationException("simulated custom presentation failure");

        public bool TryUpdateAcknowledgement(AppDialogRequest request, int repetitionCount) => false;

        public void CloseActive(AppDialogResult result)
        {
        }
    }

    private sealed class SynchronousBlockingPresenter : IAppDialogPresenter, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly TaskCompletionSource<object?> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppDialogResult> PresentAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken,
            Action<AppDialogKind>? onPresented = null)
        {
            _entered.TrySetResult(null);
            _release.Wait(cancellationToken);
            return Task.FromResult(AppDialogResult.Acknowledged);
        }

        public bool TryUpdateAcknowledgement(AppDialogRequest request, int repetitionCount) => true;

        public void CloseActive(AppDialogResult result) => _release.Set();

        public Task<object?> WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class RecordingFallback(AppDialogResult result) : INativeAppDialogFallback
    {
        public AppDialogRequest? Request { get; private set; }
        public string? Reason { get; private set; }

        public AppDialogResult Show(AppDialogRequest request, string reason)
        {
            Request = request;
            Reason = reason;
            return result;
        }
    }
}
