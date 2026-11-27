using System.Reflection;
using System.Runtime.InteropServices;
using AudioPilot.CliHost;
using AudioPilot.Models;
using Windows.ApplicationModel;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Management.Deployment;
using Windows.Media.Control;
using Windows.Networking.Connectivity;

namespace AudioPilot.Tests.Platform;

public sealed class ScopedWinRtProjectionTests
{
    private static readonly TimeSpan BrokerProbeTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ApplicationAssemblies_UseScopedAudioPilotWinRtWithoutMonolithicWindowsSdk()
    {
        Assembly[] assemblies =
        [
            typeof(App).Assembly,
            typeof(Settings).Assembly,
            typeof(CliHostRuntime).Assembly,
            typeof(GlobalSystemMediaTransportControlsSessionManager).Assembly,
        ];

        Assert.Equal("AudioPilot.WinRT", typeof(GlobalSystemMediaTransportControlsSessionManager).Assembly.GetName().Name);
        Assert.Equal("AudioPilot.WinRT", typeof(BluetoothDevice).Assembly.GetName().Name);
        Assert.Equal("AudioPilot.WinRT", typeof(NetworkInformation).Assembly.GetName().Name);
        Assert.Equal("AudioPilot.WinRT", typeof(PackageManager).Assembly.GetName().Name);

        foreach (Assembly assembly in assemblies)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                static reference => string.Equals(reference.Name, "Microsoft.Windows.SDK.NET", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ScopedWinRtSubsystems_ActivateCleanlyAtRuntime()
    {
        // 1. Synchronous WinRT projection calls must always succeed without projection-binding failures.
        IReadOnlyList<ConnectionProfile> profiles = NetworkInformation.GetConnectionProfiles();
        Assert.NotNull(profiles);

        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        Assert.False(string.IsNullOrWhiteSpace(selector));

        var packageManager = new PackageManager();
        try
        {
            IEnumerable<Package> packages =
                packageManager.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main);
            Assert.NotNull(packages);
            _ = packages.Take(1).ToList();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // Headless Windows Server / non-interactive CI sessions may disallow PackageManager queries;
            // projection binding and activation already succeeded.
        }

        // 2. Async brokered WinRT calls are bounded with a short timeout so headless CI sessions cannot hang,
        // while any TypeLoadException / MissingMethodException / BadImageFormatException / InvalidCastException still fails.
        await ProbeBrokerAsync(async token =>
        {
            GlobalSystemMediaTransportControlsSessionManager? mediaManager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
            if (mediaManager is not null)
            {
                _ = mediaManager.GetSessions();
            }
        });

        await ProbeBrokerAsync(async token =>
        {
            DeviceInformationCollection pairedDevices =
                await DeviceInformation.FindAllAsync(selector).AsTask(token);
            Assert.NotNull(pairedDevices);
        });
    }

    private static async Task ProbeBrokerAsync(Func<CancellationToken, Task> probe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(BrokerProbeTimeout);
        try
        {
            await probe(timeout.Token).WaitAsync(BrokerProbeTimeout, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or TimeoutException)
        {
            // Headless OS brokers may be unavailable in non-interactive sessions.
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            // Bounded timeout expired waiting for a non-interactive OS broker.
        }
    }
}
