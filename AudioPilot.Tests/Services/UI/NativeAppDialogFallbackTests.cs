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
    public void NativeParameterLayout_MatchesWindowsOnBothPointerSizes()
    {
        Type type = typeof(NativeAppDialogFallback.MessageBoxParameters);
        Assert.Equal(IntPtr.Size == 8 ? 80 : 40, Marshal.SizeOf(type));
        Assert.Equal(IntPtr.Size == 8 ? 40 : 20, Marshal.OffsetOf(type, "Style").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 48 : 24, Marshal.OffsetOf(type, "Icon").ToInt32());
    }
}
