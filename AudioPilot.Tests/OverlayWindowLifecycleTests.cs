using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed partial class OverlayWindowLifecycleTests
{
    [Fact]
    public void DisplayChange_FromWorkerThread_DoesNotAccessWindowOffDispatcher()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new OverlayWindow("Display change test");
            try
            {
                MethodInfo callback = typeof(OverlayWindow).GetMethod("OnDisplaySettingsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
                TestPrivateAccess.RunTaskOnDispatcher(Task.Run(
                    () => callback.Invoke(window, [null, EventArgs.Empty]), TestContext.Current.CancellationToken));
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [VisualIntegrationFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    public void NewMessage_DuringFadeOut_RemainsVisibleUntilItsOwnTimeout()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new OverlayWindow("First message");
            try
            {
                window.ApplyDisplayOptions(OverlayPosition.Center, 10, 0);
                window.ShowOverlay();
                PumpFor(TimeSpan.FromMilliseconds(250));
                window.BeginFadeOutAndCloseForTests();
                PumpFor(TimeSpan.FromMilliseconds(100));
                window.UpdateContent("Replacement message");
                window.ShowOverlay();
                PumpFor(TimeSpan.FromMilliseconds(650));

                Assert.True(window.IsVisible);
                Assert.Equal(1, window.Opacity);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [VisualIntegrationFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "VisualWpf")]
    public void Overlay_DoesNotAcceptMouseActivation()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var window = new OverlayWindow("Noninteractive overlay");
            try
            {
                window.ShowOverlay();
                PumpFor(TimeSpan.FromMilliseconds(250));
                nint handle = new WindowInteropHelper(window).Handle;
                nint result = SendMessage(handle, 0x0021, nint.Zero, (nint)((0x0201 << 16) | 1));

                Assert.Equal((nint)3, result);
                Assert.Equal(0x08000020, GetWindowLong(handle, -20) & 0x08000020);
                Point center = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
                Assert.NotEqual(handle, WindowFromPoint(new NativePoint { X = (int)center.X, Y = (int)center.Y }));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void PumpFor(TimeSpan duration) =>
        TestPrivateAccess.RunTaskOnDispatcher(Task.Delay(duration, TestContext.Current.CancellationToken));

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
