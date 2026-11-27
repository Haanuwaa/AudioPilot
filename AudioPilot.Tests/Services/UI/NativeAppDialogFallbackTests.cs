using System.Runtime.InteropServices;
using System.Windows;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed class NativeAppDialogFallbackTests
{
    [Theory]
    [InlineData(0, 32516, 0x40)]
    [InlineData(1, 32516, 0x40)]
    [InlineData(2, 32515, 0x30)]
    [InlineData(3, 32513, 0x10)]
    [InlineData(4, 32514, 0x20)]
    public void NativePresentation_UsesLiveSoundPreferenceAndPreservesTheSafeDefault(int kind, int icon, int soundStyle)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = new TestLoggerScope(nameof(NativeAppDialogFallbackTests), "native-fallback-policy.log", LogLevel.None);
            bool soundsEnabled = false;
            var styles = new List<uint>();
            var fallback = new NativeAppDialogFallback(logger.Logger, () => soundsEnabled, parameters =>
            {
                Assert.Equal(IntPtr.Size == 8 ? 80u : 40u, parameters.Size);
                Assert.Equal("Clear 🛩 assignments?", Marshal.PtrToStringUni(parameters.Text));
                Assert.Equal("AudioPilot", Marshal.PtrToStringUni(parameters.Caption));
                Assert.Equal((nint)icon, parameters.Icon);
                Assert.Equal(0, parameters.Instance);
                styles.Add(parameters.Style);
                return (int)MessageBoxResult.No;
            });
            var request = AppDialogRequest.Confirm("Clear 🛩 assignments?", "AudioPilot", (AppDialogKind)kind,
                "Clear", "Cancel", AppDialogActionStyle.Destructive);

            Assert.Equal(AppDialogResult.Declined, fallback.Show(request, "test"));
            soundsEnabled = true;
            Assert.Equal(AppDialogResult.Declined, fallback.Show(request, "test"));

            Assert.Equal([(uint)MessageBoxButton.YesNo | 0x80u | 0x100u,
                (uint)MessageBoxButton.YesNo | (uint)soundStyle | 0x100u], styles);
        });
    }

    [Fact]
    public void StartupRecoveryFallback_OnlyOffersSafeAcknowledgement()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = TestLoggerScope.CreateInMemory("native-recovery-fallback.log");
            var request = new AppDialogRequest("AudioPilot appears to be running but is not responding.",
                "Startup error", AppDialogKind.Warning,
                [
                    new AppDialogAction("Retry", AppDialogResult.Retry, isDefault: true),
                    new AppDialogAction("Terminate and continue", AppDialogResult.TerminateExisting, AppDialogActionStyle.Destructive),
                    new AppDialogAction("Exit", AppDialogResult.Cancelled, isCancel: true),
                ]);

            var fallback = new NativeAppDialogFallback(logger.Logger, showMessageBox: parameters =>
            {
                Assert.Equal((uint)MessageBoxButton.OK, parameters.Style & 0xFu);
                Assert.Contains("The action dialog could not be opened.", Marshal.PtrToStringUni(parameters.Text));
                return (int)MessageBoxResult.OK;
            });

            Assert.Equal(AppDialogResult.Cancelled, fallback.Show(request, "test"));
        });
    }

    [Fact]
    public void RecoveryWithoutVerifiedOwnerCanRetryThroughNativeFallback()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = TestLoggerScope.CreateInMemory("native-recovery-retry.log");
            var request = new AppDialogRequest("AudioPilot could not complete the request.", "Startup error", AppDialogKind.Warning,
                [new("Retry", AppDialogResult.Retry, isDefault: true), new("Exit", AppDialogResult.Cancelled, isCancel: true)]);
            var fallback = new NativeAppDialogFallback(logger.Logger, showMessageBox: parameters =>
            {
                Assert.Equal(5u, parameters.Style & 0xFu);
                return 4;
            });

            Assert.Equal(AppDialogResult.Retry, fallback.Show(request, "test"));
        });
    }

    [Fact]
    public void NativeParameterLayout_MatchesWindowsOnBothPointerSizes()
    {
        Type type = typeof(NativeAppDialogFallback.MessageBoxParameters);
        Assert.Equal(IntPtr.Size == 8 ? 80 : 40, Marshal.SizeOf(type));
        Assert.Equal(IntPtr.Size == 8 ? 40 : 20, Marshal.OffsetOf(type, "Style").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 48 : 24, Marshal.OffsetOf(type, "Icon").ToInt32());
    }
}
