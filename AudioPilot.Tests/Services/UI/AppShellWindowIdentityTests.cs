using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

[Collection("WpfApplicationIsolation")]
public sealed partial class AppShellWindowIdentityTests
{
    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId, out nint propertyStore);

    [Fact]
    public void WindowRelaunchMetadataUsesCurrentExecutableAndRoundTripsUnicodeAndSpaces()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            const string executable = @"C:\Audio utilities\音声\AudioPilot.exe";
            var window = new Window();
            try
            {
                nint handle = new WindowInteropHelper(window).EnsureHandle();
                AppShellIntegration.SetWindowIdentity(handle, executable);
                Guid interfaceId = typeof(IPropertyStoreNativeInterop).GUID;
                Assert.True(SHGetPropertyStoreForWindow(handle, in interfaceId, out nint pointer) >= 0);
                Assert.True(NativeAudioInteropHelper.ComActivator.TryWrapTyped<IPropertyStoreNativeInterop>(
                    pointer, out var store, out _));
                using (store)
                {
                    Dictionary<uint, string> expected = new()
                    {
                        [2] = $"\"{executable}\"",
                        [3] = executable + ",0",
                        [4] = "AudioPilot",
                        [5] = AppShellIntegration.ApplicationId,
                    };
                    foreach ((uint id, string text) in expected)
                    {
                        NativePropertyKey key = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), id);
                        Assert.True(store.Interface.GetValue(ref key, out NativePropVariant value) >= 0);
                        try
                        {
                            Assert.True(value.TryGetString(out string? actual));
                            Assert.Equal(text, actual);
                        }
                        finally { value.Dispose(); }
                    }
                }
            }
            finally { window.Close(); }
        });
    }
}
