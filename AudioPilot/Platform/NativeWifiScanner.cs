using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using AudioPilot.Logging;

namespace AudioPilot.Platform;

internal static partial class NativeWifiScanner
{
    private const uint WLAN_CLIENT_VERSION_V2 = 2;
    private const uint WLAN_NOTIFICATION_SOURCE_NONE = 0;
    private const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x00000008;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_NOT_FOUND = 1168;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1055;
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_BUSY = 2150899714;
    private const int WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL = 2;
    private const int WLAN_NOTIFICATION_ACM_SCAN_COMPLETE = 7;
    private const int WLAN_NOTIFICATION_ACM_SCAN_FAIL = 8;
    private const int DEFAULT_SCAN_WAIT_TIMEOUT_MS = 4000;
    private const int FALLBACK_SCAN_DELAY_MS = 1500;

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial uint WlanOpenHandle(
        uint clientVersion,
        IntPtr pReserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial uint WlanCloseHandle(IntPtr clientHandle, IntPtr pReserved);

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial uint WlanEnumInterfaces(
        IntPtr clientHandle,
        IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial uint WlanScan(
        IntPtr clientHandle,
        IntPtr pInterfaceGuid,
        IntPtr pDot11Ssid,
        IntPtr pIeData,
        IntPtr pReserved);

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial uint WlanGetAvailableNetworkList(
        IntPtr clientHandle,
        IntPtr pInterfaceGuid,
        uint dwFlags,
        IntPtr pReserved,
        out IntPtr ppAvailableNetworkList);

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial void WlanFreeMemory(IntPtr pMemory);

    [LibraryImport("wlanapi.dll", SetLastError = true)]
    private static partial uint WlanRegisterNotification(
        IntPtr clientHandle,
        uint dwNotifSource,
        [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate,
        WlanNotificationCallbackDelegate? funcCallback,
        IntPtr pCallbackContext,
        IntPtr pReserved,
        out uint pdwPrevNotifSource);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WlanNotificationCallbackDelegate(IntPtr notificationData, IntPtr callbackContext);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;
        public uint isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_AVAILABLE_NETWORK
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;
        public WLAN_AVAILABLE_NETWORK_DOT11_SSID dot11Ssid;
        public DOT11_BSS_TYPE dot11BssType;
        public uint uNumberOfBssids;
        public bool bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public DOT11_PHY_TYPE[] dot11PhyTypes;
        public bool bMorePhyTypes;
        public uint wlanSignalQuality;
        public bool bSecurityEnabled;
        public DOT11_AUTH_ALGORITHM dot11DefaultAuthAlgorithm;
        public DOT11_CIPHER_ALGORITHM dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_AVAILABLE_NETWORK_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_AVAILABLE_NETWORK_DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_NOTIFICATION_DATA
    {
        public uint NotificationSource;
        public uint NotificationCode;
        public Guid InterfaceGuid;
        public uint dwDataSize;
        public IntPtr pData;
    }

    private enum DOT11_BSS_TYPE
    {
        Infrastructure = 1,
        Independent = 2,
        Any = 3
    }

    private enum DOT11_PHY_TYPE
    {
        Unknown = 0,
        FHSS = 1,
        DSSS = 2,
        IRBaseband = 3,
        OFDM = 4,
        HRDSSS = 5,
        ERP = 6,
        HT = 7,
        VHT = 8,
        HE = 9,
        Dmg = 10,
    }

    private enum DOT11_AUTH_ALGORITHM
    {
        Open = 1,
        WEP = 2,
        WPA_PSK = 3,
        WPA_EAP = 4,
        RSNA_PSK = 5,
        RSNA_EAP = 6,
    }

    private enum DOT11_CIPHER_ALGORITHM
    {
        None = 0,
        WEP = 1,
        Tkip = 2,
        CCMP = 4,
    }

    public static HashSet<string> GetAvailableSsids(Logger? logger = null)
    {
        return GetAvailableSsidsAsync(CancellationToken.None, logger).GetAwaiter().GetResult();
    }

    public static async Task<HashSet<string>> GetAvailableSsidsAsync(Logger? logger = null)
    {
        return await GetAvailableSsidsAsync(CancellationToken.None, logger).ConfigureAwait(false);
    }

    public static async Task<HashSet<string>> GetAvailableSsidsAsync(CancellationToken cancellationToken, Logger? logger = null)
    {
        var ssids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint result = WlanOpenHandle(WLAN_CLIENT_VERSION_V2, IntPtr.Zero, out _, out IntPtr clientHandle);
            if (result != ERROR_SUCCESS)
            {
                logger?.Warning("NativeWifiScanner", () => GetErrorMessage(result, "Failed to open WLAN handle"));
                return ssids;
            }

            try
            {
                using WlanScanNotificationRegistration? notificationRegistration = WlanScanNotificationRegistration.TryCreate(clientHandle, logger);
                result = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out IntPtr interfaceListPtr);
                if (result != ERROR_SUCCESS)
                {
                    logger?.Warning("NativeWifiScanner", () => GetErrorMessage(result, "Failed to enumerate WLAN interfaces"));
                    return ssids;
                }

                try
                {
                    WLAN_INTERFACE_INFO_LIST interfaceList = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(interfaceListPtr);
                    int interfaceInfoSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                    IntPtr firstInterfacePtr = interfaceListPtr + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>();

                    for (int i = 0; i < interfaceList.dwNumberOfItems; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IntPtr currentInterfacePtr = firstInterfacePtr + (i * interfaceInfoSize);
                        WLAN_INTERFACE_INFO iface = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(currentInterfacePtr);
                        IntPtr interfaceGuidPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                        Marshal.StructureToPtr(iface.InterfaceGuid, interfaceGuidPtr, false);

                        try
                        {
                            notificationRegistration?.BeginScanWait(iface.InterfaceGuid);
                            result = WlanScan(clientHandle, interfaceGuidPtr, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            if (result != ERROR_SUCCESS && result != ERROR_INVALID_PARAMETER)
                            {
                                notificationRegistration?.CancelPendingScanWait(iface.InterfaceGuid);
                                if (result == ERROR_BUSY)
                                {
                                    logger?.Debug("NativeWifiScanner", () => $"WLAN interface busy interface={FormatInterfaceId(iface.InterfaceGuid)} action=skip");
                                }
                                else
                                {
                                    logger?.Warning("NativeWifiScanner", () => $"Failed to trigger WLAN scan interface={FormatInterfaceId(iface.InterfaceGuid)} error={result}");
                                }
                                continue;
                            }

                            if (result == ERROR_SUCCESS)
                            {
                                if (notificationRegistration != null)
                                {
                                    await notificationRegistration.WaitForScanCompletionAsync(iface.InterfaceGuid, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    await Task.Delay(FALLBACK_SCAN_DELAY_MS, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                notificationRegistration?.CancelPendingScanWait(iface.InterfaceGuid);
                            }

                            cancellationToken.ThrowIfCancellationRequested();

                            result = WlanGetAvailableNetworkList(clientHandle, interfaceGuidPtr, WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL, IntPtr.Zero, out IntPtr availableNetworkListPtr);
                            if (result != ERROR_SUCCESS)
                            {
                                logger?.Warning("NativeWifiScanner", () => GetErrorMessage(result, $"Failed to get available network list interface={FormatInterfaceId(iface.InterfaceGuid)}"));
                                continue;
                            }

                            try
                            {
                                WLAN_AVAILABLE_NETWORK_LIST networkList = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK_LIST>(availableNetworkListPtr);
                                int networkSize = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
                                IntPtr firstNetworkPtr = availableNetworkListPtr + Marshal.SizeOf<WLAN_AVAILABLE_NETWORK_LIST>();

                                for (int j = 0; j < networkList.dwNumberOfItems; j++)
                                {
                                    IntPtr currentNetworkPtr = firstNetworkPtr + (j * networkSize);
                                    WLAN_AVAILABLE_NETWORK network = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(currentNetworkPtr);
                                    string ssid = ConvertSsidToString(network.dot11Ssid);
                                    if (!string.IsNullOrWhiteSpace(ssid))
                                    {
                                        ssids.Add(ssid);
                                    }
                                }

                                logger?.Info("NativeWifiScanner", () => $"Found {networkList.dwNumberOfItems} networks interface={FormatInterfaceId(iface.InterfaceGuid)}");
                            }
                            finally
                            {
                                WlanFreeMemory(availableNetworkListPtr);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(interfaceGuidPtr);
                        }
                    }
                }
                finally
                {
                    WlanFreeMemory(interfaceListPtr);
                }
            }
            finally
            {
                uint closeResult = WlanCloseHandle(clientHandle, IntPtr.Zero);
                if (closeResult != ERROR_SUCCESS)
                {
                    logger?.Warning("NativeWifiScanner", () => $"Failed to close WLAN handle: error {closeResult}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.Warning("NativeWifiScanner", () => $"WiFi scan denied reason={ex.GetType().Name} locationPermissionMayBeRequired=true");
        }
        catch (Exception ex)
        {
            logger?.Warning("NativeWifiScanner", () => $"Failed to scan WiFi networks reason={ex.GetType().Name}");
        }

        return ssids;
    }

    private static string GetErrorMessage(uint errorCode, string context)
    {
        return errorCode switch
        {
            ERROR_NOT_FOUND => $"{context}: No WLAN interfaces found",
            ERROR_SERVICE_NOT_ACTIVE => $"{context}: WLAN service is not active",
            ERROR_ACCESS_DENIED => $"{context}: Access denied - insufficient permissions",
            _ => $"{context}: error {errorCode}"
        };
    }

    private static string FormatInterfaceId(Guid interfaceGuid)
    {
        return LogPrivacy.Id(interfaceGuid.ToString());
    }

    private static string ConvertSsidToString(WLAN_AVAILABLE_NETWORK_DOT11_SSID ssid)
    {
        if (ssid.uSSIDLength == 0 || ssid.ucSSID == null)
        {
            return string.Empty;
        }

        int ssidLength = (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length);
        if (ssidLength <= 0)
        {
            return string.Empty;
        }

        byte[] ssidBytes = ArrayPool<byte>.Shared.Rent(ssidLength);
        try
        {
            Array.Copy(ssid.ucSSID, ssidBytes, ssidLength);
            return Encoding.UTF8.GetString(ssidBytes, 0, ssidLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ssidBytes);
        }
    }

    internal static bool ExecuteNotificationCallbackForTests(Action callback)
        => WlanScanNotificationRegistration.ExecuteNotificationCallbackForTests(callback);

    private sealed class WlanScanNotificationRegistration(IntPtr clientHandle, Logger? logger) : IDisposable
    {
        private static readonly ConcurrentDictionary<IntPtr, WlanScanNotificationRegistration> _registrationsByHandle = new();

        private readonly Lock _lock = new();
        private readonly Dictionary<Guid, TaskCompletionSource<ScanCompletionResult>> _pendingScanCompletions = [];
        private readonly WlanNotificationCallbackDelegate _callback = HandleNotification;
        private IntPtr _registrationHandle;
        private bool _disposed;

        private enum ScanCompletionResult
        {
            Complete,
            Failed,
        }

        public static WlanScanNotificationRegistration? TryCreate(IntPtr clientHandle, Logger? logger)
        {
            var registration = new WlanScanNotificationRegistration(clientHandle, logger);
            if (registration.TryRegister())
            {
                return registration;
            }

            registration.Dispose();
            return null;
        }

        private bool TryRegister()
        {
            _registrationHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
            _registrationsByHandle[_registrationHandle] = this;
            uint result = WlanRegisterNotification(
                clientHandle,
                WLAN_NOTIFICATION_SOURCE_ACM,
                true,
                _callback,
                _registrationHandle,
                IntPtr.Zero,
                out _);

            if (result == ERROR_SUCCESS)
            {
                return true;
            }

            logger?.Warning("NativeWifiScanner", () => $"Failed to register WLAN notifications: error {result}. Falling back to timed scan wait.");
            return false;
        }

        public async Task WaitForScanCompletionAsync(Guid interfaceGuid, CancellationToken cancellationToken)
        {
            TaskCompletionSource<ScanCompletionResult>? completionSource;
            lock (_lock)
            {
                if (_disposed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }

                if (!_pendingScanCompletions.TryGetValue(interfaceGuid, out completionSource))
                {
                    return;
                }
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<ScanCompletionResult>)state!).TrySetCanceled(),
                completionSource);

            Task timeoutTask = Task.Delay(DEFAULT_SCAN_WAIT_TIMEOUT_MS, CancellationToken.None);
            Task completedTask = await Task.WhenAny(completionSource.Task, timeoutTask).ConfigureAwait(false);
            if (completedTask != completionSource.Task)
            {
                lock (_lock)
                {
                    _pendingScanCompletions.Remove(interfaceGuid);
                }

                logger?.Debug("NativeWifiScanner", () => $"Timed out waiting for WLAN scan completion interface={FormatInterfaceId(interfaceGuid)} action=continue");
                return;
            }

            try
            {
                ScanCompletionResult result = await completionSource.Task.ConfigureAwait(false);
                if (result == ScanCompletionResult.Failed)
                {
                    logger?.Warning("NativeWifiScanner", () => $"WLAN scan reported failure interface={FormatInterfaceId(interfaceGuid)}");
                }
            }
            finally
            {
                lock (_lock)
                {
                    _pendingScanCompletions.Remove(interfaceGuid);
                }
            }
        }

        public void BeginScanWait(Guid interfaceGuid)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingScanCompletions[interfaceGuid] = new TaskCompletionSource<ScanCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void CancelPendingScanWait(Guid interfaceGuid)
        {
            TaskCompletionSource<ScanCompletionResult>? completionSource;
            lock (_lock)
            {
                if (!_pendingScanCompletions.TryGetValue(interfaceGuid, out completionSource))
                {
                    return;
                }

                _pendingScanCompletions.Remove(interfaceGuid);
            }

            completionSource.TrySetResult(ScanCompletionResult.Complete);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (TaskCompletionSource<ScanCompletionResult> completionSource in _pendingScanCompletions.Values)
                {
                    completionSource.TrySetResult(ScanCompletionResult.Complete);
                }

                _pendingScanCompletions.Clear();
            }

            _registrationsByHandle.TryRemove(_registrationHandle, out _);

            _ = WlanRegisterNotification(
                clientHandle,
                WLAN_NOTIFICATION_SOURCE_NONE,
                true,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                out _);

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(_registrationHandle);
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
            catch
            {
            }
        }

        private static void HandleNotification(IntPtr notificationData, IntPtr callbackContext)
        {
            ExecuteNotificationCallbackSafely(() =>
            {
                if (notificationData == IntPtr.Zero || callbackContext == IntPtr.Zero)
                {
                    return;
                }

                if (_registrationsByHandle.TryGetValue(callbackContext, out WlanScanNotificationRegistration? registration))
                {
                    registration.OnNotificationReceived(notificationData);
                }
            });
        }

        internal static bool ExecuteNotificationCallbackForTests(Action callback)
            => ExecuteNotificationCallbackSafely(callback);

        private static bool ExecuteNotificationCallbackSafely(Action callback)
        {
            try
            {
                callback();
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Instance.Error(
                        "NativeWifiScanner",
                        () => $"wlan-notification-callback-failed | exceptionType={ex.GetType().Name}");
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        private void OnNotificationReceived(IntPtr notificationData)
        {
            WLAN_NOTIFICATION_DATA data = Marshal.PtrToStructure<WLAN_NOTIFICATION_DATA>(notificationData);
            if ((data.NotificationSource & WLAN_NOTIFICATION_SOURCE_ACM) == 0)
            {
                return;
            }

            if (data.NotificationCode != WLAN_NOTIFICATION_ACM_SCAN_COMPLETE && data.NotificationCode != WLAN_NOTIFICATION_ACM_SCAN_FAIL)
            {
                return;
            }

            TaskCompletionSource<ScanCompletionResult>? completionSource;
            lock (_lock)
            {
                if (_disposed || !_pendingScanCompletions.TryGetValue(data.InterfaceGuid, out completionSource))
                {
                    return;
                }
            }

            completionSource.TrySetResult(data.NotificationCode == WLAN_NOTIFICATION_ACM_SCAN_COMPLETE
                ? ScanCompletionResult.Complete
                : ScanCompletionResult.Failed);
        }
    }
}
