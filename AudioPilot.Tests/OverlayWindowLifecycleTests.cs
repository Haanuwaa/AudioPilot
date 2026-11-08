using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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
                Assert.False(TestPrivateAccess.GetField<bool>(window, "_refreshTargetMonitorQueued"));
                TestPrivateAccess.RunTaskOnDispatcher(Task.Run(
                    () => callback.Invoke(window, [null, EventArgs.Empty]), TestContext.Current.CancellationToken));
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(TestPrivateAccess.GetField<bool>(window, "_refreshTargetMonitorQueued"));
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
    public async Task NewMessage_DuringFadeOut_RemainsVisibleUntilItsOwnTimeout()
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            var window = new OverlayWindow("First message");
            try
            {
                window.ApplyDisplayOptions(OverlayPosition.Center, 10, 0);
                window.ShowOverlay();
                await TestExecutionGuards.WaitUntilAsync(
                    () => window.IsVisible && !window.GetDisplayStateForTests().IsFadeInRunning,
                    "The first overlay presentation did not finish.", TimeSpan.FromSeconds(5));
                var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) hidden.TrySetResult(); };
                window.BeginFadeOutAndCloseForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);
                window.UpdateContent("Replacement message");
                window.ShowOverlay();
                Assert.False(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);
                var fade = Assert.IsType<Storyboard>(window.Resources["FadeOutStoryboard"]);
                TimeSpan oldFadeDuration = Assert.Single(fade.Children).Duration.TimeSpan;
                await TestExecutionGuards.AssertDoesNotCompleteWithinAsync(hidden.Task, oldFadeDuration + TimeSpan.FromMilliseconds(100),
                    "The previous fade hid the replacement message.", TestContext.Current.CancellationToken);
                await TestExecutionGuards.WaitUntilAsync(
                    () => !window.GetDisplayStateForTests().IsFadeInRunning,
                    "The replacement overlay fade-in did not finish.", TimeSpan.FromSeconds(5));

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
    public async Task Overlay_DoesNotAcceptMouseActivation()
    {
        await TestExecutionGuards.RunOnSharedStaAsync(async () =>
        {
            var window = new OverlayWindow("Noninteractive overlay");
            try
            {
                window.ShowOverlay();
                await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
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
