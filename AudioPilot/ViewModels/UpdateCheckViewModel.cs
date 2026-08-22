using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using AudioPilot.Constants;
using AudioPilot.Logging;
using AudioPilot.Services.Updates;

namespace AudioPilot.ViewModels;

/// <summary>Owns opt-in update checks and passive Settings feedback independently of window creation.</summary>
public sealed class UpdateCheckViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Logger _logger;
    private readonly Func<CancellationToken, Task<PublishedRelease?>> _getLatest;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Version _currentVersion;
    private readonly bool _isPreview;
    private CancellationTokenSource? _checkCancellation;
    private Task _checkTask = Task.CompletedTask;
    private PublishedRelease? _latest;
    private long? _lastCheckTimestamp;
    private TimeSpan _nextCheckDelay;
    private bool _started;
    private bool _enabled;
    private bool _disposed;

    internal UpdateCheckViewModel(Dispatcher dispatcher, Logger logger,
        Func<CancellationToken, Task<PublishedRelease?>>? getLatest = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Version? currentVersion = null, bool? isPreview = null, TimeProvider? timeProvider = null)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _getLatest = getLatest ?? new GitHubReleaseService().GetLatestAsync;
        _delay = delay ?? Task.Delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Version assemblyVersion = currentVersion ?? typeof(UpdateCheckViewModel).Assembly.GetName().Version!;
        _currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(0, assemblyVersion.Build));
        string informationalVersion = typeof(UpdateCheckViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        _isPreview = isPreview ?? informationalVersion.Split('+')[0].Contains('-');
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsUpdateAvailable => _enabled && _latest != null
        && (_latest.Version > _currentVersion || (_isPreview && _latest.Version == _currentVersion));
    public string UpdateMessage => IsUpdateAvailable ? $"Update available: {_currentVersion}{(_isPreview ? " (preview)" : string.Empty)} → {_latest!.Version}" : string.Empty;
    public Uri ReleaseUrl => _latest?.Url ?? new Uri(AppConstants.Links.RepositoryUrl + "/releases/latest");

    internal void Start()
    {
        _dispatcher.VerifyAccess();
        if (_started || _disposed) return;
        _started = true;
        if (_enabled) StartChecks(TimeSpan.FromSeconds(30));
    }

    internal void SetEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        _checkCancellation?.Cancel();
        _checkCancellation = null;
        PublishState();
        if (_started && enabled) StartChecks(TimeSpan.Zero);
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
            TimeSpan remaining = _lastCheckTimestamp.HasValue
                ? _nextCheckDelay - _timeProvider.GetElapsedTime(_lastCheckTimestamp.Value)
                : TimeSpan.Zero;
            if (remaining > initialDelay) initialDelay = remaining;
            await _delay(initialDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                TimeSpan nextDelay = TimeSpan.FromDays(1);
                try
                {
                    PublishedRelease? release = await _getLatest(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed || !_enabled || token.IsCancellationRequested) return;
                        _latest = release;
                        PublishState();
                        _logger.Info("UpdateCheck", () => $"update-check-completed | current={_currentVersion} latest={release?.Version.ToString() ?? "none"} updateAvailable={IsUpdateAvailable}");
                    }, DispatcherPriority.Background, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    nextDelay = TimeSpan.FromHours(1);
                    string detail = ex is HttpRequestException http ? $" status={(int?)http.StatusCode}" : string.Empty;
                    _logger.Debug("UpdateCheck", () => $"update-check-unavailable | error={ex.GetType().Name}{detail} retryMinutes=60");
                }

                _lastCheckTimestamp = _timeProvider.GetTimestamp();
                _nextCheckDelay = nextDelay;
                await _delay(nextDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void PublishState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdateAvailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReleaseUrl)));
    }

    public async ValueTask DisposeAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _checkCancellation?.Cancel();
        _checkCancellation = null;
        PublishState();
        await _checkTask.ConfigureAwait(false);
    }
}
