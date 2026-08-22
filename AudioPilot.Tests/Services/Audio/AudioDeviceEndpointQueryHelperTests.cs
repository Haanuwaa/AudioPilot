using System.Reflection;
using System.Runtime.InteropServices;
using AudioPilot.Logging;
using AudioPilot.Models;
using NAudio.CoreAudioApi;
using NRole = NAudio.CoreAudioApi.Role;

namespace AudioPilot.Tests.Services.Audio;

public sealed class AudioDeviceEndpointQueryHelperTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Inventory_DistinguishesEmptyDiscoveryFromAnUnreadableNativeEntry(int unreadableEntries)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var enumeratorLock = new ReaderWriterLockSlim();
        var helper = new AudioDeviceEndpointQueryHelper(enumerator, enumeratorLock, Logger.Instance,
            () => false, () => [NRole.Multimedia], () => [NRole.Console], (_, _) => { });
        Type contract = typeof(MMDeviceCollection).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IMMDeviceCollection", throwOnError: true)!;
        var proxy = (UnreadableDeviceCollection)DispatchProxy.Create(contract, typeof(UnreadableDeviceCollection));
        proxy.Count = unreadableEntries;
        var collection = (MMDeviceCollection)Activator.CreateInstance(typeof(MMDeviceCollection),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [proxy], null)!;

        AudioDeviceInventory inventory = helper.GetActiveCycleInventory(() => collection, "Fixture inventory");

        Assert.Empty(inventory);
        Assert.Equal(unreadableEntries == 0, inventory.IsComplete);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void GetActiveCycleDevices_ReportsSkippedNativeEntriesInsteadOfSilentlyReturningEmpty(int unreadableEntries)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var enumeratorLock = new ReaderWriterLockSlim();
        var helper = new AudioDeviceEndpointQueryHelper(enumerator, enumeratorLock, Logger.Instance,
            () => false, () => [NRole.Multimedia], () => [NRole.Console], (_, _) => { });
        Type contract = typeof(MMDeviceCollection).Assembly.GetType("NAudio.CoreAudioApi.Interfaces.IMMDeviceCollection", throwOnError: true)!;
        var proxy = (UnreadableDeviceCollection)DispatchProxy.Create(contract, typeof(UnreadableDeviceCollection));
        proxy.Count = unreadableEntries;
        var collection = (MMDeviceCollection)Activator.CreateInstance(typeof(MMDeviceCollection),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [proxy], null)!;
        var failures = new List<int>();

        List<CycleDevice> devices = helper.GetActiveCycleDevices(() => collection, "Fixture inventory",
            (index, exception) =>
            {
                Assert.Same(proxy.Failure, exception);
                failures.Add(index);
            });

        Assert.Empty(devices);
        Assert.Equal(Enumerable.Range(0, unreadableEntries), failures);
    }

    public class UnreadableDeviceCollection : DispatchProxy
    {
        public int Count { get; set; }
        public COMException Failure { get; } = new("Fixture unreadable endpoint", unchecked((int)0x80004005));

        protected override object Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == "GetCount")
            {
                arguments![0] = Count;
                return 0;
            }
            if (method?.Name == "Item") throw Failure;
            throw new InvalidOperationException(method?.Name);
        }
    }

    [Theory]
    [InlineData(true, unchecked((int)0x80070490), AudioEndpointStatusKind.NoDevice)]
    [InlineData(false, unchecked((int)0x80070490), AudioEndpointStatusKind.NoDevice)]
    [InlineData(true, unchecked((int)0x88890004), AudioEndpointStatusKind.Unavailable)]
    [InlineData(false, unchecked((int)0x88890004), AudioEndpointStatusKind.Unavailable)]
    public void CaptureAudioStatus_EndpointFailurePreservesOtherEndpoint(bool failOutput, int hresult, AudioEndpointStatusKind expectedKind)
    {
        var available = new AudioEndpointStatus(AudioEndpointStatusKind.Available, "Fixture endpoint", 42, true);
        var failures = new List<string>();
        AudioEndpointStatus ReadFailure() => throw new COMException("Fixture failure", hresult);

        AudioStatusSnapshot snapshot = AudioDeviceEndpointQueryHelper.CaptureAudioStatus(
            failOutput ? ReadFailure : () => available,
            failOutput ? () => available : ReadFailure,
            (target, _) => failures.Add(target), TestContext.Current.CancellationToken);

        AudioEndpointStatus failed = failOutput ? snapshot.Output : snapshot.Input;
        Assert.Same(available, failOutput ? snapshot.Input : snapshot.Output);
        Assert.Equal(expectedKind, failed.Kind);
        Assert.Null(failed.VolumePercent);
        Assert.Null(failed.Muted);
        if (expectedKind == AudioEndpointStatusKind.Unavailable)
        {
            Assert.Equal(failOutput ? "output" : "input", Assert.Single(failures));
        }
        else
        {
            Assert.Empty(failures);
        }
    }

    [Fact]
    public void TryGetDeviceById_ReturnsNull_ForBlankId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var lockSlim = new ReaderWriterLockSlim();
        var helper = new AudioDeviceEndpointQueryHelper(
            enumerator,
            lockSlim,
            Logger.Instance,
            () => false,
            () => [NRole.Multimedia],
            () => [NRole.Console],
            (_, _) => { });

        MMDevice? device = helper.TryGetDeviceById(string.Empty);

        Assert.Null(device);
    }

    [Fact]
    public void GetAllDefaultPlaybackDevices_ReturnsEmpty_WhenDisposed()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var lockSlim = new ReaderWriterLockSlim();
        var helper = new AudioDeviceEndpointQueryHelper(
            enumerator,
            lockSlim,
            Logger.Instance,
            () => true,
            () => [NRole.Multimedia],
            () => [NRole.Console],
            (_, _) => { });

        List<MMDevice?> devices = helper.GetAllDefaultPlaybackDevices();

        Assert.Empty(devices);
    }
}
