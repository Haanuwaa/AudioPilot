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
        /// <summary>
        /// Requests a driver reconnect. A true result means the request was accepted;
        /// the caller must still observe the audio endpoint becoming available.
        /// </summary>
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
        private readonly INativeAudioEndpointFactory _endpointFactory = NativeAudioInteropHelper.EndpointFactory;
        private readonly RememberedBluetoothEndpointCache _rememberedAudioEndpoints = new();

        internal BluetoothAudioEndpointReconnectFallback(Logger logger, INativeAudioEndpointFactory endpointFactory)
            : this(logger)
        {
            _endpointFactory = endpointFactory;
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
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            using MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All);
            List<MMDevice> devices = AudioDeviceCollectionHelper.MaterializeDevices(endpoints,
                (_, ex) => LogEndpointFailure(opId, kind, "enumeration", ex));
            try
            {
                return TryReconnectEndpoints(devices, expectedDeviceName, opId, kind, cancellationToken);
            }
            finally
            {
                AudioDeviceCollectionHelper.DisposeDevices(devices,
                    (_, ex) => LogEndpointFailure(opId, kind, "dispose", ex));
            }
        }

        /// <summary>Reconnects the selected device's audio profiles using one shared inventory for cached and uncached selection.</summary>
        internal bool TryReconnectEndpoints(
            IReadOnlyList<MMDevice> endpoints, string expectedDeviceName, string opId, string kind, CancellationToken cancellationToken)
        {
            string normalizedExpected = BluetoothReconnectService.NormalizeForMatch(expectedDeviceName);
            _rememberedAudioEndpoints.TryGetEndpointId(normalizedExpected, out string cachedId);
            var candidates = new List<(MMDevice Candidate, string MatchReason)>();
            MMDevice? cachedCandidate = null;
            foreach (MMDevice endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((endpoint.State & (DeviceState.Disabled | DeviceState.Active)) != 0) continue;
                    string reason = BluetoothReconnectService.ResolveMatchReason(endpoint.FriendlyName, expectedDeviceName, normalizedExpected);
                    if (string.IsNullOrEmpty(reason)) continue;
                    candidates.Add((endpoint, reason));
                    if (string.Equals(endpoint.ID, cachedId, StringComparison.OrdinalIgnoreCase)) cachedCandidate = endpoint;
                }
                catch (Exception ex)
                {
                    LogEndpointFailure(opId, kind, "selection", ex);
                }
            }

            if (cachedCandidate == null) _rememberedAudioEndpoints.ForgetEndpointId(normalizedExpected);
            List<(MMDevice Candidate, string MatchReason)> ordered = BluetoothReconnectService.OrderCandidatesByMatchPriority(candidates);
            if (cachedCandidate != null)
            {
                int cachedIndex = ordered.FindIndex(item => ReferenceEquals(item.Candidate, cachedCandidate));
                var cached = ordered[cachedIndex];
                ordered.RemoveAt(cachedIndex);
                ordered.Insert(0, cached);
            }

            var requestedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var topologyResults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach ((MMDevice candidate, string reason) in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string endpointId = candidate.ID;
                    if (!requestedEndpoints.Add(endpointId)) continue;
                    if ((candidate.State & (DeviceState.Disabled | DeviceState.Active)) != 0) continue;
                    string source = ReferenceEquals(candidate, cachedCandidate) ? "cached-id" : "full-scan";
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackStart} | opId={opId} kind={kind} endpointId={LogPrivacy.Id(endpointId)} reason={reason} source={source}");
                    TryGetConnectedBluetoothTopologyDeviceId(endpointId, out string topologyId);
                    if (topologyResults.TryGetValue(topologyId, out int previousTopologyHr) && previousTopologyHr >= 0) continue;
                    List<(MMDevice Endpoint, string Id, string TopologyId)> companions = ResolveCompanions(
                        endpoints, endpointId, topologyId, requestedEndpoints, opId, kind, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    bool accepted = TryInvokeBluetoothReconnect(endpointId, topologyId, topologyResults, out int hr, out string route, out string diagnostic);
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result={(accepted ? "request-accepted" : "failed")} source={source} route={route} hresult=0x{hr:X8} {diagnostic}");

                    int companionRequestsAccepted = ReconnectCompanions(companions, requestedEndpoints, topologyResults, opId, kind, cancellationToken);
                    if (accepted || companionRequestsAccepted > 0)
                    {
                        _rememberedAudioEndpoints.RememberEndpointId(normalizedExpected, endpointId);
                        _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=request-accepted source={source} primaryAccepted={accepted} companionRequestsAccepted={companionRequestsAccepted} endpointReady=unconfirmed");
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogEndpointFailure(opId, kind, "primary", ex);
                }
            }

            _rememberedAudioEndpoints.ForgetEndpointId(normalizedExpected);
            _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=no-accepted-request candidates={ordered.Count}");
            return false;
        }

        /// <summary>Resolves physical identity and topology before reconnect requests can invalidate endpoint metadata.</summary>
        private List<(MMDevice Endpoint, string Id, string TopologyId)> ResolveCompanions(
            IReadOnlyList<MMDevice> endpoints, string primaryId, string primaryTopologyId,
            HashSet<string> requestedEndpoints,
            string opId, string kind, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LooksLikeBluetoothDeviceId(primaryTopologyId))
            {
                _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=companions-skipped reason=non-bluetooth-topology");
                return [];
            }
            if (!TryGetContainerId(primaryId, out Guid containerId, out string primaryDiagnostic))
            {
                _logger.Info("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=companions-skipped reason=identity-unavailable endpointId={LogPrivacy.Id(primaryId)} {primaryDiagnostic}");
                return [];
            }

            var companions = new List<(MMDevice Endpoint, string Id, string TopologyId)>();
            foreach (MMDevice endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string endpointId = endpoint.ID;
                    if (requestedEndpoints.Contains(endpointId)
                        || (endpoint.State & (DeviceState.Disabled | DeviceState.Active)) != 0) continue;
                    if (!TryGetContainerId(endpointId, out Guid companionContainerId, out string diagnostic))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=companion-skipped reason=identity-unavailable endpointId={LogPrivacy.Id(endpointId)} {diagnostic}");
                        continue;
                    }
                    if (companionContainerId != containerId) continue;
                    if (!TryGetConnectedBluetoothTopologyDeviceId(endpointId, out string topologyId)
                        || !LooksLikeBluetoothDeviceId(topologyId))
                    {
                        _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=companion-skipped reason=bluetooth-topology-unavailable endpointId={LogPrivacy.Id(endpointId)}");
                        continue;
                    }

                    companions.Add((endpoint, endpointId, topologyId));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogEndpointFailure(opId, kind, "companion-identity", ex);
                }
            }

            _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=companions-resolved count={companions.Count}");
            return companions;
        }

        private int ReconnectCompanions(
            List<(MMDevice Endpoint, string Id, string TopologyId)> companions,
            HashSet<string> requestedEndpoints, Dictionary<string, int> topologyResults,
            string opId, string kind, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int accepted = 0;
            foreach ((MMDevice endpoint, string endpointId, string topologyId) in companions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!requestedEndpoints.Add(endpointId)
                        || (endpoint.State & (DeviceState.Disabled | DeviceState.Active)) != 0) continue;
                    if (topologyResults.TryGetValue(topologyId, out int previousTopologyHr) && previousTopologyHr >= 0) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    bool success = TryInvokeBluetoothReconnect(endpointId, topologyId, topologyResults, out int hr, out string route, out string diagnostic);
                    if (success) accepted++;
                    _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result={(success ? "request-accepted" : "failed")} source=companion endpointId={LogPrivacy.Id(endpointId)} route={route} hresult=0x{hr:X8} {diagnostic}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogEndpointFailure(opId, kind, "companion", ex);
                }
            }

            return accepted;
        }

        private bool TryGetContainerId(string endpointId, out Guid containerId, out string diagnostic)
        {
            containerId = Guid.Empty;
            string stage = "endpoint-open";
            diagnostic = $"stage={stage} hresult=unavailable";
            try
            {
                if (!_endpointFactory.TryCreate(endpointId, out INativeAudioEndpoint? endpoint)) return false;
                using (endpoint)
                {
                    stage = "property-store-open";
                    if (!endpoint.TryOpenPropertyStore(0, out INativePropertyStore? store, out int openHr))
                    {
                        diagnostic = $"stage={stage} hresult=0x{openHr:X8}";
                        return false;
                    }
                    using (store)
                    {
                        stage = "container-property-read";
                        var key = new NativePropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
                        int hr = store.GetValue(ref key, out NativePropVariant value);
                        using (value)
                        {
                            bool hasGuid = hr >= 0 && value.TryGetGuid(out containerId);
                            if (!hasGuid || containerId == Guid.Empty)
                            {
                                string failure = hr < 0 ? "read-failed"
                                    : value.IsEmpty ? "property-missing"
                                    : hasGuid ? "empty-guid"
                                    : "invalid-guid-value";
                                diagnostic = $"stage={stage} failure={failure} hresult=0x{hr:X8} variantType={value.vt} nullPointer={value.pointerValue == IntPtr.Zero}";
                                return false;
                            }
                            diagnostic = string.Empty;
                            return true;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostic = $"stage={stage} failure=exception error={ex.GetType().Name} hresult=0x{ex.HResult:X8}";
                return false;
            }
        }

        private void LogEndpointFailure(string opId, string kind, string stage, Exception exception)
        {
            _logger.Debug("BluetoothReconnect", () => $"{AppConstants.Audio.LogEvents.BluetoothReconnect.FallbackResult} | opId={opId} kind={kind} result=endpoint-skipped stage={stage} error={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }

        /// <summary>Reuses shared topology results while preserving each endpoint's direct fallback after a topology failure.</summary>
        private bool TryInvokeBluetoothReconnect(
            string endpointId, string topologyDeviceId, Dictionary<string, int> topologyResults,
            out int hr, out string route, out string diagnostic)
        {
            hr = 0;
            route = "none";
            diagnostic = "topologyResolved=false topologyHr=0x00000000 endpointHr=0x00000000";

            bool topologyResolved = false;
            int topologyHr = 0;
            if (!string.IsNullOrEmpty(topologyDeviceId))
            {
                topologyResolved = true;
                if (!topologyResults.TryGetValue(topologyDeviceId, out topologyHr))
                {
                    try
                    {
                        if (!TryInvokeKsReconnectProperty(topologyDeviceId, out topologyHr) && topologyHr >= 0)
                        {
                            topologyHr = ENoInterface;
                        }
                    }
                    catch (Exception ex)
                    {
                        topologyHr = ex.HResult;
                    }
                    topologyResults.Add(topologyDeviceId, topologyHr);
                }

                if (topologyHr >= 0)
                {
                    route = "topology";
                    hr = topologyHr;
                    diagnostic = $"topologyResolved=true topologyDevice={LogPrivacy.Id(topologyDeviceId)} topologyHr=0x{topologyHr:X8} endpointHr=0x00000000";
                    return true;
                }
            }

            if (TryInvokeKsReconnectProperty(endpointId, out int endpointHr))
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

            return false;
        }

        private bool TryInvokeKsReconnectProperty(string endpointId, out int hr)
        {
            hr = ENoInterface;

            if (!_endpointFactory.TryCreate(endpointId, out INativeAudioEndpoint? nativeEndpoint))
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
                    catch (Exception ex)
                    {
                        hr = ex.HResult;
                        return false;
                    }
            }
        }

        private bool TryGetConnectedBluetoothTopologyDeviceId(string endpointId, out string topologyDeviceId)
        {
            topologyDeviceId = string.Empty;

            if (!_endpointFactory.TryCreate(endpointId, out INativeAudioEndpoint? nativeEndpoint))
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
