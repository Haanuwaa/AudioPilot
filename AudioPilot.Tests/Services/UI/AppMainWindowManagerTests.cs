using System.Windows;
using AudioPilot.Tests.Helpers;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class AppMainWindowManagerTests
{
    [Fact]
    public void Hide_BeforeFirstShow_DoesNotCreateMainWindowOrHwndOwner()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            Window? originalMainWindow = application.MainWindow;
            var dialogs = new RecordingAppDialogService();
            var manager = new AppMainWindowManager(application, dialogs);
            int factoryCalls = 0;
            manager.SetWindowFactory(() =>
            {
                factoryCalls++;
                throw new InvalidOperationException("The factory must not run for Hide.");
            });

            bool hidden = manager.Hide();

            Assert.True(hidden);
            Assert.False(manager.IsCreated);
            Assert.False(manager.IsVisible);
            Assert.Null(manager.CurrentWindow);
            Assert.Same(originalMainWindow, application.MainWindow);
            Assert.Equal(0, factoryCalls);

            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
        });
    }

    [Fact]
    public void VisibilityQueries_FromBackground_DoNotSynchronouslyEnterDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            var manager = new AppMainWindowManager(application, new RecordingAppDialogService());

            (bool IsCreated, bool IsVisible, MainWindow? Window) queryResult = default;
            Exception? queryException = null;
            var queryThread = new Thread(() =>
            {
                try
                {
                    queryResult = (manager.IsCreated, manager.IsVisible, manager.CurrentWindow);
                }
                catch (Exception ex)
                {
                    queryException = ex;
                }
            })
            {
                IsBackground = true,
                Name = "AppMainWindowManager visibility query test",
            };

            queryThread.Start();
            Assert.True(queryThread.Join(TimeSpan.FromSeconds(2)));
            Assert.Null(queryException);
            Assert.False(queryResult.IsCreated);
            Assert.False(queryResult.IsVisible);
            Assert.Null(queryResult.Window);

            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
        });
    }

    [Fact]
    public void FailedCreation_IsContainedAndNextExplicitShowRetries()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            Window? originalMainWindow = application.MainWindow;
            var dialogs = new RecordingAppDialogService();
            var manager = new AppMainWindowManager(application, dialogs);
            int factoryCalls = 0;
            manager.SetWindowFactory(() =>
            {
                factoryCalls++;
                throw new InvalidOperationException("synthetic construction failure");
            });

            Task<bool> first = manager.ShowAsync(MainWindowOpenTarget.Settings);
            TestPrivateAccess.RunTaskOnDispatcher(first);
            Task<bool> second = manager.ShowAsync(MainWindowOpenTarget.Default);
            TestPrivateAccess.RunTaskOnDispatcher(second);

            Assert.False(first.Result);
            Assert.False(second.Result);
            Assert.Equal(2, factoryCalls);
            Assert.Equal(2, dialogs.ErrorMessages.Count);
            Assert.False(manager.IsCreated);
            Assert.Same(originalMainWindow, application.MainWindow);

            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
        });
    }

    [Fact]
    public void ShutdownBeforeCreation_RejectsLaterShowsWithoutCallingFactory()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            Window? originalMainWindow = application.MainWindow;
            var manager = new AppMainWindowManager(application, new RecordingAppDialogService());
            int factoryCalls = 0;
            manager.SetWindowFactory(() =>
            {
                factoryCalls++;
                throw new InvalidOperationException();
            });

            TestPrivateAccess.RunTaskOnDispatcher(manager.CloseForShutdownAsync());
            Task<bool> show = manager.ShowAsync();
            TestPrivateAccess.RunTaskOnDispatcher(show);

            Assert.False(show.Result);
            Assert.Equal(0, factoryCalls);
            Assert.Same(originalMainWindow, application.MainWindow);
            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
        });
    }

    [Fact]
    public void ConcurrentShows_ShareOnePresentationAttempt_AndLatestTargetWins()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            var manager = new AppMainWindowManager(application, new RecordingAppDialogService());
            var presentationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int presentationCalls = 0;
            manager.ShowCoreOverrideForTests = () =>
            {
                presentationCalls++;
                return presentationCompletion.Task;
            };

            Task<bool> first = manager.ShowAsync(MainWindowOpenTarget.Output);
            Task<bool> second = manager.ShowAsync(MainWindowOpenTarget.Settings);

            Assert.Equal(1, presentationCalls);
            Assert.Equal(MainWindowOpenTarget.Settings, manager.PendingOpenTargetForTests);

            presentationCompletion.SetResult(true);
            TestPrivateAccess.RunTaskOnDispatcher(Task.WhenAll(first, second));

            Assert.True(first.Result);
            Assert.True(second.Result);
            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
        });
    }

    [Fact]
    public void HideDuringPendingFirstPresentation_ClearsRevealIntent()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            var manager = new AppMainWindowManager(application, new RecordingAppDialogService());
            var presentationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.ShowCoreOverrideForTests = () => presentationCompletion.Task;

            Task<bool> show = manager.ShowAsync();

            Assert.True(manager.DesiredVisibleForTests);
            Assert.True(manager.Hide());
            Assert.False(manager.DesiredVisibleForTests);

            presentationCompletion.SetResult(true);
            TestPrivateAccess.RunTaskOnDispatcher(show);
            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
        });
    }

    [Fact]
    public void BackgroundShowThatPassedPrecheck_WhenShutdownDisposesManager_ReturnsFalseWithoutTouchingDisposedGate()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = EnsureApplication();
            var manager = new AppMainWindowManager(application, new RecordingAppDialogService());
            int factoryCalls = 0;
            manager.SetWindowFactory(() =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Factory must not run after shutdown.");
            });

            using var precheckPassed = new ManualResetEventSlim();
            using var allowDispatch = new ManualResetEventSlim();
            manager.BeforeBackgroundShowDispatchForTests = () =>
            {
                precheckPassed.Set();
                Assert.True(allowDispatch.Wait(TimeSpan.FromSeconds(2)));
            };

            Task<bool> show = Task.Run(() => manager.ShowAsync());
            Assert.True(precheckPassed.Wait(TimeSpan.FromSeconds(2)));

            TestPrivateAccess.RunTaskOnDispatcher(manager.DisposeAsync().AsTask());
            allowDispatch.Set();
            TestPrivateAccess.RunTaskOnDispatcher(show);

            Assert.False(show.Result);
            Assert.Equal(0, factoryCalls);
        });
    }

    [Fact]
    public async Task BackgroundShow_CanceledAfterPrecheck_DoesNotStartPresentation()
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            var manager = new AppMainWindowManager(EnsureApplication(), new RecordingAppDialogService());
            int presentations = 0;
            manager.ShowCoreOverrideForTests = () => { presentations++; return Task.FromResult(true); };
            using var cancellation = new CancellationTokenSource();
            manager.BeforeBackgroundShowDispatchForTests = cancellation.Cancel;
            try
            {
                bool shown = await Task.Run(() => manager.ShowAsync(cancellationToken: cancellation.Token));
                Assert.False(shown);
                Assert.Equal(0, presentations);
                Assert.False(manager.DesiredVisibleForTests);
            }
            finally { await manager.DisposeAsync(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledActivationWaitingForTransition_OnlyContinuesForAnotherLiveCaller(bool anotherCaller)
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            var manager = new AppMainWindowManager(EnsureApplication(), new RecordingAppDialogService());
            int creations = 0;
            manager.SetWindowFactory(() => { creations++; throw new InvalidOperationException("Stop before creating a visible window"); });
            var gate = TestPrivateAccess.GetField<SemaphoreSlim>(manager, "_transitionGate");
            using var cancellation = new CancellationTokenSource();
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            Task<bool> first = manager.ShowAsync(cancellationToken: cancellation.Token);
            Task<bool>? second = anotherCaller ? manager.ShowAsync() : null;
            Task<bool> presentation = TestPrivateAccess.GetField<Task<bool>>(manager, "_activeShowTask");
            cancellation.Cancel();
            gate.Release();
            try
            {
                Assert.False(await first);
                await presentation;
                if (second != null) await second;
                Assert.Equal(anotherCaller ? 1 : 0, creations);
            }
            finally { await manager.DisposeAsync(); }
        });
    }

    private static Application EnsureApplication()
    {
        return Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
    }
}
