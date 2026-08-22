using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.ViewModels;

public partial class AppViewModel
{
    private DispatcherTimer _audioTestUiTimer = null!;
    private AudioEndpointTestState _audioTestState = AudioEndpointTestState.Idle;
    private readonly AudioEndpointTestPreferences _audioTestPreferences = new();
    private CancellationTokenSource? _audioTestMonitorDebounceCts;
    private string _audioTestSelectedMonitorOutputId = string.Empty;
    private double _audioTestLevelPercent;
    private double _audioTestPeakPercent;
    private string _audioTestElapsed = "00:00";
    private string _audioTestMonitorFallbackNotice = string.Empty;
    private bool _updatingAudioTestUi;

    public ObservableCollection<CycleDevice> AudioTestMonitorOutputDevices { get; } = [];

    public ICommand TestOutputDeviceCommand { get; private set; } = null!;
    public ICommand StopAudioTestCommand { get; private set; } = null!;
    public ICommand TestInputDeviceCommand { get; private set; } = null!;
    public ICommand SetDefaultOutputDeviceCommand { get; private set; } = null!;
    public ICommand SetDefaultInputDeviceCommand { get; private set; } = null!;

    public bool IsOutputTestStatusVisible =>
        _audioTestState.Kind == AudioEndpointTestKind.Output && _audioTestState.Phase != AudioEndpointTestPhase.Idle;

    public bool IsOutputTestRunning =>
        _audioTestState.Kind == AudioEndpointTestKind.Output &&
        _audioTestState.Phase is AudioEndpointTestPhase.Starting or AudioEndpointTestPhase.Running or AudioEndpointTestPhase.Stopping;

    public bool IsInputTestPanelVisible =>
        _audioTestState.Kind == AudioEndpointTestKind.Input && _audioTestState.Phase != AudioEndpointTestPhase.Idle;

    public bool IsInputTestRunning =>
        _audioTestState.Kind == AudioEndpointTestKind.Input &&
        _audioTestState.Phase is AudioEndpointTestPhase.Starting or AudioEndpointTestPhase.Running or AudioEndpointTestPhase.Stopping;

    public string AudioTestStatus => _audioTestState.Status;
    public string AudioTestEndpointName => _audioTestState.Endpoint.Name;
    public string AudioTestActionText => _audioTestState.Phase == AudioEndpointTestPhase.Failed ? "Dismiss" : "Stop";

    public double AudioTestLevelPercent
    {
        get => _audioTestLevelPercent;
        private set
        {
            if (Math.Abs(_audioTestLevelPercent - value) < 0.05) return;
            _audioTestLevelPercent = value;
            OnPropertyChanged(nameof(AudioTestLevelPercent));
            OnPropertyChanged(nameof(AudioTestLevelText));
        }
    }

    public double AudioTestPeakPercent
    {
        get => _audioTestPeakPercent;
        private set
        {
            if (Math.Abs(_audioTestPeakPercent - value) < 0.05) return;
            _audioTestPeakPercent = value;
            OnPropertyChanged(nameof(AudioTestPeakPercent));
        }
    }

    public string AudioTestLevelText => AudioTestLevelPercent is > 0 and < 10
        ? $"{AudioTestLevelPercent:0.0}%"
        : $"{AudioTestLevelPercent:0}%";

    public string AudioTestElapsed
    {
        get => _audioTestElapsed;
        private set
        {
            if (_audioTestElapsed == value) return;
            _audioTestElapsed = value;
            OnPropertyChanged(nameof(AudioTestElapsed));
        }
    }

    public bool AudioTestHearMyself
    {
        get => _audioTestPreferences.HearMyself;
        set
        {
            if (_audioTestPreferences.HearMyself == value) return;
            _audioTestPreferences.HearMyself = value;
            OnPropertyChanged(nameof(AudioTestHearMyself));
            if (!_updatingAudioTestUi) QueueAudioTestMonitorConfiguration(0);
        }
    }

    public double AudioTestMonitorVolume
    {
        get => _audioTestPreferences.MonitorVolumePercent;
        set
        {
            double normalized = Math.Clamp(value, 0, 100);
            if (Math.Abs(_audioTestPreferences.MonitorVolumePercent - normalized) < 0.05) return;
            _audioTestPreferences.MonitorVolumePercent = normalized;
            OnPropertyChanged(nameof(AudioTestMonitorVolume));
            OnPropertyChanged(nameof(AudioTestMonitorVolumeText));
            if (!_updatingAudioTestUi && AudioTestHearMyself) QueueAudioTestMonitorConfiguration(50);
        }
    }

    public string AudioTestMonitorVolumeText => $"{AudioTestMonitorVolume:0}%";

    public string AudioTestSelectedMonitorOutputId
    {
        get => _audioTestSelectedMonitorOutputId;
        set
        {
            string normalized = value ?? string.Empty;
            if (_audioTestSelectedMonitorOutputId == normalized) return;
            _audioTestSelectedMonitorOutputId = normalized;
            if (!_updatingAudioTestUi)
                _audioTestPreferences.RememberMonitorOutput(normalized);
            OnPropertyChanged(nameof(AudioTestSelectedMonitorOutputId));
            if (!_updatingAudioTestUi && AudioTestHearMyself) QueueAudioTestMonitorConfiguration(0);
        }
    }

    public string AudioTestMonitorFallbackNotice
    {
        get => _audioTestMonitorFallbackNotice;
        private set
        {
            if (_audioTestMonitorFallbackNotice == value) return;
            _audioTestMonitorFallbackNotice = value;
            OnPropertyChanged(nameof(AudioTestMonitorFallbackNotice));
            OnPropertyChanged(nameof(HasAudioTestMonitorFallbackNotice));
        }
    }

    public bool HasAudioTestMonitorFallbackNotice => !string.IsNullOrWhiteSpace(AudioTestMonitorFallbackNotice);

    private void InitializeAudioEndpointTesting()
    {
        _audioEndpointTestService.StateChanged += OnAudioEndpointTestStateChanged;
        _audioTestUiTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _audioTestUiTimer.Tick += OnAudioTestUiTimerTick;

        TestOutputDeviceCommand = TrackCommand(new RelayCommand(TestOutputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAudioTestCommandException("test-output", ex)));
        StopAudioTestCommand = TrackCommand(new RelayCommand(() => StopAudioEndpointTestAsync(AudioEndpointTestStopReason.User), () => _audioTestState.Phase != AudioEndpointTestPhase.Idle, ex => HandleAudioTestCommandException("stop-test", ex)), observeViewModel: true);
        TestInputDeviceCommand = TrackCommand(new RelayCommand(TestInputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAudioTestCommandException("test-input", ex)));
        SetDefaultOutputDeviceCommand = TrackCommand(new RelayCommand(SetDefaultOutputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAudioTestCommandException("set-default-output", ex)));
        SetDefaultInputDeviceCommand = TrackCommand(new RelayCommand(SetDefaultInputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAudioTestCommandException("set-default-input", ex)));
    }

    private static bool IsUsableCycleDevice(object? parameter) => parameter is CycleDevice { Id.Length: > 0 };

    private async Task TestOutputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is CycleDevice device)
            await _audioEndpointTestService.StartOutputTestAsync(AudioEndpointReference.FromCycleDevice(device), ShutdownToken);
    }

    private async Task TestInputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is not CycleDevice device) return;
        RefreshAudioTestMonitorOutputOptions();
        await _audioEndpointTestService.StartInputTestAsync(AudioEndpointReference.FromCycleDevice(device), ResolveSelectedAudioTestMonitorEndpoint(), ShutdownToken);
        if (AudioTestHearMyself)
        {
            await _audioEndpointTestService.ConfigureInputMonitoringAsync(
                true,
                ResolveSelectedAudioTestMonitorEndpoint(),
                (float)(AudioTestMonitorVolume / 100d),
                ShutdownToken);
        }
    }

    private async Task SetDefaultOutputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is not CycleDevice device) return;
        await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
        Settings settings = CurrentSettings ?? new Settings();
        await _switchCoordinator.SwitchOutputToDeviceAsync(device.Clone(), MuteMic, MuteSound, Deafen, PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings), ScheduleOutputPostSwitchRefresh);
    }

    private async Task SetDefaultInputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is not CycleDevice device) return;
        await StopAudioEndpointTestAsync(AudioEndpointTestStopReason.Replaced);
        Settings settings = CurrentSettings ?? new Settings();
        await _switchCoordinator.SwitchInputToDeviceAsync(device.Clone(), PreserveAudioLevels, BluetoothReconnectOptions.FromSettings(settings));
    }

    private void RefreshAudioTestMonitorOutputOptions()
    {
        string selectedMonitorOutputId = _audioTestPreferences.ResolveAvailableMonitorOutputId(
            _outputDevices.Select(static device => device.Id),
            CurrentSettings?.Hotkeys.Listen.MonitorOutputDeviceId,
            out bool usedDefaultFallback);

        _updatingAudioTestUi = true;
        try
        {
            AudioTestMonitorOutputDevices.Clear();
            AudioTestMonitorOutputDevices.Add(new CycleDevice { Id = string.Empty, Name = "Default output" });
            foreach (CycleDevice device in _outputDevices)
            {
                if (!string.IsNullOrWhiteSpace(device.Id))
                {
                    AudioTestMonitorOutputDevices.Add(device.Clone());
                }
            }

            AudioTestSelectedMonitorOutputId = selectedMonitorOutputId;
        }
        finally
        {
            _updatingAudioTestUi = false;
        }

        AudioTestMonitorFallbackNotice = usedDefaultFallback
            ? "The last monitor output is unavailable, so Default output will be used for this test."
            : string.Empty;
    }

    private AudioEndpointReference? ResolveSelectedAudioTestMonitorEndpoint()
    {
        if (string.IsNullOrWhiteSpace(AudioTestSelectedMonitorOutputId)) return null;
        CycleDevice? selected = AudioTestMonitorOutputDevices.FirstOrDefault(device => device.Id.Equals(AudioTestSelectedMonitorOutputId, StringComparison.OrdinalIgnoreCase));
        return selected == null ? null : AudioEndpointReference.FromCycleDevice(selected);
    }

    private void QueueAudioTestMonitorConfiguration(int delayMs)
    {
        CancellationTokenSource nextCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
            ref _audioTestMonitorDebounceCts);
        bool enabled = AudioTestHearMyself;
        float volume = (float)(AudioTestMonitorVolume / 100.0);
        AudioEndpointReference? endpoint = ResolveSelectedAudioTestMonitorEndpoint();
        bool queued = TryRunBackgroundWork(shutdownToken =>
        {
            return AppDebouncedBackgroundWorkCoordinator.ExecuteDelayedAsync(
                nextCts,
                owned => AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(
                    ref _audioTestMonitorDebounceCts,
                    owned),
                delayMs,
                token => _audioEndpointTestService.ConfigureInputMonitoringAsync(enabled, endpoint, volume, token),
                shutdownToken);
        }, "configure-input-test-monitoring");

        if (!queued)
        {
            AppDebouncedBackgroundWorkCoordinator.ReleaseOwned(ref _audioTestMonitorDebounceCts, nextCts);
        }
    }

    private void OnAudioEndpointTestStateChanged(AudioEndpointTestState state)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplyAudioEndpointTestState(state);
        }
        else if (!AppDispatcherHelper.IsDispatcherUnavailable(_dispatcher))
        {
            _ = _dispatcher.BeginInvoke(() => ApplyAudioEndpointTestState(state), DispatcherPriority.Background);
        }
    }

    private void ApplyAudioEndpointTestState(AudioEndpointTestState state)
    {
        if (state.Revision < _audioTestState.Revision)
        {
            _logger.Debug(
                "AppViewModel",
                () => $"audio-test-stale-ui-state-ignored | incomingRevision={state.Revision} currentRevision={_audioTestState.Revision} phase={state.Phase}");
            return;
        }

        _audioTestState = state;
        string[] properties = [nameof(IsOutputTestStatusVisible), nameof(IsOutputTestRunning), nameof(IsInputTestPanelVisible), nameof(IsInputTestRunning), nameof(AudioTestStatus), nameof(AudioTestEndpointName), nameof(AudioTestActionText)];
        foreach (string property in properties) OnPropertyChanged(property);

        if (IsInputTestRunning) _audioTestUiTimer.Start();
        else
        {
            _audioTestUiTimer.Stop();
            AudioTestLevelPercent = 0;
            AudioTestPeakPercent = 0;
            AudioTestElapsed = "00:00";
        }
    }

    private void OnAudioTestUiTimerTick(object? sender, EventArgs e)
    {
        AudioInputLevelSnapshot level = _audioEndpointTestService.ReadInputLevel();
        AudioTestLevelPercent = level.LevelPercent;
        AudioTestPeakPercent = level.PeakPercent;
        if (_audioTestState.StartedAt is { } startedAt)
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
            AudioTestElapsed = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        }
    }

    internal Task StopAudioEndpointTestAsync(AudioEndpointTestStopReason reason) => _audioEndpointTestService.StopAsync(reason);
    internal Task ReconcileAudioEndpointTestDevicesAsync(CancellationToken cancellationToken) => _audioEndpointTestService.ReconcileActiveEndpointsAsync(cancellationToken);

    private void RequestStopAudioEndpointTest(AudioEndpointTestStopReason reason)
    {
        if (_audioEndpointTestService.CurrentState.Phase != AudioEndpointTestPhase.Idle)
        {
            _ = StopAudioEndpointTestForLifecycleAsync(reason);
        }
    }

    private async Task StopAudioEndpointTestForLifecycleAsync(AudioEndpointTestStopReason reason)
    {
        try
        {
            await _audioEndpointTestService.StopAsync(reason);
        }
        catch (ObjectDisposedException) when (_isCleaningUp)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "AppViewModel",
                () => $"audio-test-lifecycle-stop-failed | reason={reason} error={ex.GetType().Name}",
                nameof(StopAudioEndpointTestForLifecycleAsync),
                ex);
        }
    }

    private void HandleAudioTestCommandException(string operation, Exception exception) =>
        _logger.Warning("AppViewModel", () => $"audio-test-command-failed | operation={operation} error={exception.GetType().Name}", nameof(HandleAudioTestCommandException), exception);

    private async Task DisposeAudioEndpointTestingAsync()
    {
        _audioTestUiTimer.Stop();
        _audioTestUiTimer.Tick -= OnAudioTestUiTimerTick;
        _audioEndpointTestService.StateChanged -= OnAudioEndpointTestStateChanged;
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _audioTestMonitorDebounceCts);
        await _audioEndpointTestService.DisposeAsync();
    }
}
