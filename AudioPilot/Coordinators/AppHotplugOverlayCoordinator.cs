using AudioPilot.Models;
using AudioPilot.ViewModels;

namespace AudioPilot.Coordinators
{
    internal sealed class AppHotplugOverlayCoordinator(
        SettingsService settingsService,
        AppViewModel appVm,
        OverlayService overlayService,
        Func<(List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices)>? activeDeviceSnapshotProvider = null)
    {
        private sealed class HotplugConfiguredConnectivitySnapshot
        {
            public Dictionary<string, string> OutputConfiguredById { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> InputConfiguredById { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ConnectedConfiguredOutputIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ConnectedConfiguredInputIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly SettingsService _settingsService = settingsService;
        private readonly AppViewModel _appVm = appVm;
        private readonly OverlayService _overlayService = overlayService;
        private readonly Func<(List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices)> _activeDeviceSnapshotProvider = activeDeviceSnapshotProvider
            ?? (() => appVm.GetKnownActiveDeviceInfoSnapshot());
        private HotplugConfiguredConnectivitySnapshot? _lastSnapshot;

        internal AppHotplugOverlayCoordinator(
            SettingsService settingsService,
            AppViewModel appVm,
            Lazy<OverlayService> overlayService,
            Func<(List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices)>? activeDeviceSnapshotProvider = null)
            : this(settingsService, appVm, overlayService.Value, activeDeviceSnapshotProvider)
        {
        }

        public void CaptureInitialSnapshot()
        {
            _lastSnapshot = CaptureConfiguredHotplugSnapshot();
        }

        public void ProcessPostRefresh()
        {
            HotplugConfiguredConnectivitySnapshot afterSnapshot = CaptureConfiguredHotplugSnapshot();

            if (_lastSnapshot != null)
            {
                if (DidConfiguredSelectionChange(_lastSnapshot, afterSnapshot))
                {
                    _lastSnapshot = afterSnapshot;
                    return;
                }

                ShowConfiguredHotplugOverlayIfNeeded(_lastSnapshot, afterSnapshot);
            }

            _lastSnapshot = afterSnapshot;
        }

        private static bool DidConfiguredSelectionChange(
            HotplugConfiguredConnectivitySnapshot before,
            HotplugConfiguredConnectivitySnapshot after)
        {
            return !HaveSameConfiguredIds(before.OutputConfiguredById, after.OutputConfiguredById)
                || !HaveSameConfiguredIds(before.InputConfiguredById, after.InputConfiguredById);
        }

        private static bool HaveSameConfiguredIds(
            Dictionary<string, string> before,
            Dictionary<string, string> after)
        {
            if (before.Count != after.Count)
            {
                return false;
            }

            foreach (string id in before.Keys)
            {
                if (!after.ContainsKey(id))
                {
                    return false;
                }
            }

            return true;
        }

        private HotplugConfiguredConnectivitySnapshot CaptureConfiguredHotplugSnapshot()
        {
            var snapshot = new HotplugConfiguredConnectivitySnapshot();
            Settings? savedSettings = _appVm.CurrentSettings ?? _settingsService.LoadSettings();
            var (activeOutputDevices, activeInputDevices) = _activeDeviceSnapshotProvider();

            PopulateConfiguredConnectivitySnapshot(
                snapshot.OutputConfiguredById,
                snapshot.ConnectedConfiguredOutputIds,
                savedSettings.DeviceSwitching.Output.CycleDevices,
                activeOutputDevices);

            PopulateConfiguredConnectivitySnapshot(
                snapshot.InputConfiguredById,
                snapshot.ConnectedConfiguredInputIds,
                savedSettings.DeviceSwitching.Input.CycleDevices,
                activeInputDevices);

            return snapshot;
        }

        private static void PopulateConfiguredConnectivitySnapshot(
            Dictionary<string, string> configuredById,
            HashSet<string> connectedConfiguredIds,
            IEnumerable<CycleDevice> configuredDevices,
            IReadOnlyList<CycleDevice> activeDevices)
        {
            var activeById = new Dictionary<string, CycleDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (CycleDevice? activeDevice in activeDevices)
            {
                if (activeDevice == null || string.IsNullOrWhiteSpace(activeDevice.Id) || activeById.ContainsKey(activeDevice.Id))
                {
                    continue;
                }

                activeById[activeDevice.Id] = activeDevice;
            }

            var consumedActiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CycleDevice? configuredDevice in configuredDevices)
            {
                if (configuredDevice == null || string.IsNullOrWhiteSpace(configuredDevice.Id) || configuredById.ContainsKey(configuredDevice.Id))
                {
                    continue;
                }

                CycleDevice? matchedActiveDevice = TryResolveConfiguredActiveMatch(configuredDevice, activeDevices, activeById, consumedActiveIds);
                configuredById[configuredDevice.Id] = ResolveSnapshotDeviceName(configuredDevice, matchedActiveDevice);
                if (matchedActiveDevice != null)
                {
                    connectedConfiguredIds.Add(configuredDevice.Id);
                    if (!string.IsNullOrWhiteSpace(matchedActiveDevice.Id))
                    {
                        consumedActiveIds.Add(matchedActiveDevice.Id);
                    }
                }
            }
        }

        private static CycleDevice? TryResolveConfiguredActiveMatch(
            CycleDevice configuredDevice,
            IReadOnlyList<CycleDevice> activeDevices,
            Dictionary<string, CycleDevice> activeById,
            HashSet<string> consumedActiveIds)
        {
            if (!string.IsNullOrWhiteSpace(configuredDevice.Id)
                && activeById.TryGetValue(configuredDevice.Id, out CycleDevice? exactMatch))
            {
                return exactMatch;
            }

            if (string.IsNullOrWhiteSpace(configuredDevice.Name))
            {
                return null;
            }

            CycleDevice? uniqueNameMatch = null;
            bool ambiguousNameMatch = false;

            foreach (CycleDevice? activeDevice in activeDevices)
            {
                if (activeDevice == null
                    || string.IsNullOrWhiteSpace(activeDevice.Name)
                    || (!string.IsNullOrWhiteSpace(activeDevice.Id) && consumedActiveIds.Contains(activeDevice.Id)))
                {
                    continue;
                }

                if (configuredDevice.Name.Equals(activeDevice.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (uniqueNameMatch == null)
                    {
                        uniqueNameMatch = activeDevice;
                        continue;
                    }

                    ambiguousNameMatch = true;
                    break;
                }
            }

            return ambiguousNameMatch ? null : uniqueNameMatch;
        }

        private static string ResolveSnapshotDeviceName(CycleDevice configuredDevice, CycleDevice? matchedActiveDevice)
        {
            if (matchedActiveDevice != null && !string.IsNullOrWhiteSpace(matchedActiveDevice.Name))
            {
                return matchedActiveDevice.Name;
            }

            if (!string.IsNullOrWhiteSpace(configuredDevice.Name))
            {
                return configuredDevice.Name;
            }

            return configuredDevice.Id;
        }

        private void ShowConfiguredHotplugOverlayIfNeeded(HotplugConfiguredConnectivitySnapshot before, HotplugConfiguredConnectivitySnapshot after)
        {
            List<string> outputConnected = GetTransitionedDeviceNames(
                before.ConnectedConfiguredOutputIds,
                after.ConnectedConfiguredOutputIds,
                after.OutputConfiguredById,
                becameConnected: true);

            List<string> outputDisconnected = GetTransitionedDeviceNames(
                before.ConnectedConfiguredOutputIds,
                after.ConnectedConfiguredOutputIds,
                before.OutputConfiguredById,
                becameConnected: false);

            List<string> inputConnected = GetTransitionedDeviceNames(
                before.ConnectedConfiguredInputIds,
                after.ConnectedConfiguredInputIds,
                after.InputConfiguredById,
                becameConnected: true);

            List<string> inputDisconnected = GetTransitionedDeviceNames(
                before.ConnectedConfiguredInputIds,
                after.ConnectedConfiguredInputIds,
                before.InputConfiguredById,
                becameConnected: false);

            if (outputConnected.Count > 0 && _appVm.ShouldSuppressConnectedHotplugOverlay(output: true))
            {
                outputConnected.Clear();
            }

            if (inputConnected.Count > 0 && _appVm.ShouldSuppressConnectedHotplugOverlay(output: false))
            {
                inputConnected.Clear();
            }

            if (outputConnected.Count == 0 && outputDisconnected.Count == 0 && inputConnected.Count == 0 && inputDisconnected.Count == 0)
            {
                return;
            }

            List<OverlayService.OverlayStackItem> overlayItems = [];

            overlayItems.AddRange(BuildHotplugOverlayItems(
                connectedNames: outputConnected,
                disconnectedNames: outputDisconnected,
                connectedPrefix: "Connected output",
                disconnectedPrefix: "Disconnected output",
                disconnectedKind: OverlayDeviceKind.Error,
                connectedKind: OverlayDeviceKind.Output));

            overlayItems.AddRange(BuildHotplugOverlayItems(
                connectedNames: inputConnected,
                disconnectedNames: inputDisconnected,
                connectedPrefix: "Connected input",
                disconnectedPrefix: "Disconnected input",
                disconnectedKind: OverlayDeviceKind.Error,
                connectedKind: OverlayDeviceKind.Input));

            if (overlayItems.Count == 0)
            {
                return;
            }

            if (overlayItems.Count == 1)
            {
                OverlayService.OverlayStackItem item = overlayItems[0];
                _overlayService.Show(item.Kind, item.Header, item.DeviceName);
                return;
            }

            _overlayService.ShowStacked(overlayItems);
        }

        private static List<OverlayService.OverlayStackItem> BuildHotplugOverlayItems(
            List<string> connectedNames,
            List<string> disconnectedNames,
            string connectedPrefix,
            string disconnectedPrefix,
            OverlayDeviceKind disconnectedKind,
            OverlayDeviceKind connectedKind)
        {
            List<OverlayService.OverlayStackItem> items = [];

            if (connectedNames.Count > 0)
            {
                items.Add(new OverlayService.OverlayStackItem(
                    connectedKind,
                    BuildHotplugHeader(connectedPrefix, connectedNames.Count),
                    FormatDeviceNameSummary(connectedNames)));
            }

            if (disconnectedNames.Count > 0)
            {
                items.Add(new OverlayService.OverlayStackItem(
                    disconnectedKind,
                    BuildHotplugHeader(disconnectedPrefix, disconnectedNames.Count),
                    FormatDeviceNameSummary(disconnectedNames)));
            }

            return items;
        }

        private static string BuildHotplugHeader(string prefix, int count)
        {
            return count == 1
                ? $"{prefix} device"
                : $"{prefix} devices";
        }

        private static List<string> GetTransitionedDeviceNames(
            HashSet<string> previous,
            HashSet<string> current,
            Dictionary<string, string> names,
            bool becameConnected)
        {
            var transitioned = new List<string>();

            if (becameConnected)
            {
                foreach (string id in current)
                {
                    if (previous.Contains(id))
                    {
                        continue;
                    }

                    transitioned.Add(names.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name) ? name : id);
                }
            }
            else
            {
                foreach (string id in previous)
                {
                    if (current.Contains(id))
                    {
                        continue;
                    }

                    transitioned.Add(names.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name) ? name : id);
                }
            }

            transitioned.Sort(StringComparer.OrdinalIgnoreCase);
            return transitioned;
        }

        private static string FormatDeviceNameSummary(List<string> names)
        {
            if (names.Count <= 1)
            {
                return string.Join(", ", names);
            }

            return string.Join("\n", names.Select(static name => $"- {name}"));
        }
    }
}
