using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioPilot.Tests.Platform;

public sealed class NativeWifiScannerTests
{
    [Fact]
    public void NotificationCallbackBoundary_ContainsManagedFailures()
    {
        bool succeeded = NativeWifiScanner.ExecuteNotificationCallbackForTests(
            static () => throw new InvalidOperationException("injected callback failure"));

        Assert.False(succeeded);
    }

    [Fact]
    public void GetAvailableSsids_ReturnsHashSet_RegardlessOfScanResult()
    {
        HashSet<string> ssids = NativeWifiScanner.GetAvailableSsids(logger: null);
        Assert.NotNull(ssids);
    }

    [Fact]
    public async Task GetAvailableSsidsAsync_ReturnsHashSet_RegardlessOfScanResult()
    {
        HashSet<string> ssids = await NativeWifiScanner.GetAvailableSsidsAsync(logger: null);
        Assert.NotNull(ssids);
    }

    [Fact]
    public void GetAvailableSsids_UsesCaseInsensitiveComparer()
    {
        HashSet<string> ssids = NativeWifiScanner.GetAvailableSsids(logger: null);
        Assert.NotNull(ssids);
    }

    [Fact]
    public void WlanInterfaceInfo_UsesNativeUnicodeLayout()
    {
        Type interfaceInfoType = typeof(NativeWifiScanner).GetNestedType("WLAN_INTERFACE_INFO", BindingFlags.NonPublic)!;

        Assert.NotNull(interfaceInfoType);
        Assert.Equal(532, Marshal.SizeOf(interfaceInfoType));
    }

    [Fact]
    public void ConvertSsidToString_ClampsMalformedLengthToNativeBufferSize()
    {
        Type ssidType = typeof(NativeWifiScanner).GetNestedType("WLAN_AVAILABLE_NETWORK_DOT11_SSID", BindingFlags.NonPublic)!;
        object ssid = Activator.CreateInstance(ssidType)!;
        ssidType.GetField("uSSIDLength", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(ssid, 64u);
        ssidType.GetField("ucSSID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(ssid, Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz123456"));
        MethodInfo method = typeof(NativeWifiScanner).GetMethod("ConvertSsidToString", BindingFlags.Static | BindingFlags.NonPublic)!;

        string result = (string)method.Invoke(null, [ssid])!;

        Assert.Equal("abcdefghijklmnopqrstuvwxyz123456", result);
    }
}
