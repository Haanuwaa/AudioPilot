using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Bluetooth;

public sealed partial class BluetoothAudioEndpointReconnectFallbackTests
{
    private static readonly Guid FixtureContainer = new("15c6f8fd-45c1-4fab-94d9-362454832aa3");

    [Theory]
    [InlineData(false, "output")]
    [InlineData(true, "output")]
    [InlineData(false, "input")]
    [InlineData(true, "input")]
    public void Reconnect_ResolvesBothProfilesBeforeRequestsWithCachedAndFullScanSelection(bool cached, string kind)
    {
        var music = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        var voice = new Profile("voice", "Headset (Fixture)", "bthhfenum-voice");
        Profile primary = kind == "input" ? voice : music;
        Profile companion = kind == "input" ? music : voice;
        primary.OnReconnect = () =>
        {
            primary.ThrowOnProperty = true;
            companion.ThrowOnProperty = true;
            primary.TopologyUnavailable = true;
            companion.TopologyUnavailable = true;
        };
        using var harness = new ProfileHarness(companion, primary);
        if (cached) harness.Remember(primary.Name, primary.Id);

        Assert.True(harness.Reconnect(primary.Name, kind, TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId, companion.TopologyId], harness.Factory.Requests);
        Assert.True(harness.Cache.TryGetEndpointId(BluetoothReconnectService.NormalizeForMatch(primary.Name), out string remembered));
        Assert.Equal(primary.Id, remembered);
        harness.AssertDisposed();
    }

    [Fact]
    public void Reconnect_CompanionsRequirePhysicalIdentityAndBluetoothTopology()
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        var voice = new Profile("voice", "Different profile name", "bthhfenum-voice");
        using var harness = new ProfileHarness(primary,
            new Profile("unrelated", primary.Name, "bthenum-other") { Container = Guid.NewGuid() },
            new Profile("usb", primary.Name, "usb-speaker"),
            new Profile("empty", primary.Name, "bthenum-empty") { Container = Guid.Empty }, voice);

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId, voice.TopologyId], harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Theory]
    [InlineData("missing", "container-property-read", "failure=property-missing", "variantType=0")]
    [InlineData("empty", "container-property-read", "failure=empty-guid", "variantType=72")]
    [InlineData("wrong-type", "container-property-read", "failure=invalid-guid-value", "variantType=19")]
    [InlineData("null-pointer", "container-property-read", "failure=invalid-guid-value", "nullPointer=True")]
    [InlineData("failed-read", "container-property-read", "failure=read-failed", "hresult=0x8007001F")]
    [InlineData("exception", "container-property-read", "failure=exception", "error=COMException")]
    [InlineData("store-open", "property-store-open", "hresult=0x8007001F", "reason=identity-unavailable")]
    public void Reconnect_UnavailablePrimaryContainerPreservesAcceptanceAndLogsFailure(
        string failure, string stage, string failureDiagnostic, string valueDiagnostic)
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        switch (failure)
        {
            case "missing": primary.Container = null; break;
            case "empty": primary.Container = Guid.Empty; break;
            case "wrong-type": primary.VariantType = 19; break;
            case "null-pointer": primary.NullPointer = true; break;
            case "failed-read": primary.PropertyHresult = unchecked((int)0x8007001F); break;
            case "exception": primary.ThrowOnProperty = true; break;
            case "store-open": primary.PropertyStoreHresult = unchecked((int)0x8007001F); break;
        }
        using var loggerScope = TestLoggerScope.CreateInMemory("bluetooth-identity.log", LogLevel.Info);
        using var harness = new ProfileHarness(loggerScope.Logger, primary, new Profile("voice", "Headset (Fixture)", "bthhfenum-voice"));

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId], harness.Factory.Requests);
        harness.AssertDisposed();
        string logText = loggerScope.DisposeAndReadLogText();
        Assert.Contains("result=companions-skipped reason=identity-unavailable", logText, StringComparison.Ordinal);
        Assert.Contains($"stage={stage}", logText, StringComparison.Ordinal);
        Assert.Contains(failureDiagnostic, logText, StringComparison.Ordinal);
        Assert.Contains(valueDiagnostic, logText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeviceState.Disabled)]
    [InlineData(DeviceState.Active)]
    public void Reconnect_RechecksCompanionStateBeforeRequest(DeviceState state)
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        var companion = new Profile("voice", "Headset (Fixture)", "bthhfenum-voice");
        using var harness = new ProfileHarness(primary, companion);
        primary.OnReconnect = () => harness.SetState(companion.Id, state);

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId], harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Fact]
    public void Reconnect_SkipsDisabledAndActiveProfilesAndDeduplicatesSharedTopology()
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        using var harness = new ProfileHarness(primary,
            new Profile("disabled", "Disabled profile", "bthhfenum-disabled") { State = DeviceState.Disabled },
            new Profile("active", "Active profile", "bthhfenum-active") { State = DeviceState.Active },
            new Profile("duplicate", "Duplicate music", primary.TopologyId),
            new Profile("voice-render", "Voice output", "bthhfenum-voice"),
            new Profile("voice-capture", "Voice input", "BTHHFENUM-VOICE"));

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId, "bthhfenum-voice"], harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconnect_FailedSharedTopologyStillTriesOtherEndpointRoutes(bool sameContainer)
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-shared")
        {
            Hresult = unchecked((int)0x8007001F),
        };
        var other = new Profile("voice", primary.Name, primary.TopologyId)
        {
            Container = sameContainer ? FixtureContainer : Guid.NewGuid(),
        };
        using var harness = new ProfileHarness(primary, other);

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId, primary.Id, other.Id], harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconnect_PartialFailureDoesNotEraseAcceptance(bool primaryFails)
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        var voice = new Profile("voice", "Headset (Fixture)", "bthhfenum-voice");
        (primaryFails ? primary : voice).Hresult = unchecked((int)0x8007001F);
        using var harness = new ProfileHarness(primary, voice);

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(primary.TopologyId, harness.Factory.Requests);
        Assert.Contains(voice.TopologyId, harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Fact]
    public void Reconnect_StaleCompanionsDoNotPreventOtherProfiles()
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        using var harness = new ProfileHarness(new Profile("stale", "Stale endpoint", "bth-stale") { Stale = true }, primary,
            new Profile("missing", "Disappeared profile", "bth-missing") { ThrowOnProperty = true },
            new Profile("voice", "Headset (Fixture)", "bthhfenum-voice"));

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId, "bthhfenum-voice"], harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Reconnect_CancellationStopsFurtherRequests(bool cancelAfterPrimary, bool hasCompanion)
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        if (cancelAfterPrimary) primary.OnReconnect = cancellation.Cancel;
        else cancellation.Cancel();
        Profile[] profiles = hasCompanion ? [primary, new Profile("voice", "Headset (Fixture)", "bthhfenum-voice")] : [primary];
        using var harness = new ProfileHarness(profiles);

        Assert.Throws<OperationCanceledException>(() => harness.Reconnect(primary.Name, cancellationToken: cancellation.Token));
        Assert.Equal(cancelAfterPrimary ? [primary.TopologyId] : Array.Empty<string>(), harness.Factory.Requests);
        harness.AssertDisposed();
    }

    [Fact]
    public void Reconnect_MissingCachedEndpointUsesCurrentInventory()
    {
        var primary = new Profile("music", "Headphones (Fixture)", "bthenum-music");
        using var harness = new ProfileHarness(primary, new Profile("voice", "Headset (Fixture)", "bthhfenum-voice"));
        harness.Remember(primary.Name, "missing-id");

        Assert.True(harness.Reconnect(primary.Name, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([primary.TopologyId, "bthhfenum-voice"], harness.Factory.Requests);
    }

    private sealed class Profile(string id, string name, string topologyId)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string TopologyId { get; } = topologyId;
        public Guid? Container { get; set; } = FixtureContainer;
        public ushort VariantType { get; set; } = 72;
        public bool NullPointer { get; set; }
        public int PropertyHresult { get; set; }
        public int PropertyStoreHresult { get; set; }
        public bool ThrowOnProperty { get; set; }
        public bool TopologyUnavailable { get; set; }
        public bool Stale { get; init; }
        public DeviceState State { get; init; } = DeviceState.Unplugged;
        public int Hresult { get; set; }
        public Action? OnReconnect { get; set; }
    }

    private sealed class ProfileHarness : IDisposable
    {
        private readonly MMDevice[] _endpoints;
        private readonly BluetoothAudioEndpointReconnectFallback _fallback;
        public ProfileFactory Factory { get; }
        public RememberedBluetoothEndpointCache Cache { get; }
        public ProfileHarness(params Profile[] profiles) : this(Logger.Instance, profiles) { }
        public ProfileHarness(Logger logger, params Profile[] profiles)
        {
            _endpoints = [.. profiles.Select(profile =>
            {
                MMDevice device = CreateEndpoint(profile.Id, profile.Name, profile.State);
                TestPrivateAccess.GetField<EndpointIdentityProxy>(device, "deviceInterface").Stale = profile.Stale;
                return device;
            })];
            Factory = new ProfileFactory(profiles);
            _fallback = new BluetoothAudioEndpointReconnectFallback(logger, Factory);
            Cache = TestPrivateAccess.GetField<RememberedBluetoothEndpointCache>(_fallback, "_rememberedAudioEndpoints");
        }
        public void Remember(string name, string id) => Cache.RememberEndpointId(BluetoothReconnectService.NormalizeForMatch(name), id);
        public void SetState(string id, DeviceState state) =>
            TestPrivateAccess.GetField<EndpointIdentityProxy>(_endpoints.Single(endpoint => endpoint.ID == id), "deviceInterface").State = state;
        public bool Reconnect(string name, string kind = "output", CancellationToken cancellationToken = default) =>
            _fallback.TryReconnectEndpoints(_endpoints, name, "fixture", kind, cancellationToken);
        public void AssertDisposed() => Assert.All(Factory.Handles, handle => Assert.True(handle.Disposed));
        public void Dispose()
        {
            foreach (MMDevice endpoint in _endpoints) endpoint.Dispose();
        }
    }

    private class TrackedProfileHandle : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class ProfileFactory(Profile[] profiles) : INativeAudioEndpointFactory
    {
        public bool IsAvailable => true;
        public List<string> Requests { get; } = [];
        public List<TrackedProfileHandle> Handles { get; } = [];
        public bool TryCreate(string endpointId, [NotNullWhen(true)] out INativeAudioEndpoint? endpoint)
        {
            Profile profile = profiles.First(item => item.Id == endpointId || item.TopologyId.Equals(endpointId, StringComparison.OrdinalIgnoreCase));
            endpoint = Track(new ProfileEndpoint(this, profile, endpointId));
            return true;
        }
        public T Track<T>(T handle) where T : TrackedProfileHandle
        {
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class ProfileEndpoint(ProfileFactory factory, Profile profile, string id) : TrackedProfileHandle, INativeAudioEndpoint
    {
        public bool TryOpenPropertyStore(uint stgmAccess, [NotNullWhen(true)] out INativePropertyStore? propertyStore, out int hresult)
        {
            Assert.Equal(0u, stgmAccess);
            hresult = profile.PropertyStoreHresult;
            if (hresult < 0)
            {
                propertyStore = null;
                return false;
            }
            propertyStore = factory.Track(new ContainerStore(profile));
            return true;
        }
        public bool TryActivate<TInterface>(Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
            where TInterface : class
        {
            activatedObject = interfaceId == typeof(BluetoothAudioEndpointReconnectFallback.IKsControl).GUID
                ? factory.Track(new ProfileKsHandle(factory, profile, id)) as IActivatedNativeComObject<TInterface>
                : profile.TopologyUnavailable ? null : factory.Track(new ProfileTopology(profile.TopologyId)) as IActivatedNativeComObject<TInterface>;
            hresult = activatedObject == null ? unchecked((int)0x80004002) : 0;
            return activatedObject != null;
        }
    }

    private sealed class ContainerStore(Profile profile) : TrackedProfileHandle, INativePropertyStore
    {
        public int GetValue(ref NativePropertyKey key, out NativePropVariant value)
        {
            Assert.Equal(new NativePropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2), key);
            if (profile.ThrowOnProperty) throw new COMException("Fixture property store disappeared");
            value = default;
            if (profile.Container.HasValue)
            {
                value.vt = profile.VariantType;
                if (value.vt == 72 && !profile.NullPointer)
                {
                    value.pointerValue = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
                    Marshal.StructureToPtr(profile.Container.Value, value.pointerValue, false);
                }
            }
            return profile.PropertyHresult;
        }
        public int SetValue(ref NativePropertyKey key, ref NativePropVariant value) => throw new NotSupportedException();
        public int Commit() => throw new NotSupportedException();
    }

    [GeneratedComClass]
    private sealed partial class ProfileKsHandle(ProfileFactory factory, Profile profile, string id)
        : TrackedProfileHandle, IActivatedNativeComObject<BluetoothAudioEndpointReconnectFallback.IKsControl>, BluetoothAudioEndpointReconnectFallback.IKsControl
    {
        public BluetoothAudioEndpointReconnectFallback.IKsControl Interface => this;
        public int KsProperty(IntPtr property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned)
        {
            factory.Requests.Add(id);
            bytesReturned = 0;
            profile.OnReconnect?.Invoke();
            return profile.Hresult;
        }
    }

    [GeneratedComClass]
    private sealed partial class ProfileTopology(string topologyId) : TrackedProfileHandle, IActivatedNativeComObject<IDeviceTopologyNativeInterop>, IDeviceTopologyNativeInterop
    {
        public IDeviceTopologyNativeInterop Interface => this;
        public int GetConnectorCount(out uint connectorCount) { connectorCount = 1; return 0; }
        public unsafe int GetConnector(uint index, out IntPtr connector)
        {
            connector = (IntPtr)ComInterfaceMarshaller<IConnectorNativeInterop>.ConvertToUnmanaged(new ProfileConnector(topologyId));
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class ProfileConnector(string topologyId) : IConnectorNativeInterop
    {
        public int GetDeviceIdConnectedTo(out string deviceId) { deviceId = topologyId; return 0; }
        public int GetType(out int connectorType) => throw new NotSupportedException();
        public int GetDataFlow(out int dataFlow) => throw new NotSupportedException();
        public int ConnectTo(IConnectorNativeInterop connectedTo) => throw new NotSupportedException();
        public int Disconnect() => throw new NotSupportedException();
        public int IsConnected(out bool isConnected) => throw new NotSupportedException();
        public int GetConnectedTo(out IntPtr connectedTo) => throw new NotSupportedException();
        public int GetConnectorIdConnectedTo(out string connectorId) => throw new NotSupportedException();
    }
}
