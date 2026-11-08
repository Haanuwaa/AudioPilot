using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Input;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Helpers;
using AudioPilot.Logging;
using AudioPilot.Services.Updates;

namespace AudioPilot.ViewModels;

/// <summary>Owns opt-in update checks and feedback independently of window creation.</summary>
public sealed class UpdateCheckViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Logger _logger;
    private readonly Func<CancellationToken, Task<PublishedRelease?>> _getLatest;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Version, CancellationToken, Task<bool>>? _notifyUpdate;
    private readonly Version _currentVersion;
    private readonly bool _isPreview;
    private readonly bool _persistFeedback;
    private CancellationTokenSource? _checkCancellation;
    private Task _checkTask = Task.CompletedTask;
    private PublishedRelease? _latest;
    private sealed record CheckSchedule(long Timestamp, TimeSpan Interval, TimeSpan ServerCooldown);
    private CheckSchedule? _schedule;
    private readonly Channel<bool> _manualRequests = Channel.CreateBounded<bool>(1);
    private readonly RelayCommand _checkNowCommand;
    private static readonly TimeSpan[] NotificationRetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];
    private string _feedback = "Not checked yet.";
    private bool _isChecking;
    private bool _manualPending;
    private volatile bool _manualInFlight;
    private TaskCompletionSource<UpdateCheckSnapshot>? _manualCompletion;
    private DateTimeOffset? _lastCheckedUtc;
    private string? _lastError;
    private bool _started;
    private bool _enabled;
    private bool _disposed;

    internal UpdateCheckViewModel(Dispatcher dispatcher, Logger logger,
        Func<CancellationToken, Task<PublishedRelease?>>? getLatest = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Version? currentVersion = null, bool? isPreview = null, TimeProvider? timeProvider = null,
        Func<Version, CancellationToken, Task<bool>>? notifyUpdate = null)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persistFeedback = getLatest == null;
        _getLatest = getLatest ?? new GitHubReleaseService(timeProvider: _timeProvider).GetLatestAsync;
        _notifyUpdate = notifyUpdate;
        _checkNowCommand = new RelayCommand(RequestCheck, () => CanCheckNow);
        Version assemblyVersion = currentVersion ?? typeof(UpdateCheckViewModel).Assembly.GetName().Version!;
        _currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(0, assemblyVersion.Build));
        string informationalVersion = typeof(UpdateCheckViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        _isPreview = isPreview ?? informationalVersion.Split('+')[0].Contains('-');
        if (_persistFeedback && UpdateCheckSnapshotStore.Read() is { } saved)
        {
            if (Version.TryParse(saved.LatestVersion, out var latest))
                _latest = new(latest, new Uri(AppConstants.Links.RepositoryUrl + "/releases/tag/v" + latest));
            _lastCheckedUtc = saved.LastCheckedUtc;
            _lastError = saved.ErrorCode;
            var now = _timeProvider.GetUtcNow();
            TimeSpan server = saved.RetryAfterUtc > now ? saved.RetryAfterUtc.Value - now : TimeSpan.Zero;
            TimeSpan daily = saved.LastCheckedUtc is { } checkedAt && checkedAt <= now
                ? TimeSpan.FromDays(1) - (now - checkedAt) : TimeSpan.Zero;
            _schedule = new(_timeProvider.GetTimestamp(), server > daily ? server : daily > TimeSpan.Zero ? daily : TimeSpan.Zero, server);
            _feedback = server > TimeSpan.Zero ? $"Couldn't check. Retry after {saved.RetryAfterUtc!.Value.ToLocalTime():g}."
                : _lastCheckedUtc is { } last ? $"Last checked {last.ToLocalTime():g}: {(IsUpdateAvailable ? "update available." : "no newer release recorded.")}"
                : "Not checked yet.";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsUpdateAvailable => _latest != null && IsNewer(_latest.Version);
    public string UpdateMessage => IsUpdateAvailable ? $"Update available: {_currentVersion}{(_isPreview ? " (preview)" : string.Empty)} → {_latest!.Version}" : string.Empty;
    public string ReleaseLinkText => IsUpdateAvailable ? UpdateMessage : "View release downloads";
    public Uri ReleaseUrl => _latest?.Url ?? new Uri(AppConstants.Links.RepositoryUrl + "/releases/latest");
    public bool AreChecksEnabled => _enabled;
    public bool CanCheckNow => _started && !_disposed && !_isChecking && !_manualPending && ServerCooldownRemaining <= TimeSpan.Zero;
    public ICommand CheckNowCommand => _checkNowCommand;
    public string CheckStatus => _isChecking || _manualPending ? "Checking for updates..." : _feedback;
    public string CheckNowHelp => ServerCooldownRemaining > TimeSpan.Zero ? _feedback
        : "Check for a newer stable release now. This only checks release information; it never downloads or installs a package.";

    internal UpdateCheckSnapshot Snapshot => new(_currentVersion.ToString(), _latest?.Version.ToString(), ReleaseUrl.AbsoluteUri,
        IsUpdateAvailable, _enabled, _lastCheckedUtc, _lastError,
        ServerCooldownRemaining > TimeSpan.Zero ? _timeProvider.GetUtcNow() + TimeSpan.FromTicks(Math.Min(ServerCooldownRemaining.Ticks, (DateTimeOffset.MaxValue - _timeProvider.GetUtcNow()).Ticks)) : null);

    internal Task<UpdateCheckSnapshot> CheckNowAsync(CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_isChecking || _manualPending)
        {
            _manualInFlight = true;
            return (_manualCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token);
        }
        if (!CanCheckNow) return Task.FromResult(Snapshot);
        RequestCheck();
        return _manualCompletion!.Task.WaitAsync(token);
    }

    private TimeSpan ServerCooldownRemaining => _schedule is { } schedule
        ? schedule.ServerCooldown - _timeProvider.GetElapsedTime(schedule.Timestamp) : TimeSpan.Zero;

    private void RequestCheck()
    {
        _dispatcher.VerifyAccess();
        if (!CanCheckNow) return;
        _manualCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _manualPending = true;
        _manualRequests.Writer.TryWrite(true);
        if (!_enabled) StartChecks(TimeSpan.Zero);
        PublishState();
    }

    internal void Start()
    {
        _dispatcher.VerifyAccess();
        if (_started || _disposed) return;
        _started = true;
        if (_enabled) StartChecks(TimeSpan.FromSeconds(30));
        PublishState();
    }

    internal void SetEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        if (!_manualPending && !_manualInFlight)
        {
            _checkCancellation?.Cancel();
            _checkCancellation = null;
            _isChecking = false;
            if (_started && enabled) StartChecks(TimeSpan.Zero);
        }
        PublishState();
    }

    private void StartChecks(TimeSpan initialDelay)
    {
        var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        Task previous = _checkTask;
        _checkTask = Task.Run(() => RunChecksAsync(previous, initialDelay, cancellation));
    }

    private async Task RunChecksAsync(Task previous, TimeSpan initialDelay, CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await previous.ConfigureAwait(false);
            previous = Task.CompletedTask;
            if (_manualPending) _manualRequests.Writer.TryWrite(true);
            TimeSpan remaining = _schedule is { } schedule
                ? schedule.Interval - _timeProvider.GetElapsedTime(schedule.Timestamp) : TimeSpan.Zero;
            if (remaining > initialDelay) initialDelay = remaining;
            TimeSpan untilCheck = initialDelay;
            TimeSpan? untilNotification = null;
            PublishedRelease? notificationRelease = null;
            int notificationAttempts = 0;
            while (!token.IsCancellationRequested)
            {
                TimeSpan wait = untilNotification.HasValue && untilNotification.Value < untilCheck
                    ? untilNotification.Value : untilCheck;
                // Long server cooldowns are retained, with each individual timer bounded.
                wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait;
                long started = _timeProvider.GetTimestamp();
                bool requested = await WaitForWorkAsync(wait, token).ConfigureAwait(false);
                TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
                if (!requested && elapsed < wait) elapsed = wait;
                untilCheck -= elapsed;
                untilNotification -= elapsed;
                bool notify = false;
                if (requested || untilCheck <= TimeSpan.Zero)
                {
                    if (ServerCooldownRemaining > TimeSpan.Zero)
                    {
                        untilCheck = ServerCooldownRemaining;
                        await _dispatcher.InvokeAsync(() =>
                        {
                            _manualPending = false;
                            PublishState();
                            _manualCompletion?.TrySetResult(Snapshot);
                            _manualCompletion = null;
                        }, DispatcherPriority.Background, token);
                        if (!_enabled) return;
                    }
                    else
                    {
                        _manualInFlight = requested && _manualPending;
                        (bool succeeded, PublishedRelease? release) = await CheckAsync(token).ConfigureAwait(false);
                        _manualInFlight = false;
                        if (!_enabled) return;
                        untilCheck = _schedule!.Interval;
                        if (succeeded)
                        {
                            notificationAttempts = 0;
                            notificationRelease = release;
                            untilNotification = null;
                            notify = release != null && IsNewer(release.Version);
                        }
                    }
                }
                else if (untilNotification <= TimeSpan.Zero)
                {
                    notify = true;
                }
                if (notify && _enabled && _notifyUpdate != null)
                {
                    bool delivered;
                    try { delivered = await _notifyUpdate(notificationRelease!.Version, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        delivered = false;
                        _logger.Debug("UpdateCheck", () => $"update-notification-unavailable | error={ex.GetType().Name}");
                    }
                    notificationAttempts++;
                    untilNotification = !delivered && notificationAttempts <= NotificationRetryDelays.Length
                        ? NotificationRetryDelays[notificationAttempts - 1] : null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _manualInFlight = false;
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                await _dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_checkCancellation, cancellation)) _checkCancellation = null;
                });
            cancellation.Dispose();
        }
    }

    private bool IsNewer(Version version) => version > _currentVersion || (_isPreview && version == _currentVersion);

    private async Task<(bool Succeeded, PublishedRelease? Release)> CheckAsync(CancellationToken token)
    {
        await _dispatcher.InvokeAsync(() => { _manualPending = false; _isChecking = true; PublishState(); }, DispatcherPriority.Background, token);
        try
        {
            PublishedRelease? release = await _getLatest(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _schedule = new(_timeProvider.GetTimestamp(), TimeSpan.FromDays(1), TimeSpan.Zero);
            await _dispatcher.InvokeAsync(() =>
            {
                _latest = release;
                _lastCheckedUtc = _timeProvider.GetUtcNow();
                _lastError = null;
                _feedback = $"Last checked {_timeProvider.GetLocalNow():t}: {(IsUpdateAvailable ? "update available." : release != null ? "up to date." : "no published release.")}";
                _logger.Info("UpdateCheck", () => $"update-check-completed | current={_currentVersion} latest={release?.Version.ToString() ?? "none"} updateAvailable={IsUpdateAvailable}");
            }, DispatcherPriority.Background, token);
            return (true, release);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            TimeSpan serverDelay = ex is ReleaseCheckException { RetryAfter: { } delay } ? delay : TimeSpan.Zero;
            TimeSpan retry = serverDelay > TimeSpan.Zero ? serverDelay : TimeSpan.FromHours(1);
            _schedule = new(_timeProvider.GetTimestamp(), retry, serverDelay);
            await _dispatcher.InvokeAsync(() =>
            {
                _lastError = "update-check-failed";
                _feedback = $"Couldn't check ({_timeProvider.GetLocalNow():t}).";
                if (serverDelay > TimeSpan.Zero)
                {
                    DateTimeOffset now = _timeProvider.GetLocalNow();
                    DateTimeOffset after = serverDelay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + serverDelay;
                    _feedback += $" Retry after {after:g}.";
                }
            }, DispatcherPriority.Background, token);
            string detail = ex is HttpRequestException http ? $" status={(int?)http.StatusCode}" : string.Empty;
            _logger.Debug("UpdateCheck", () => $"update-check-unavailable | error={ex.GetType().Name}{detail} retryMinutes={retry.TotalMinutes}");
            return (false, null);
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                var snapshot = await _dispatcher.InvokeAsync(() =>
                {
                    _isChecking = false;
                    PublishState();
                    _manualCompletion?.TrySetResult(Snapshot);
                    _manualCompletion = null;
                    return Snapshot;
                }, DispatcherPriority.Background, CancellationToken.None);
                if (_persistFeedback) UpdateCheckSnapshotStore.Save(snapshot, _logger);
            }
        }
    }

    private async Task<bool> WaitForWorkAsync(TimeSpan duration, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task timer = _delay(duration, wait.Token);
        Task<bool> request = _manualRequests.Reader.ReadAsync(wait.Token).AsTask();
        try { await Task.WhenAny(timer, request).ConfigureAwait(false); }
        finally
        {
            wait.Cancel();
            try { await Task.WhenAll(timer, request).ConfigureAwait(false); }
            catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        return request.IsCompletedSuccessfully;
    }

    private void PublishState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdateAvailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReleaseUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReleaseLinkText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AreChecksEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCheckNow)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckNowHelp)));
        _checkNowCommand.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _dispatcher.VerifyAccess();
        if (!_disposed)
        {
            _disposed = true;
            _enabled = false;
            _isChecking = false;
            _manualPending = false;
            _checkCancellation?.Cancel();
            _checkCancellation = null;
            PublishState();
            _manualCompletion?.TrySetCanceled();
            _manualCompletion = null;
            _checkNowCommand.Dispose();
        }
        await _checkTask.ConfigureAwait(false);
    }
}
