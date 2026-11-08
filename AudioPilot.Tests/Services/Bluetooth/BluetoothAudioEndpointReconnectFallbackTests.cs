using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AudioPilot.Logging;
using AudioPilot.Tests.Helpers;
using NAudio.CoreAudioApi;

namespace AudioPilot.Tests.Services.Bluetooth;

public sealed partial class BluetoothAudioEndpointReconnectFallbackTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(unchecked((int)0x8007001F), false, false)]
    [InlineData(unchecked((int)0x8007001F), false, true)]
    public void Reconnect_PreservesEndpointAcrossTopologyAndPropertyRequests(int reconnectHresult, bool expectedSuccess, bool throwFromReconnect)
    {
        using MMDevice endpoint = CreateEndpoint("fixture-endpoint", "Fixture");
        var factory = new ReconnectEndpointFactory(reconnectHresult, throwFromReconnect);
        var fallback = new BluetoothAudioEndpointReconnectFallback(Logger.Instance, factory);

        bool success = fallback.TryReconnectEndpoints([endpoint], "Fixture", "fixture", "output", TestContext.Current.CancellationToken);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal("fixture-endpoint", endpoint.ID);
        Assert.Equal(["fixture-endpoint", "fixture-endpoint"], factory.RequestedIds);
        Assert.All(factory.Endpoints, nativeEndpoint => Assert.True(nativeEndpoint.Disposed));
        Assert.True(factory.KsHandle.Disposed);
    }

    private class EndpointIdentityProxy : DispatchProxy
    {
        public string Id { get; set; } = "fixture-endpoint";
        public DeviceState State { get; set; } = DeviceState.Unplugged;
        public bool Stale { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Stale) throw new COMException("Fixture endpoint disappeared");
            switch (targetMethod?.Name)
            {
                case "GetId": args![0] = Id; return 0;
                case "GetState": args![0] = State; return 0;
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private sealed class ReconnectEndpointFactory(int reconnectHresult, bool throwFromReconnect) : INativeAudioEndpointFactory
    {
        public bool IsAvailable => true;
        public List<string> RequestedIds { get; } = [];
        public List<ReconnectNativeEndpoint> Endpoints { get; } = [];
        public ReconnectKsHandle KsHandle { get; } = new(reconnectHresult, throwFromReconnect);

        public bool TryCreate(string endpointId, [NotNullWhen(true)] out INativeAudioEndpoint? endpoint)
        {
            RequestedIds.Add(endpointId);
            var nativeEndpoint = new ReconnectNativeEndpoint(KsHandle);
            Endpoints.Add(nativeEndpoint);
            endpoint = nativeEndpoint;
            return true;
        }
    }

    private sealed class ReconnectNativeEndpoint(ReconnectKsHandle ksHandle) : INativeAudioEndpoint
    {
        public bool Disposed { get; private set; }

        public bool TryOpenPropertyStore(uint stgmAccess, [NotNullWhen(true)] out INativePropertyStore? propertyStore, out int hresult)
        {
            throw new NotSupportedException();
        }

        public bool TryActivate<TInterface>(Guid interfaceId, uint clsCtx, [NotNullWhen(true)] out IActivatedNativeComObject<TInterface>? activatedObject, out int hresult)
            where TInterface : class
        {
            activatedObject = ksHandle as IActivatedNativeComObject<TInterface>;
            hresult = activatedObject == null ? unchecked((int)0x80004002) : 0;
            return activatedObject != null;
        }

        public void Dispose() => Disposed = true;
    }

    [GeneratedComClass]
    private sealed partial class ReconnectKsHandle(int reconnectHresult, bool throwFromReconnect) : IActivatedNativeComObject<BluetoothAudioEndpointReconnectFallback.IKsControl>, BluetoothAudioEndpointReconnectFallback.IKsControl
    {
        public BluetoothAudioEndpointReconnectFallback.IKsControl Interface => this;
        public bool Disposed { get; private set; }
        public int RequestCount { get; private set; }

        public int KsProperty(IntPtr property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned)
        {
            RequestCount++;
            bytesReturned = 0;
            if (throwFromReconnect) throw new COMException("Fixture reconnect failed", reconnectHresult);
            return reconnectHresult;
        }

        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData("Headphones (Creative Pebble Pro)", DeviceState.Unplugged, 0, true, 1)]
    [InlineData("Creative Pebble Pro Hands-Free AG Audio", DeviceState.Unplugged, 0, true, 1)]
    [InlineData("Other Bluetooth Headset", DeviceState.Unplugged, 0, false, 0)]
    [InlineData("Creative Pebble Pro", DeviceState.Unplugged, unchecked((int)0x8007001F), false, 1)]
    [InlineData("Creative Pebble Pro", DeviceState.Disabled, 0, false, 0)]
    [InlineData("Creative Pebble Pro", DeviceState.Active, 0, false, 0)]
    public void CachedReconnect_RevalidatesNameStateAndRequest(
        string candidateName, DeviceState state, int hresult, bool expectedAccepted, int expectedRequests)
    {
        const string expectedName = "Headphones (Creative Pebble Pro)";
        string normalizedName = BluetoothReconnectService.NormalizeForMatch(expectedName);
        var factory = new ReconnectEndpointFactory(hresult, throwFromReconnect: false);
        var fallback = new BluetoothAudioEndpointReconnectFallback(Logger.Instance, factory);
        var cache = TestPrivateAccess.GetField<RememberedBluetoothEndpointCache>(fallback, "_rememberedAudioEndpoints");
        cache.RememberEndpointId(normalizedName, "fixture-endpoint");
        using MMDevice endpoint = CreateEndpoint("fixture-endpoint", candidateName, state);

        bool accepted = fallback.TryReconnectEndpoints([endpoint], expectedName, "fixture", "output", CancellationToken.None);

        Assert.Equal(expectedAccepted, accepted);
        Assert.Equal(expectedRequests, factory.KsHandle.RequestCount);
        Assert.Equal(expectedAccepted, cache.TryGetEndpointId(normalizedName, out _));
        Assert.All(factory.Endpoints, item => Assert.True(item.Disposed));
    }

    private static MMDevice CreateEndpoint(string id, string name, DeviceState state = DeviceState.Unplugged)
    {
        Type endpointType = typeof(MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IMMDevice", throwOnError: true)!;
        var proxy = (EndpointIdentityProxy)DispatchProxy.Create(endpointType, typeof(EndpointIdentityProxy));
        proxy.Id = id;
        proxy.State = state;
        var endpoint = (MMDevice)RuntimeHelpers.GetUninitializedObject(typeof(MMDevice));
        TestPrivateAccess.SetField(endpoint, "deviceInterface", proxy);
        Type storeType = typeof(MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IPropertyStore", throwOnError: true)!;
        var store = (EndpointNameProxy)DispatchProxy.Create(storeType, typeof(EndpointNameProxy));
        store.Name = name;
        TestPrivateAccess.SetField(endpoint, "propertyStore", (PropertyStore)Activator.CreateInstance(
            typeof(PropertyStore), BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [store], culture: null)!);
        return endpoint;
    }

    private class EndpointNameProxy : DispatchProxy
    {
        public string Name { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetValue") throw new NotSupportedException(targetMethod?.Name);
            Type variantType = typeof(MMDevice).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.PropVariant", throwOnError: true)!;
            object variant = Activator.CreateInstance(variantType)!;
            variantType.GetField("vt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(variant, (short)31);
            variantType.GetField("pointerValue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .SetValue(variant, Marshal.StringToCoTaskMemUni(Name));
            Marshal.StructureToPtr(variant, (IntPtr)args![1]!, fDeleteOld: false);
            return 0;
        }
    }
}
