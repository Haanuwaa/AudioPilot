using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed partial class AppDialogServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BeforeDispatcherPresentation_ObservesCancellationAndLatestContent(bool cancel)
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            using var logger = Logger.CreateInMemoryForTests();
            using var release = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int dispatchCount = 0;
            var presenter = new ControlledPresenter();
            await using var service = new AppDialogService(logger, presenter, new RecordingFallback(AppDialogResult.Cancelled), applicationDispatcherProvider: () =>
            {
                if (!dispatcher.CheckAccess() && Interlocked.Increment(ref dispatchCount) == 1)
                {
                    entered.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                }

                return dispatcher;
            });
            TestPrivateAccess.SetField(service, "_presenterRunsWithoutApplication", false);

            Task<AppDialogResult> first = service.ShowWarningAsync("original", cancellationToken: cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                if (cancel)
                {
                    cancellation.Cancel();
                    Assert.Equal(AppDialogResult.Cancelled, await first);
                    release.Set();
                    await TestPrivateAccess.GetField<TaskCompletionSource<object?>>(service, "_pumpCompletion").Task
                        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    Assert.Empty(presenter.Presented);
                }
                else
                {
                    Task<AppDialogResult> latest = service.ShowErrorAsync("latest", cancellationToken: TestContext.Current.CancellationToken);
                    release.Set();
                    await presenter.WaitForPresentationCountAsync(1);
                    Assert.Equal("latest", Assert.Single(presenter.Presented).Message);
                    presenter.CompleteActive(AppDialogResult.Acknowledged);
                    Assert.Equal(AppDialogResult.Acknowledged, await first);
                    Assert.Equal(AppDialogResult.Acknowledged, await latest);
                }
            }
            finally
            {
                release.Set();
            }
        });
    }
}
