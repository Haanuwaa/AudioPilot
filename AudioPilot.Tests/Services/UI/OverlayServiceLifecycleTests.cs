using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class OverlayServiceLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareForFirstUse_WithoutMainWindow_CreatesHiddenWindowOnlyWhenEnabled(bool enabled)
    {
        TestExecutionGuards.EnsureSharedWpfApplication();
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Application application = Application.Current;
            Window? originalMainWindow = application.MainWindow;
            Window[] originalWindows = [.. application.Windows.Cast<Window>()];
            using var service = new OverlayService(initiallyEnabled: enabled);
            try
            {
                application.MainWindow = null;
                service.PrepareForFirstUse();
                TestPrivateAccess.RunTaskOnDispatcher(application.Dispatcher.InvokeAsync(
                    () => { }, DispatcherPriority.ContextIdle).Task);

                Assert.Equal(enabled, service.HasPresenterForTests);
                if (!enabled)
                {
                    Assert.Empty(application.Windows.Cast<Window>().Except(originalWindows));
                    Assert.Null(application.MainWindow);
                    return;
                }

                OverlayWindow overlay = Assert.IsType<OverlayWindow>(Assert.Single(
                    application.Windows.Cast<Window>().Except(originalWindows)));
                Assert.Null(application.MainWindow);
                Assert.False(overlay.IsVisible);
                Assert.False(overlay.IsLoaded);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(overlay).Handle);

                service.Dispose();
                TestPrivateAccess.RunTaskOnDispatcher(application.Dispatcher.InvokeAsync(
                    () => { }, DispatcherPriority.ContextIdle).Task);
                Assert.DoesNotContain(overlay, application.Windows.Cast<Window>());
                Assert.Null(application.MainWindow);
            }
            finally
            {
                application.MainWindow = originalMainWindow;
            }
        });
    }
}
