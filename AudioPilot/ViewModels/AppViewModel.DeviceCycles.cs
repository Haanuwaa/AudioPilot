using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AudioPilot.Models;

namespace AudioPilot.ViewModels
{
    public partial class AppViewModel
    {
        private const string AllDevicesOptionLabel = "All Devices";

        public ObservableCollection<CycleDevice> SelectedOutputCycleDevices { get; } = [];

        public ObservableCollection<CycleDevice> SelectedInputCycleDevices { get; } = [];

        public bool HasSelectedOutputCycleDevices => SelectedOutputCycleDevices.Count > 0;

        public bool HasSelectedInputCycleDevices => SelectedInputCycleDevices.Count > 0;

        public bool HasSingleSelectedOutputCycleDevice => SelectedOutputCycleDevices.Count == 1;

        public bool HasSingleSelectedInputCycleDevice => SelectedInputCycleDevices.Count == 1;

        private void InitializeDeviceCycleSelectionTracking()
        {
            SelectedOutputCycleDevices.CollectionChanged += OnSelectedOutputCycleDevicesChanged;
            SelectedInputCycleDevices.CollectionChanged += OnSelectedInputCycleDevicesChanged;
        }

        private void OnSelectedOutputCycleDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasSelectedOutputCycleDevices));
            OnPropertyChanged(nameof(HasSingleSelectedOutputCycleDevice));
        }

        private void OnSelectedInputCycleDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasSelectedInputCycleDevices));
            OnPropertyChanged(nameof(HasSingleSelectedInputCycleDevice));
        }

        private bool CanAddInputCycleDevice()
        {
            if (SelectedAvailableInputIndex < 0)
            {
                return false;
            }

            if (IsAllDevicesSelection(SelectedAvailableInputIndex))
            {
                return CanAddAnyDevice(_inputDevices, InputCycleDevices);
            }

            int selectedDeviceIndex = ToDeviceIndex(SelectedAvailableInputIndex);
            if (selectedDeviceIndex < 0 || selectedDeviceIndex >= _inputDevices.Count)
            {
                return false;
            }

            string selectedDeviceId = _inputDevices[selectedDeviceIndex].Id;
            return !ContainsDevice(InputCycleDevices, selectedDeviceId);
        }

        private void LoadInputDevices()
        {
            SelectedAvailableInputIndex = LoadAvailableInputDevices(
                _inputDevices,
                AvailableInputDeviceNames,
                GetActiveInputDeviceInfos(),
                _selectedAvailableInputIndex);
        }

        private void ApplyInputCycleFromSettings(IEnumerable<CycleDevice>? devices)
        {
            AppViewModelDeviceCycleHelper.ApplyCycleFromSettingsCore(InputCycleDevices, devices);
            SelectedInputCycleDevices.Clear();

            AppViewModelDeviceCycleHelper.ReindexCycleDevices(InputCycleDevices);

            if (_selectedInputCycleIndex >= InputCycleDevices.Count)
            {
                SelectedInputCycleIndex = -1;
            }
        }

        private void AddInputCycleDevice()
        {
            if (SelectedAvailableInputIndex < 0)
            {
                return;
            }

            if (IsAllDevicesSelection(SelectedAvailableInputIndex))
            {
                int added = AppViewModelDeviceCycleHelper.AddAllMissingDevices(_inputDevices, InputCycleDevices);
                if (added == 0)
                {
                    return;
                }

                AppViewModelDeviceCycleHelper.ReindexCycleDevices(InputCycleDevices);
                SelectedInputCycleIndex = InputCycleDevices.Count - 1;
                SelectedAvailableInputIndex = 0;
                return;
            }

            int selectedDeviceIndex = ToDeviceIndex(SelectedAvailableInputIndex);
            if (selectedDeviceIndex < 0 || selectedDeviceIndex >= _inputDevices.Count)
            {
                return;
            }

            var selected = _inputDevices[selectedDeviceIndex];
            if (ContainsDevice(InputCycleDevices, selected.Id))
            {
                return;
            }

            InputCycleDevices.Add(new CycleDevice
            {
                Id = selected.Id,
                Name = selected.Name,
            });
            AppViewModelDeviceCycleHelper.ReindexCycleDevices(InputCycleDevices);
            SelectedInputCycleIndex = InputCycleDevices.Count - 1;
            SelectNextAvailableInputDevice();
        }

        private void SelectNextAvailableInputDevice()
        {
            int nextDeviceIndex = AppViewModelDeviceCycleHelper.FindNextAvailableDeviceIndex(_inputDevices, InputCycleDevices, ToDeviceIndex(SelectedAvailableInputIndex));
            SelectedAvailableInputIndex = nextDeviceIndex >= 0 ? ToComboIndex(nextDeviceIndex) : 0;
        }

        private void RemoveInputCycleDevice()
        {
            List<int> selectedIndices = GetSelectedCycleIndices(InputCycleDevices, SelectedInputCycleDevices, SelectedInputCycleIndex);
            if (selectedIndices.Count == 0)
            {
                return;
            }

            int nextSelectedIndex = selectedIndices[0];
            for (int i = selectedIndices.Count - 1; i >= 0; i--)
            {
                InputCycleDevices.RemoveAt(selectedIndices[i]);
            }
            SelectedInputCycleDevices.Clear();

            if (InputCycleDevices.Count == 0)
            {
                SelectedInputCycleIndex = -1;
            }
            else
            {
                SelectedInputCycleIndex = Math.Min(nextSelectedIndex, InputCycleDevices.Count - 1);
            }

            AppViewModelDeviceCycleHelper.ReindexCycleDevices(InputCycleDevices);
        }

        private void MoveInputCycleDeviceUp()
        {
            if (SelectedInputCycleIndex <= 0 || SelectedInputCycleIndex >= InputCycleDevices.Count)
            {
                return;
            }

            int index = SelectedInputCycleIndex;
            var item = InputCycleDevices[index];
            InputCycleDevices.RemoveAt(index);
            InputCycleDevices.Insert(index - 1, item);
            AppViewModelDeviceCycleHelper.ReindexCycleDevices(InputCycleDevices);
            SelectedInputCycleIndex = index - 1;
        }

        private void MoveInputCycleDeviceDown()
        {
            if (SelectedInputCycleIndex < 0 || SelectedInputCycleIndex >= InputCycleDevices.Count - 1)
            {
                return;
            }

            int index = SelectedInputCycleIndex;
            var item = InputCycleDevices[index];
            InputCycleDevices.RemoveAt(index);
            InputCycleDevices.Insert(index + 1, item);
            AppViewModelDeviceCycleHelper.ReindexCycleDevices(InputCycleDevices);
            SelectedInputCycleIndex = index + 1;
        }

        private bool CanAddOutputCycleDevice()
        {
            if (SelectedAvailableOutputIndex < 0)
            {
                return false;
            }

            if (IsAllDevicesSelection(SelectedAvailableOutputIndex))
            {
                return CanAddAnyDevice(_outputDevices, OutputCycleDevices);
            }

            int selectedDeviceIndex = ToDeviceIndex(SelectedAvailableOutputIndex);
            if (selectedDeviceIndex < 0 || selectedDeviceIndex >= _outputDevices.Count)
            {
                return false;
            }

            string selectedDeviceId = _outputDevices[selectedDeviceIndex].Id;
            return !ContainsDevice(OutputCycleDevices, selectedDeviceId);
        }

        private void LoadOutputDevices()
        {
            SelectedAvailableOutputIndex = LoadAvailableOutputDevices(
                _outputDevices,
                AvailableOutputDeviceNames,
                GetActiveOutputDeviceInfos(),
                _selectedAvailableOutputIndex);
        }

        private void RefreshListenMonitorOutputOptions(string? preferredDeviceId = null, string? preferredDeviceName = null)
        {
            CycleDevice selectedDevice = AppViewModelListenMonitorOutputHelper.RefreshOptions(
                SettingsListenMonitorOutputDevices,
                _outputDevices,
                SettingsListenMonitorOutputDeviceIdDraft,
                _settingsListenMonitorOutputDeviceNameDraft,
                preferredDeviceId,
                preferredDeviceName);

            if (!string.Equals(SettingsListenMonitorOutputDeviceIdDraft, selectedDevice.Id, StringComparison.Ordinal)
                || !string.Equals(_settingsListenMonitorOutputDeviceNameDraft, selectedDevice.Name, StringComparison.Ordinal))
            {
                SetSettingsListenMonitorOutputDraft(selectedDevice.Id, selectedDevice.Name);
            }
            else if (string.IsNullOrWhiteSpace(selectedDevice.Id))
            {
                OnPropertyChanged(nameof(SettingsListenMonitorOutputDeviceIdDraft));
            }
        }

        private int LoadAvailableOutputDevices(
            List<CycleDevice> targetDevices,
            ObservableCollection<string> targetNames,
            List<CycleDevice> refreshedDevices,
            int previousSelectedIndex)
        {
            int selectedIndex = AppViewModelDeviceCycleHelper.LoadAvailableDevices(
                targetDevices,
                targetNames,
                refreshedDevices,
                previousSelectedIndex,
                AllDevicesOptionLabel,
                includeAllOption: true);

            RefreshListenMonitorOutputOptions();
            if (IsInputTestPanelVisible)
            {
                RefreshAudioTestMonitorOutputOptions();
            }

            RefreshRoutineDeviceOptions();
            return selectedIndex;
        }

        private int LoadAvailableInputDevices(
            List<CycleDevice> targetDevices,
            ObservableCollection<string> targetNames,
            List<CycleDevice> refreshedDevices,
            int previousSelectedIndex)
        {
            int selectedIndex = AppViewModelDeviceCycleHelper.LoadAvailableDevices(
                targetDevices,
                targetNames,
                refreshedDevices,
                previousSelectedIndex,
                AllDevicesOptionLabel,
                includeAllOption: true);

            RefreshRoutineDeviceOptions();
            return selectedIndex;
        }

        private void ApplyOutputCycleFromSettings(IEnumerable<CycleDevice>? devices)
        {
            AppViewModelDeviceCycleHelper.ApplyCycleFromSettingsCore(OutputCycleDevices, devices);
            SelectedOutputCycleDevices.Clear();

            AppViewModelDeviceCycleHelper.ReindexCycleDevices(OutputCycleDevices);

            if (_selectedOutputCycleIndex >= OutputCycleDevices.Count)
            {
                SelectedOutputCycleIndex = -1;
            }
        }

        internal static List<CycleDevice> CloneCycleDevices(IEnumerable<CycleDevice>? devices)
        {
            return AppViewModelDeviceCycleHelper.CloneCycleDevices(devices);
        }

        private void AddOutputCycleDevice()
        {
            if (SelectedAvailableOutputIndex < 0)
            {
                return;
            }

            if (IsAllDevicesSelection(SelectedAvailableOutputIndex))
            {
                int added = AppViewModelDeviceCycleHelper.AddAllMissingDevices(_outputDevices, OutputCycleDevices);
                if (added == 0)
                {
                    return;
                }

                AppViewModelDeviceCycleHelper.ReindexCycleDevices(OutputCycleDevices);
                SelectedOutputCycleIndex = OutputCycleDevices.Count - 1;
                SelectedAvailableOutputIndex = 0;
                return;
            }

            int selectedDeviceIndex = ToDeviceIndex(SelectedAvailableOutputIndex);
            if (selectedDeviceIndex < 0 || selectedDeviceIndex >= _outputDevices.Count)
            {
                return;
            }

            var selected = _outputDevices[selectedDeviceIndex];
            if (ContainsDevice(OutputCycleDevices, selected.Id))
            {
                return;
            }

            OutputCycleDevices.Add(new CycleDevice
            {
                Id = selected.Id,
                Name = selected.Name,
            });
            AppViewModelDeviceCycleHelper.ReindexCycleDevices(OutputCycleDevices);
            SelectedOutputCycleIndex = OutputCycleDevices.Count - 1;
            SelectNextAvailableOutputDevice();
        }

        private void SelectNextAvailableOutputDevice()
        {
            int nextDeviceIndex = AppViewModelDeviceCycleHelper.FindNextAvailableDeviceIndex(_outputDevices, OutputCycleDevices, ToDeviceIndex(SelectedAvailableOutputIndex));
            SelectedAvailableOutputIndex = nextDeviceIndex >= 0 ? ToComboIndex(nextDeviceIndex) : 0;
        }

        internal static bool IsAllDevicesSelection(int selectedIndex)
        {
            return AppViewModelDeviceCycleHelper.IsAllDevicesSelection(selectedIndex);
        }

        internal static int ToDeviceIndex(int selectedIndex)
        {
            return AppViewModelDeviceCycleHelper.ToDeviceIndex(selectedIndex);
        }

        internal static int ToComboIndex(int deviceIndex)
        {
            return AppViewModelDeviceCycleHelper.ToComboIndex(deviceIndex);
        }

        internal static bool CanAddAnyDevice(IEnumerable<CycleDevice> availableDevices, IEnumerable<CycleDevice> cycleDevices)
        {
            return AppViewModelDeviceCycleHelper.CanAddAnyDevice(availableDevices, cycleDevices);
        }

        private static void DisposeDevices(List<CycleDevice> devices)
        {
            devices.Clear();
        }

        private static bool ContainsDevice(IEnumerable<CycleDevice> cycleDevices, string deviceId)
        {
            return AppViewModelDeviceCycleHelper.ContainsDevice(cycleDevices, deviceId);
        }

        private List<CycleDevice> GetActiveOutputDeviceInfos()
        {
            return _audio.GetActivePlaybackCycleEntries();
        }

        private List<CycleDevice> GetActiveInputDeviceInfos()
        {
            return _audio.GetActiveCaptureCycleEntries();
        }

        private (List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices) GetActiveDeviceInfoSnapshot()
        {
            return (GetActiveOutputDeviceInfos(), GetActiveInputDeviceInfos());
        }

        internal (List<CycleDevice> OutputDevices, List<CycleDevice> InputDevices) GetKnownActiveDeviceInfoSnapshot(bool fallbackToEnumeration = true, bool forceEnumeration = false)
        {
            if (!forceEnumeration && (_outputDevices.Count > 0 || _inputDevices.Count > 0))
            {
                return (CloneDeviceInfoList(_outputDevices), CloneDeviceInfoList(_inputDevices));
            }

            return fallbackToEnumeration
                ? GetActiveDeviceInfoSnapshot()
                : ([], []);
        }

        private static List<CycleDevice> CloneDeviceInfoList(IEnumerable<CycleDevice> devices)
        {
            var cloned = new List<CycleDevice>();
            foreach (var device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Id))
                {
                    continue;
                }

                cloned.Add(new CycleDevice
                {
                    Id = device.Id,
                    Name = device.Name,
                    DisplayOrder = device.DisplayOrder,
                });
            }

            return cloned;
        }

        private void RemoveOutputCycleDevice()
        {
            List<int> selectedIndices = GetSelectedCycleIndices(OutputCycleDevices, SelectedOutputCycleDevices, SelectedOutputCycleIndex);
            if (selectedIndices.Count == 0)
            {
                return;
            }

            int nextSelectedIndex = selectedIndices[0];
            for (int i = selectedIndices.Count - 1; i >= 0; i--)
            {
                OutputCycleDevices.RemoveAt(selectedIndices[i]);
            }
            SelectedOutputCycleDevices.Clear();

            if (OutputCycleDevices.Count == 0)
            {
                SelectedOutputCycleIndex = -1;
            }
            else
            {
                SelectedOutputCycleIndex = Math.Min(nextSelectedIndex, OutputCycleDevices.Count - 1);
            }

            AppViewModelDeviceCycleHelper.ReindexCycleDevices(OutputCycleDevices);
        }

        private static List<int> GetSelectedCycleIndices(
            ObservableCollection<CycleDevice> cycleDevices,
            ObservableCollection<CycleDevice> selectedDevices,
            int fallbackSelectedIndex)
        {
            if (selectedDevices.Count > 0)
            {
                HashSet<CycleDevice> selectedDeviceSet = [.. selectedDevices];
                var selectedIndices = new List<int>();

                for (int index = 0; index < cycleDevices.Count; index++)
                {
                    if (selectedDeviceSet.Contains(cycleDevices[index]))
                    {
                        selectedIndices.Add(index);
                    }
                }

                if (selectedIndices.Count > 0)
                {
                    return selectedIndices;
                }
            }

            return fallbackSelectedIndex >= 0 && fallbackSelectedIndex < cycleDevices.Count
                ? [fallbackSelectedIndex]
                : [];
        }

        private void MoveOutputCycleDeviceUp()
        {
            if (SelectedOutputCycleIndex <= 0 || SelectedOutputCycleIndex >= OutputCycleDevices.Count)
            {
                return;
            }

            int index = SelectedOutputCycleIndex;
            var item = OutputCycleDevices[index];
            OutputCycleDevices.RemoveAt(index);
            OutputCycleDevices.Insert(index - 1, item);
            AppViewModelDeviceCycleHelper.ReindexCycleDevices(OutputCycleDevices);
            SelectedOutputCycleIndex = index - 1;
        }

        private void MoveOutputCycleDeviceDown()
        {
            if (SelectedOutputCycleIndex < 0 || SelectedOutputCycleIndex >= OutputCycleDevices.Count - 1)
            {
                return;
            }

            int index = SelectedOutputCycleIndex;
            var item = OutputCycleDevices[index];
            OutputCycleDevices.RemoveAt(index);
            OutputCycleDevices.Insert(index + 1, item);
            AppViewModelDeviceCycleHelper.ReindexCycleDevices(OutputCycleDevices);
            SelectedOutputCycleIndex = index + 1;
        }

        private List<string> GetDisconnectedConfiguredDeviceNames(IEnumerable<CycleDevice> configuredCycle, bool output)
        {
            var configured = new List<CycleDevice>();
            foreach (var device in configuredCycle)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Id))
                {
                    continue;
                }

                configured.Add(new CycleDevice { Id = device.Id, Name = device.Name });
            }

            if (configured.Count == 0)
            {
                return [];
            }

            List<CycleDevice> activeDevices = output
                ? GetActiveOutputDeviceInfos()
                : GetActiveInputDeviceInfos();

            CycleValidationResult validationResult = SettingsValidationService.ValidateCycle(configured, activeDevices);
            return [.. validationResult.DisconnectedDeviceNames];
        }
    }
}
