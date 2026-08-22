using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AudioPilot.Coordinators;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Models;
using AudioPilot.Services.Audio.Testing;

namespace AudioPilot.ViewModels;

/// <summary>Owns endpoint-test commands, monitor preferences, UI state, and the test service lifetime.</summary>
public sealed class AudioTestingViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IAudioEndpointTestService _audioEndpointTestService;
    private readonly Dispatcher _dispatcher;
    private readonly Logger _logger;
    private readonly CancellationToken _shutdownToken;
    private readonly Func<(IReadOnlyList<CycleDevice> Devices, string? PreferredOutputId)> _getMonitorOutputs;
    private readonly Func<Func<CancellationToken, Task>, string, bool> _tryRunBackgroundWork;
    private volatile bool _isDisposed;

    internal AudioTestingViewModel(
        IAudioEndpointTestService service,
        Dispatcher dispatcher,
        Logger logger,
        Func<(IReadOnlyList<CycleDevice> Devices, string? PreferredOutputId)> getMonitorOutputs,
        Func<Func<CancellationToken, Task>, string, bool> tryRunBackgroundWork,
        CancellationToken shutdownToken)
    {
        _audioEndpointTestService = service;
        _dispatcher = dispatcher;
        _logger = logger;
        _shutdownToken = shutdownToken;
        _getMonitorOutputs = getMonitorOutputs;
        _tryRunBackgroundWork = tryRunBackgroundWork;
        _audioEndpointTestService.StateChanged += OnAudioEndpointTestStateChanged;
        _audioTestUiTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _audioTestUiTimer.Tick += OnAudioTestUiTimerTick;

        TestOutputCommand = new RelayCommand(TestOutputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAudioTestCommandException("test-output", ex));
        StopCommand = new RelayCommand(() => StopAudioEndpointTestAsync(AudioEndpointTestStopReason.User), () => !_isDisposed && _audioTestState.Phase != AudioEndpointTestPhase.Idle, ex => HandleAudioTestCommandException("stop-test", ex));
        StopCommand.AddObservedSource(this);
        TestInputCommand = new RelayCommand(TestInputDeviceFromContextAsync, IsUsableCycleDevice, ex => HandleAudioTestCommandException("test-input", ex));
        ApplyAudioEndpointTestState(service.CurrentState);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private readonly DispatcherTimer _audioTestUiTimer;
    private AudioEndpointTestState _audioTestState = AudioEndpointTestState.Idle;
    private readonly AudioEndpointTestPreferences _audioTestPreferences = new();
    private CancellationTokenSource? _audioTestMonitorDebounceCts;
    private string _audioTestSelectedMonitorOutputId = string.Empty;
    private double _audioTestLevelPercent;
    private double _audioTestPeakPercent;
    private string _audioTestElapsed = "00:00";
    private string _audioTestMonitorFallbackNotice = string.Empty;
    private bool _updatingAudioTestUi;

    public ObservableCollection<CycleDevice> MonitorOutputDevices { get; } = [];

    public RelayCommand TestOutputCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand TestInputCommand { get; }

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

    public string Status => _audioTestState.Status;
    public string EndpointName => _audioTestState.Endpoint.Name;
    public string ActionText => _audioTestState.Phase == AudioEndpointTestPhase.Failed ? "Dismiss" : "Stop";

    public double LevelPercent
    {
        get => _audioTestLevelPercent;
        private set
        {
            if (Math.Abs(_audioTestLevelPercent - value) < 0.05) return;
            _audioTestLevelPercent = value;
            OnPropertyChanged(nameof(LevelPercent));
            OnPropertyChanged(nameof(LevelText));
        }
    }

    public double PeakPercent
    {
        get => _audioTestPeakPercent;
        private set
        {
            if (Math.Abs(_audioTestPeakPercent - value) < 0.05) return;
            _audioTestPeakPercent = value;
            OnPropertyChanged(nameof(PeakPercent));
        }
    }

    public string LevelText => LevelPercent is > 0 and < 10
        ? $"{LevelPercent:0.0}%"
        : $"{LevelPercent:0}%";

    public string Elapsed
    {
        get => _audioTestElapsed;
        private set
        {
            if (_audioTestElapsed == value) return;
            _audioTestElapsed = value;
            OnPropertyChanged(nameof(Elapsed));
        }
    }

    public bool HearMyself
    {
        get => _audioTestPreferences.HearMyself;
        set
        {
            if (_audioTestPreferences.HearMyself == value) return;
            _audioTestPreferences.HearMyself = value;
            OnPropertyChanged(nameof(HearMyself));
            if (!_updatingAudioTestUi) QueueAudioTestMonitorConfiguration(0);
        }
    }

    public double MonitorVolume
    {
        get => _audioTestPreferences.MonitorVolumePercent;
        set
        {
            double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
            if (Math.Abs(_audioTestPreferences.MonitorVolumePercent - normalized) < 0.05) return;
            _audioTestPreferences.MonitorVolumePercent = normalized;
            OnPropertyChanged(nameof(MonitorVolume));
            OnPropertyChanged(nameof(MonitorVolumeText));
            if (!_updatingAudioTestUi && HearMyself) QueueAudioTestMonitorConfiguration(50);
        }
    }

    public string MonitorVolumeText => $"{MonitorVolume:0}%";

    public string SelectedMonitorOutputId
    {
        get => _audioTestSelectedMonitorOutputId;
        set
        {
            string normalized = value ?? string.Empty;
            if (_audioTestSelectedMonitorOutputId == normalized) return;
            _audioTestSelectedMonitorOutputId = normalized;
            if (!_updatingAudioTestUi)
                _audioTestPreferences.RememberMonitorOutput(normalized);
            OnPropertyChanged(nameof(SelectedMonitorOutputId));
            if (!_updatingAudioTestUi && HearMyself) QueueAudioTestMonitorConfiguration(0);
        }
    }

    public string MonitorFallbackNotice
    {
        get => _audioTestMonitorFallbackNotice;
        private set
        {
            if (_audioTestMonitorFallbackNotice == value) return;
            _audioTestMonitorFallbackNotice = value;
            OnPropertyChanged(nameof(MonitorFallbackNotice));
            OnPropertyChanged(nameof(HasMonitorFallbackNotice));
        }
    }

    public bool HasMonitorFallbackNotice => !string.IsNullOrWhiteSpace(MonitorFallbackNotice);

    private bool IsUsableCycleDevice(object? parameter) => !_isDisposed && parameter is CycleDevice { Id.Length: > 0 };

    private async Task TestOutputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is CycleDevice device)
            await _audioEndpointTestService.StartOutputTestAsync(AudioEndpointReference.FromCycleDevice(device), _shutdownToken);
    }

    private async Task TestInputDeviceFromContextAsync(object? parameter)
    {
        if (parameter is not CycleDevice device) return;
        RefreshAudioTestMonitorOutputOptions();
        await _audioEndpointTestService.StartInputTestAsync(AudioEndpointReference.FromCycleDevice(device), ResolveSelectedAudioTestMonitorEndpoint(), _shutdownToken);
        if (HearMyself)
        {
            await _audioEndpointTestService.ConfigureInputMonitoringAsync(
                true,
                ResolveSelectedAudioTestMonitorEndpoint(),
                (float)(MonitorVolume / 100d),
                _shutdownToken);
        }
    }

    internal void RefreshAudioTestMonitorOutputOptions()
    {
        (IReadOnlyList<CycleDevice> outputDevices, string? preferredOutputId) = _getMonitorOutputs();
        string selectedMonitorOutputId = _audioTestPreferences.ResolveAvailableMonitorOutputId(
            outputDevices.Select(static device => device.Id),
            preferredOutputId,
            out bool usedDefaultFallback);

        _updatingAudioTestUi = true;
        try
        {
            MonitorOutputDevices.Clear();
            MonitorOutputDevices.Add(new CycleDevice { Id = string.Empty, Name = "Default output" });
            foreach (CycleDevice device in outputDevices)
            {
                if (!string.IsNullOrWhiteSpace(device.Id))
                {
                    MonitorOutputDevices.Add(device.Clone());
                }
            }

            SelectedMonitorOutputId = selectedMonitorOutputId;
        }
        finally
        {
            _updatingAudioTestUi = false;
        }

        MonitorFallbackNotice = usedDefaultFallback
            ? "The last monitor output is unavailable, so Default output will be used for this test."
            : string.Empty;
    }

    private AudioEndpointReference? ResolveSelectedAudioTestMonitorEndpoint()
    {
        if (string.IsNullOrWhiteSpace(SelectedMonitorOutputId)) return null;
        CycleDevice? selected = MonitorOutputDevices.FirstOrDefault(device => device.Id.Equals(SelectedMonitorOutputId, StringComparison.OrdinalIgnoreCase));
        return selected == null ? null : AudioEndpointReference.FromCycleDevice(selected);
    }

    private void QueueAudioTestMonitorConfiguration(int delayMs)
    {
        if (_isDisposed) return;
        CancellationTokenSource nextCts = AppDebouncedBackgroundWorkCoordinator.BeginDebounce(
            ref _audioTestMonitorDebounceCts);
        bool enabled = HearMyself;
        float volume = (float)(MonitorVolume / 100.0);
        AudioEndpointReference? endpoint = ResolveSelectedAudioTestMonitorEndpoint();
        bool queued = _tryRunBackgroundWork(shutdownToken =>
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

    internal void ApplyAudioEndpointTestState(AudioEndpointTestState state)
    {
        if (_isDisposed) return;
        if (state.Revision < _audioTestState.Revision)
        {
            _logger.Debug(
                "AudioTestingViewModel",
                () => $"audio-test-stale-ui-state-ignored | incomingRevision={state.Revision} currentRevision={_audioTestState.Revision} phase={state.Phase}");
            return;
        }

        _audioTestState = state;
        if (state.Kind != AudioEndpointTestKind.Input || state.Phase != AudioEndpointTestPhase.Running)
            AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _audioTestMonitorDebounceCts);
        string[] properties = [nameof(IsOutputTestStatusVisible), nameof(IsOutputTestRunning), nameof(IsInputTestPanelVisible), nameof(IsInputTestRunning), nameof(Status), nameof(EndpointName), nameof(ActionText)];
        foreach (string property in properties) OnPropertyChanged(property);

        if (IsInputTestRunning) _audioTestUiTimer.Start();
        else
        {
            _audioTestUiTimer.Stop();
            LevelPercent = 0;
            PeakPercent = 0;
            Elapsed = "00:00";
        }
    }

    private void OnAudioTestUiTimerTick(object? sender, EventArgs e)
    {
        AudioInputLevelSnapshot level = _audioEndpointTestService.ReadInputLevel();
        LevelPercent = level.LevelPercent;
        PeakPercent = level.PeakPercent;
        if (_audioTestState.StartedAt is { } startedAt)
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            Elapsed = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        }
    }

    internal Task StopAudioEndpointTestAsync(AudioEndpointTestStopReason reason)
    {
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _audioTestMonitorDebounceCts);
        return _audioEndpointTestService.StopAsync(reason);
    }
    internal Task ReconcileAudioEndpointTestDevicesAsync(CancellationToken cancellationToken) => _audioEndpointTestService.ReconcileActiveEndpointsAsync(cancellationToken);

    internal void RequestStopAudioEndpointTest(AudioEndpointTestStopReason reason)
    {
        if (_isDisposed) return;
        if (_audioEndpointTestService.CurrentState.Phase != AudioEndpointTestPhase.Idle)
        {
            _ = StopAudioEndpointTestForLifecycleAsync(reason);
        }
    }

    private async Task StopAudioEndpointTestForLifecycleAsync(AudioEndpointTestStopReason reason)
    {
        try
        {
            await StopAudioEndpointTestAsync(reason);
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "AudioTestingViewModel",
                () => $"audio-test-lifecycle-stop-failed | reason={reason} error={ex.GetType().Name}",
                nameof(StopAudioEndpointTestForLifecycleAsync),
                ex);
        }
    }

    private void HandleAudioTestCommandException(string operation, Exception exception) =>
        _logger.Warning("AudioTestingViewModel", () => $"audio-test-command-failed | operation={operation} error={exception.GetType().Name}", nameof(HandleAudioTestCommandException), exception);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        TestOutputCommand.Dispose();
        TestInputCommand.Dispose();
        StopCommand.Dispose();
        _audioTestUiTimer.Stop();
        _audioTestUiTimer.Tick -= OnAudioTestUiTimerTick;
        _audioEndpointTestService.StateChanged -= OnAudioEndpointTestStateChanged;
        AppDebouncedBackgroundWorkCoordinator.CancelAndDispose(ref _audioTestMonitorDebounceCts);
        await _audioEndpointTestService.DisposeAsync();
    }
}
