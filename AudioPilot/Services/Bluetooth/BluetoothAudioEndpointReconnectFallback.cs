using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using NAudio.CoreAudioApi;

namespace AudioPilot.Services.Bluetooth
{
    public interface IBluetoothAudioEndpointReconnectFallback
    {
        Task<bool> TryReconnectAsync(string expectedDeviceName, string opId, string kind, CancellationToken cancellationToken);
    }

    public sealed partial class BluetoothAudioEndpointReconnectFallback(Logger logger) : IBluetoothAudioEndpointReconnectFallback
    {
        private const uint ClsCtxAll = 23;
        private const uint KsPropertyTypeGet = 0x00000001;
        private const uint KsPropertyOneshotReconnect = 0;
        private const int ENoInterface = unchecked((int)0x80004002);

        private static readonly Guid KsPropertySetBluetoothAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");
        private readonly Logger _logger = logger;
        private readonly RememberedBluetoothEndpointCache _rememberedAudioEndpoints = new();

        internal readonly record struct VisibleEndpoint(string Id, string Name);

        private enum CachedEndpointReconnectResult
        {
            NotAttempted,
            Succeeded,
            ContinueScan,
        }

        public Task<bool> TryReconnectAsync(string expectedDeviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(expectedDeviceName))
            {
                return Task.FromResult(false);
            }

            return ComThreadingHelper.RunOnCoreAudioThreadAsync(
                () => TryReconnectCore(expectedDeviceName, opId, kind, cancellationToken),
                cancellationToken);
        }

        private bool TryReconnectCore(string expectedDeviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            string normalizedExpected = BluetoothReconnectService.NormalizeForMatch(expectedDeviceName);

            using var enumerator = new MMDeviceEnumerator();

            CachedEndpointReconnectResult cachedReconnectResult = TryReconnectCachedEndpoint(
                expectedDeviceName,
                normalizedExpected,
                enumerator,
                opId,
                kind,
                cancellationToken);
            if (cachedReconnectResult == CachedEndpointReconnectResult.Succeeded)
            {
                return true;
            }

            using MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                DataFlow.All,
                DeviceState.Active | DeviceState.Disabled | DeviceState.NotPresent | DeviceState.Unplugged);

            List<MMDevice> endpointDevices = AudioDeviceCollectionHelper.MaterializeDevices(endpoints);
            try
            {
                var candidates = new List<(MMDevice Candidate, string MatchReason)>();
                foreach (MMDevice endpoint in endpointDevices)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string reason = BluetoothReconnectService.ResolveMatchReason(endpoint.FriendlyName, expectedDeviceName, normalizedExpected);
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        continue;
                    }

                    candidates.Add((endpoint, reason));
                }

                List<(MMDevice Candidate, string MatchReason)> prioritizedCandidates = BluetoothReconnectService.OrderCandidatesByMatchPriority(candidates);
                if (prioritizedCandidates.Count == 0)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=no-unique-match candidates={candidates.Count}");
                    }

                    return false;
                }

                for (int index = 0; index < prioritizedCandidates.Count; index++)
                {
                    MMDevice candidate = prioritizedCandidates[index].Candidate;
                    string matchReason = prioritizedCandidates[index].MatchReason;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        int candidateAttempt = index + 1;
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackStart} | opId={opId} kind={kind} endpoint={LogPrivacy.Device(candidate.FriendlyName)} reason={matchReason} candidateAttempt={candidateAttempt} candidateTotal={prioritizedCandidates.Count}");
                    }

                    if (TryInvokeBluetoothReconnect(candidate, enumerator, out int hr, out string route, out string diagnostic))
                    {
                        RememberEndpointId(normalizedExpected, candidate.ID);

                        if (_logger.IsEnabled(LogLevel.Info))
                        {
                            _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=success source=full-scan endpoint={LogPrivacy.Device(candidate.FriendlyName)} route={route} {diagnostic}");
                        }

                        return true;
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=failed endpoint={LogPrivacy.Device(candidate.FriendlyName)} route={route} hresult=0x{hr:X8} {diagnostic}");
                    }
                }

                return false;
            }
            finally
            {
                foreach (MMDevice endpoint in endpointDevices)
                {
                    endpoint.Dispose();
                }
            }
        }

        private CachedEndpointReconnectResult TryReconnectCachedEndpoint(
            string expectedDeviceName,
            string normalizedExpected,
            MMDeviceEnumerator enumerator,
            string opId,
            string kind,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(normalizedExpected)
                || !TryGetRememberedEndpointId(normalizedExpected, out string endpointId))
            {
                return CachedEndpointReconnectResult.NotAttempted;
            }

            MMDevice? cachedCandidate = null;
            try
            {
                cachedCandidate = enumerator.GetDevice(endpointId);
                if (TryInvokeBluetoothReconnect(cachedCandidate, enumerator, out int hr, out string route, out string diagnostic))
                {
                    if (ProbeForVisibleEndpoint(
                        enumerator,
                        expectedDeviceName,
                        normalizedExpected,
                        endpointId,
                        cancellationToken,
                        out int probeChecks,
                        out string probeMatchReason))
                    {
                        if (_logger.IsEnabled(LogLevel.Info))
                        {
                            _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=success source=cached-id endpoint={LogPrivacy.Device(cachedCandidate.FriendlyName)} route={route} reason=cached-id probeChecks={probeChecks} probeMatch={probeMatchReason} {diagnostic}");
                        }

                        RememberEndpointId(normalizedExpected, endpointId);

                        return CachedEndpointReconnectResult.Succeeded;
                    }

                    ForgetRememberedEndpointId(normalizedExpected);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=provisional-miss endpoint={LogPrivacy.Device(cachedCandidate.FriendlyName)} route={route} reason=cached-id probeChecks={probeChecks} probeDelayMs={RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs} {diagnostic}");
                    }

                    return CachedEndpointReconnectResult.ContinueScan;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=failed endpoint={LogPrivacy.Device(cachedCandidate.FriendlyName)} route={route} reason=cached-id hresult=0x{hr:X8} {diagnostic}");
                }

                ForgetRememberedEndpointId(normalizedExpected);

                return CachedEndpointReconnectResult.ContinueScan;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=exception reason=cached-id endpointId={LogPrivacy.Id(endpointId)} error={ex.GetType().Name}");
                }

                ForgetRememberedEndpointId(normalizedExpected);
                return CachedEndpointReconnectResult.ContinueScan;
            }
            finally
            {
                cachedCandidate?.Dispose();
            }
        }

        private static bool ProbeForVisibleEndpoint(
            MMDeviceEnumerator enumerator,
            string expectedDeviceName,
            string normalizedExpected,
            string preferredEndpointId,
            CancellationToken cancellationToken,
            out int probeChecks,
            out string probeMatchReason)
        {
            probeChecks = 0;
            probeMatchReason = "not-visible";

            for (int attempt = 1; attempt <= RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                probeChecks = attempt;

                List<VisibleEndpoint> activeEndpoints = GetVisibleActiveEndpoints(enumerator);
                if (TryResolveVisibleEndpointMatch(activeEndpoints, expectedDeviceName, normalizedExpected, preferredEndpointId, out probeMatchReason))
                {
                    return true;
                }

                if (attempt < RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeAttempts
                    && cancellationToken.WaitHandle.WaitOne(RuntimeTuningConfig.BluetoothReconnectCachedEndpointVisibilityProbeDelayMs))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return false;
        }

        private static List<VisibleEndpoint> GetVisibleActiveEndpoints(MMDeviceEnumerator enumerator)
        {
            using MMDeviceCollection activeEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
            var endpoints = new List<VisibleEndpoint>(activeEndpoints.Count);

            for (int index = 0; index < activeEndpoints.Count; index++)
            {
                using MMDevice endpoint = activeEndpoints[index];
                endpoints.Add(new VisibleEndpoint(endpoint.ID, endpoint.FriendlyName));
            }

            return endpoints;
        }

        internal static bool TryResolveVisibleEndpointMatch(
            IReadOnlyList<VisibleEndpoint> activeEndpoints,
            string expectedDeviceName,
            string normalizedExpected,
            string? preferredEndpointId,
            out string matchReason)
        {
            matchReason = string.Empty;
            if (activeEndpoints == null || activeEndpoints.Count == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(preferredEndpointId))
            {
                for (int index = 0; index < activeEndpoints.Count; index++)
                {
                    if (string.Equals(activeEndpoints[index].Id, preferredEndpointId, StringComparison.OrdinalIgnoreCase))
                    {
                        matchReason = "active-id";
                        return true;
                    }
                }
            }

            var candidates = new List<(VisibleEndpoint Candidate, string MatchReason)>();
            for (int index = 0; index < activeEndpoints.Count; index++)
            {
                VisibleEndpoint endpoint = activeEndpoints[index];
                string reason = BluetoothReconnectService.ResolveMatchReason(endpoint.Name, expectedDeviceName, normalizedExpected);
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    candidates.Add((endpoint, reason));
                }
            }

            if (!BluetoothReconnectService.TrySelectBestUniqueMatch(candidates, out (VisibleEndpoint Candidate, string MatchReason)? best)
                || best is null)
            {
                List<(VisibleEndpoint Candidate, string MatchReason)> prioritizedCandidates = BluetoothReconnectService.OrderCandidatesByMatchPriority(candidates);
                if (prioritizedCandidates.Count == 0)
                {
                    return false;
                }

                matchReason = $"active-{prioritizedCandidates[0].MatchReason}";
                return true;
            }

            matchReason = $"active-{best.Value.MatchReason}";
            return true;
        }

        private bool TryGetRememberedEndpointId(string normalizedExpected, out string endpointId)
        {
            return _rememberedAudioEndpoints.TryGetEndpointId(normalizedExpected, out endpointId);
        }

        private void RememberEndpointId(string normalizedExpected, string endpointId)
        {
            _rememberedAudioEndpoints.RememberEndpointId(normalizedExpected, endpointId);
        }

        private void ForgetRememberedEndpointId(string normalizedExpected)
        {
            _rememberedAudioEndpoints.ForgetEndpointId(normalizedExpected);
        }

        private static bool TryInvokeBluetoothReconnect(MMDevice endpoint, MMDeviceEnumerator enumerator, out int hr, out string route, out string diagnostic)
        {
            hr = 0;
            route = "none";
            diagnostic = "topologyResolved=false topologyHr=0x00000000 endpointHr=0x00000000";

            bool topologyResolved = false;
            int topologyHr = 0;
            if (TryGetConnectedBluetoothTopologyDeviceId(endpoint, out string topologyDeviceId))
            {
                topologyResolved = true;
                MMDevice? bluetoothTopologyDevice = null;
                try
                {
                    bluetoothTopologyDevice = enumerator.GetDevice(topologyDeviceId);
                    if (TryInvokeKsReconnectProperty(bluetoothTopologyDevice, out topologyHr))
                    {
                        route = "topology";
                        hr = topologyHr;
                        diagnostic = $"topologyResolved=true topologyDevice={LogPrivacy.Id(topologyDeviceId)} topologyHr=0x{topologyHr:X8} endpointHr=0x00000000";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    topologyHr = ex.HResult;
                }
                finally
                {
                    try
                    {
                        bluetoothTopologyDevice?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            if (TryInvokeKsReconnectProperty(endpoint, out int endpointHr))
            {
                route = "endpoint";
                hr = endpointHr;
                diagnostic = $"topologyResolved={topologyResolved.ToString().ToLowerInvariant()} topologyHr=0x{topologyHr:X8} endpointHr=0x{endpointHr:X8}";
                return true;
            }

            route = topologyResolved ? "topology->endpoint" : "endpoint-only";
            hr = endpointHr != 0
                ? endpointHr
                : topologyHr != 0
                    ? topologyHr
                    : ENoInterface;

            diagnostic = $"topologyResolved={topologyResolved.ToString().ToLowerInvariant()} topologyHr=0x{topologyHr:X8} endpointHr=0x{endpointHr:X8}";

            if (hr == 0)
            {
                hr = ENoInterface;
            }

            return false;
        }

        private static bool TryInvokeKsReconnectProperty(MMDevice endpoint, out int hr)
        {
            hr = 0;

            using var endpointInfo = new MmDeviceAudioEndpointInfo(endpoint);
            if (!NativeAudioInteropHelper.EndpointFactory.TryCreate(endpointInfo, out INativeAudioEndpoint? nativeEndpoint))
            {
                return false;
            }

            Guid interfaceId = typeof(IKsControl).GUID;
            using (nativeEndpoint)
            {
                if (!nativeEndpoint.TryActivate(interfaceId, ClsCtxAll, out IActivatedNativeComObject<IKsControl>? ksObject, out hr))
                {
                    return false;
                }

                using (ksObject)
                    try
                    {
                        IKsControl ksControl = ksObject.Interface;

                        var property = new KsProperty
                        {
                            Set = KsPropertySetBluetoothAudio,
                            Id = KsPropertyOneshotReconnect,
                            Flags = KsPropertyTypeGet,
                        };

                        int size = Marshal.SizeOf<KsProperty>();
                        IntPtr propertyPtr = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(property, propertyPtr, false);
                            hr = ksControl.KsProperty(propertyPtr, (uint)size, IntPtr.Zero, 0, out _);
                            return hr >= 0;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(propertyPtr);
                        }
                    }
                    catch
                    {
                        return false;
                    }
            }
        }

        private static bool TryGetConnectedBluetoothTopologyDeviceId(MMDevice endpoint, out string topologyDeviceId)
        {
            topologyDeviceId = string.Empty;

            using var endpointInfo = new MmDeviceAudioEndpointInfo(endpoint);
            if (!NativeAudioInteropHelper.EndpointFactory.TryCreate(endpointInfo, out INativeAudioEndpoint? nativeEndpoint))
            {
                return false;
            }

            Guid topologyInterfaceId = typeof(IDeviceTopologyNativeInterop).GUID;
            using (nativeEndpoint)
            {
                if (!nativeEndpoint.TryActivate(topologyInterfaceId, ClsCtxAll, out IActivatedNativeComObject<IDeviceTopologyNativeInterop>? topologyObject, out int activateHr))
                {
                    return false;
                }

                using (topologyObject)
                {
                    IDeviceTopologyNativeInterop topology = topologyObject.Interface;
                    int countHr = topology.GetConnectorCount(out uint connectorCount);
                    if (countHr < 0 || connectorCount == 0)
                    {
                        return false;
                    }

                    string fallbackConnectedDeviceId = string.Empty;

                    for (uint index = 0; index < connectorCount; index++)
                    {
                        IActivatedNativeComObject<IConnectorNativeInterop>? connector = null;
                        try
                        {
                            if (!TryGetConnector(topology, index, out connector, out int connectorHr))
                            {
                                continue;
                            }

                            int connectedToHr = connector!.Interface.GetDeviceIdConnectedTo(out string connectedToId);
                            if (connectedToHr < 0)
                            {
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(fallbackConnectedDeviceId)
                                && !string.IsNullOrWhiteSpace(connectedToId))
                            {
                                fallbackConnectedDeviceId = connectedToId;
                            }

                            if (LooksLikeBluetoothDeviceId(connectedToId))
                            {
                                topologyDeviceId = connectedToId;
                                return true;
                            }
                        }
                        catch
                        {
                        }
                        finally
                        {
                            connector?.Dispose();
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(fallbackConnectedDeviceId))
                    {
                        topologyDeviceId = fallbackConnectedDeviceId;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetConnector(
            IDeviceTopologyNativeInterop topology,
            uint index,
            out IActivatedNativeComObject<IConnectorNativeInterop>? connector,
            out int hresult)
        {
            connector = null;
            hresult = topology.GetConnector(index, out IntPtr rawConnector);
            if (hresult < 0 || rawConnector == IntPtr.Zero)
            {
                return false;
            }

            return NativeAudioInteropHelper.ComActivator.TryWrapTyped(rawConnector, out connector, out hresult);
        }

        private static bool LooksLikeBluetoothDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return false;
            }

            return deviceId.Contains("bth", StringComparison.OrdinalIgnoreCase)
                || deviceId.Contains("bluetooth", StringComparison.OrdinalIgnoreCase);
        }

        [GeneratedComInterface]
        [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal partial interface IKsControl
        {
            [PreserveSig]
            int KsProperty(IntPtr property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KsProperty
        {
            public Guid Set;
            public uint Id;
            public uint Flags;
        }
    }
}
