using System.Collections.ObjectModel;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Coordinators;

public class NetworkTriggerCoordinatorTests
{
    [Fact]
    public void OnConnectivityChanged_OlderSnapshotFinishesLast_DoesNotReplaceNewerNetworkState()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routine = new AudioRoutine
        {
            Id = "home",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Network,
            NetworkTriggerDirection = NetworkTriggerDirection.Both,
            TriggerNetworkName = "HomeWiFi",
            OutputDeviceId = "out-1",
        };
        var routines = new ObservableCollection<AudioRoutine>([routine]);
        var executions = new List<string>();
        bool resolveNewerSnapshot = true;
        using var coordinator = new NetworkTriggerCoordinator(routines, (_, source) => executions.Add(source), Logger.Instance, monitor,
            routineSnapshotProvider: () =>
            {
                if (resolveNewerSnapshot)
                {
                    resolveNewerSnapshot = false;
                    monitor.SimulateConnectivityChange("HomeWiFi");
                }
                return [.. routines];
            });
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeWiFi");
        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Empty(executions);
    }

    [Fact]
    public void OnConnectivityChanged_NetworkChangesDuringDispatch_DoesNotRunRemainingStaleRoutines()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>(Enumerable.Range(0, 2).Select(index => new AudioRoutine
        {
            Id = $"routine-{index}",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Network,
            NetworkTriggerDirection = NetworkTriggerDirection.Connect,
            TriggerNetworkName = "HomeWiFi",
            OutputDeviceId = "out-1",
        }));
        var executed = new List<string>();
        using var coordinator = new NetworkTriggerCoordinator(routines, (routine, _) =>
        {
            executed.Add(routine.Id);
            monitor.SimulateConnectivityChange("OfficeWiFi");
        }, Logger.Instance, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Equal(["routine-0"], executed);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  TestNetwork  ", "TestNetwork")]
    [InlineData("TestNetwork", "TestNetwork")]
    public void NetworkHelper_NormalizeNetworkName_TrimsOptionalNames(string? value, string expected)
    {
        Assert.Equal(expected, NetworkHelper.NormalizeNetworkName(value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnConnectivityChanged_StoppedDuringDispatch_DoesNotExecuteRemainingRoutines(bool restart)
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>(Enumerable.Range(0, 2).Select(index => new AudioRoutine
        {
            Id = $"routine-{index}",
            Enabled = true,
            TriggerKind = RoutineTriggerKind.Network,
            NetworkTriggerDirection = NetworkTriggerDirection.Connect,
            TriggerNetworkName = "HomeWiFi",
            OutputDeviceId = "out-1",
        }));
        var executed = new List<string>();
        NetworkTriggerCoordinator? coordinator = null;
        using (coordinator = new NetworkTriggerCoordinator(routines, (routine, _) =>
        {
            executed.Add(routine.Id);
            coordinator!.Stop();
            if (restart) { coordinator.Start(); }
        }, Logger.Instance, monitor))
        {
            coordinator.Start();

            monitor.SimulateConnectivityChange("HomeWiFi");

            Assert.Equal(["routine-0"], executed);
        }
    }

    [Fact]
    public void Start_StartsMonitorAndRecordsInitialNetwork()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        Assert.True(monitor.Started);
        Assert.Equal(["HomeWiFi"], monitor.LastObservedNetworkNames);
    }

    [Fact]
    public void Start_Idempotent_DoesNotStartMonitorTwice()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Start();

        Assert.Equal(1, monitor.StartCount);
    }

    [Fact]
    public void Stop_StopsMonitor()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Stop();

        Assert.False(monitor.Started);
    }

    [Fact]
    public void Stop_Idempotent_DoesNotStopMonitorTwice()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Stop();
        coordinator.Stop();

        Assert.Equal(1, monitor.StopCount);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesMatchingRoutine_WhenNetworkChanges()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "OfficeRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeEthernet",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeEthernet");

        Assert.Equal("OfficeRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteRoutine_WhenNetworkNameMatches()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteDisabledRoutine()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                Enabled = false,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeEthernet",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeEthernet");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteNonNetworkTrigger()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HotkeyRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Hotkey,
                TriggerNetworkName = "OfficeEthernet",
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeEthernet");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_NormalizesNetworkNameComparison()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "  HOME-WIFI  ",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "out-1"
            }
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("home-wifi");

        Assert.Equal("HomeRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void Dispose_StopsMonitorAndDisposesIt()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>();
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();
        coordinator.Dispose();

        Assert.True(monitor.Disposed);
        Assert.False(monitor.Started);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesDisconnectRoutine_WhenNetworkDisconnects()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "DisconnectRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = string.Empty,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(string.Empty);

        Assert.Equal("DisconnectRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteDisconnectRoutine_WhenNetworkConnects()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "DisconnectRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = string.Empty,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesBothDirectionRoutine_WhenNetworkConnects()
    {
        var monitor = new FakeNetworkMonitor(string.Empty);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "BothRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("HomeWiFi");

        Assert.Equal("BothRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesBothDirectionRoutine_WhenNetworkDisconnects()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "BothRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(string.Empty);

        Assert.Equal("BothRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_DoesNotExecuteBothDirectionRoutine_WhenAnotherNetworkDisconnects()
    {
        var monitor = new FakeNetworkMonitor(["HomeWiFi", "GuestWiFi"]);
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(["HomeWiFi"]);

        Assert.Empty(executedRoutines);
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesBothDirectionRoutine_WhenTargetNetworkIsRemovedDuringSwitch()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-1",
                Name = "HomeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeWiFi");

        Assert.Equal("HomeRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesConnectRoutine_WhenTargetNetworkAppearsAlongsideExistingNetwork()
    {
        var monitor = new FakeNetworkMonitor("Ethernet");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-office",
                Name = "OfficeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(["Ethernet", "OfficeWiFi"]);

        Assert.Equal("OfficeRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_ExecutesConnectRoutine_WhenSwitchingNetworks()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-office",
                Name = "OfficeRoutine",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "device-1",
                OutputDeviceName = "Device 1",
            },
        ]);
        var executedRoutines = new List<string>();
        var logger = Logger.Instance;

        var coordinator = new NetworkTriggerCoordinator(routines, (r, reason) => executedRoutines.Add(r.Name), logger, monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange("OfficeWiFi");

        Assert.Equal("OfficeRoutine", Assert.Single(executedRoutines));
    }

    [Fact]
    public void OnConnectivityChanged_RapidDistinctTransitions_ExecuteEachMatchingRoutine()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>([
            new AudioRoutine
            {
                Id = "routine-home-disconnect",
                Name = "Home disconnected",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "HomeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Both,
                OutputDeviceId = "device-1",
            },
            new AudioRoutine
            {
                Id = "routine-office-connect",
                Name = "Office connected",
                TriggerKind = RoutineTriggerKind.Network,
                TriggerNetworkName = "OfficeWiFi",
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                OutputDeviceId = "device-2",
            },
        ]);
        var executions = new List<(string RoutineId, string Source)>();

        using var coordinator = new NetworkTriggerCoordinator(
            routines,
            (routine, source) => executions.Add((routine.Id, source)),
            Logger.Instance,
            monitor);
        coordinator.Start();

        monitor.SimulateConnectivityChange(string.Empty);
        monitor.SimulateConnectivityChange("OfficeWiFi");

        Assert.Equal(
            [
                ("routine-home-disconnect", "network-disconnect-trigger"),
                ("routine-office-connect", "network-connect-trigger"),
            ],
            executions);
    }

    [Fact]
    public void OnConnectivityChanged_SnapshotFailure_DoesNotImpersonateDisconnect()
    {
        var monitor = new FakeNetworkMonitor("HomeWiFi");
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-disconnect",
                Name = "Offline",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                NetworkTriggerDirection = NetworkTriggerDirection.Disconnect,
                OutputDeviceId = "out-1",
            }
        };
        var executions = new List<AudioRoutine>();
        using var coordinator = new NetworkTriggerCoordinator(
            routines,
            (routine, _) => executions.Add(routine),
            new Logger(),
            monitor);
        coordinator.Start();

        monitor.SnapshotAvailable = false;
        monitor.SimulateConnectivityChange(string.Empty);
        Assert.Empty(executions);

        monitor.SnapshotAvailable = true;
        monitor.SimulateConnectivityChange(string.Empty);
        Assert.Single(executions);
    }

    [Fact]
    public void OnConnectivityChanged_FirstSuccessfulSnapshot_EstablishesBaselineWithoutTriggering()
    {
        var monitor = new FakeNetworkMonitor(string.Empty)
        {
            SnapshotAvailable = false,
        };
        var routines = new ObservableCollection<AudioRoutine>
        {
            new()
            {
                Id = "routine-connect",
                Name = "Office",
                Enabled = true,
                TriggerKind = RoutineTriggerKind.Network,
                NetworkTriggerDirection = NetworkTriggerDirection.Connect,
                TriggerNetworkName = "OfficeWiFi",
                OutputDeviceId = "out-1",
            }
        };
        var executions = new List<AudioRoutine>();
        using var coordinator = new NetworkTriggerCoordinator(
            routines,
            (routine, _) => executions.Add(routine),
            new Logger(),
            monitor);
        coordinator.Start();

        monitor.SnapshotAvailable = true;
        monitor.SimulateConnectivityChange("OfficeWiFi");

        Assert.Empty(executions);
    }

    private sealed class FakeNetworkMonitor(IEnumerable<string> initialNetworks) : INetworkConnectionMonitor
    {
        public FakeNetworkMonitor(string initialNetwork)
            : this(string.IsNullOrWhiteSpace(initialNetwork) ? [] : [initialNetwork])
        {
        }

        public event EventHandler? ConnectivityChanged;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool SnapshotAvailable { get; set; } = true;
        public IReadOnlyList<string> LastObservedNetworkNames { get; private set; } = [.. initialNetworks];

        public void Start()
        {
            Started = true;
            StartCount++;
        }

        public void Stop()
        {
            Started = false;
            StopCount++;
        }

        public bool TryGetConnectedNetworkNames(out IReadOnlyCollection<string> networkNames)
        {
            networkNames = [.. LastObservedNetworkNames];
            return SnapshotAvailable;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public void SimulateConnectivityChange(string newNetworkName)
        {
            LastObservedNetworkNames = string.IsNullOrWhiteSpace(newNetworkName)
                ? []
                : [newNetworkName];
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SimulateConnectivityChange(IEnumerable<string> newNetworkNames)
        {
            LastObservedNetworkNames = [.. newNetworkNames];
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
