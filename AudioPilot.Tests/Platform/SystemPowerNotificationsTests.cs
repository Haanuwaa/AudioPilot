using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using Microsoft.Win32;

namespace AudioPilot.Tests.Platform;

public sealed class SystemPowerNotificationsTests
{
    [Fact]
    public Task PowerCallbackDeliversSuspendAndResumeOnTheDispatcherInOrder() =>
        SharedStaDispatcherHost.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var delivered = new List<(PowerModes Mode, bool OnDispatcher)>();
            void Receive(PowerModes mode) => delivered.Add((mode, dispatcher.CheckAccess()));

            await Task.Run(() =>
            {
                SystemPowerNotifications.QueueOnDispatcher(dispatcher, Logger.Instance, PowerModes.Suspend, Receive);
                SystemPowerNotifications.QueueOnDispatcher(dispatcher, Logger.Instance, PowerModes.Resume, Receive);
            }, TestContext.Current.CancellationToken);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal([(PowerModes.Suspend, true), (PowerModes.Resume, true)], delivered);
        });

    [Fact]
    public void DispatcherShutdownDropsQueuedAndLatePowerNotifications()
    {
        TestExecutionGuards.RunIsolatedSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int delivered = 0;
            void Receive(PowerModes _) => delivered++;
            SystemPowerNotifications.QueueOnDispatcher(dispatcher, Logger.Instance, PowerModes.Suspend, Receive);
            dispatcher.InvokeShutdown();
            SystemPowerNotifications.QueueOnDispatcher(dispatcher, Logger.Instance, PowerModes.Resume, Receive);

            Assert.Equal(0, delivered);
        });
    }

    [Fact]
    public void BackgroundCallbackSubscriptionDisposesAcrossThreadsWithoutRetainingItsSubscriber()
    {
        WeakReference subscriber = RegisterAndDispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(subscriber.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndDispose()
    {
        var observer = new Observer();
        var weak = new WeakReference(observer);
        IDisposable subscription = SystemPowerNotifications.RegisterCallback(observer.Notify);
        Task.Run(subscription.Dispose).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        subscription.Dispose();
        return weak;
    }

    private sealed class Observer
    {
        private int _notifications;
        internal void Notify(PowerModes _) => Interlocked.Increment(ref _notifications);
    }
}
