using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Localization;
using AudioPilot.UpdateProtocol;

namespace AudioPilot;

/// <summary>Presents preparation before any helper I/O and owns cancellation until the verified window takes over.</summary>
public partial class UpdateLaunchWindow : Window
{
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private readonly DialogWorkAreaLifetime _workArea;
    private bool _closed;
    internal bool IsClosed => _closed;

    internal UpdateLaunchWindow(Window? owner, LocalizationService localization, CancellationToken token)
    {
        Owner = owner;
        LocalizationContext.SetService(this, localization);
        InitializeComponent();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _stop.Token;
        _workArea = new(this);
        LocalizationContext.SetText(StatusHeading, TextBlock.TextProperty, new("Update_PreparingUpdater"));
        LocalizationContext.SetText(StatusMessage, TextBlock.TextProperty, new("Update_PreparingUpdaterMessage"));
        Closed += OnClosed;
        PreviewKeyDown += OnKey;
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        DialogWindowHelper.ApplyOwnerOrMainWindowTheme(this);
    }

    internal UpdaterWindowPlacement? CapturePlacement() => DialogWindowHelper.ReadScreenBounds(this) is { } bounds
        ? new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized) : null;

    internal async Task PrepareAsync(Func<CancellationToken, Task> prepare)
    {
        try
        {
            WindowFirstPresentationHelper.Prepare(this, hideFromTaskbar: false);
            WindowFirstPresentationHelper.StageOffscreenFirstRender(this);
            WindowFirstPresentationHelper.BeginOffscreenFirstRender(this);
            _ = WindowFirstPresentationHelper.TryApplyNativeClientBackground(this, ensureHandle: true);
            Show();
            if (!await WindowFirstPresentationHelper.RevealAsync(this, activate: true,
                canReveal: () => !_closed && IsVisible, waitForFirstRender: true, cancellationToken: _token,
                positionBeforeReveal: () => DialogWindowHelper.TryCenterBeforeReveal(this, Owner)))
            {
                if (!_closed) Close();
                return;
            }
            await prepare(_token);
            if (!_closed) Close();
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { if (!_closed) Close(); }
    }

    internal void ShowFailure()
    {
        if (_closed) return;
        PreparationProgress.Visibility = Visibility.Collapsed;
        ManualDownloadLink.Visibility = Visibility.Visible;
        LocalizationContext.SetText(StatusHeading, TextBlock.TextProperty, new("Update_TheUpdaterCouldNotOpen"));
        LocalizationContext.SetText(StatusMessage, TextBlock.TextProperty, new("Update_PreparingUpdaterFailed"));
    }

    private void Close_Click(object sender, RoutedEventArgs args) => Close();
    private void ManualDownload_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Constants.AppConstants.Links.RepositoryUrl + "/releases/latest")
            { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Logging.Logger.Instance.Warning("Updater", "The release download page could not open.", nameof(ManualDownload_Click), error);
        }
    }
    private void OnKey(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        Close();
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        try { _stop.Cancel(); }
        finally
        {
            _workArea.Dispose();
            _stop.Dispose();
            Closed -= OnClosed;
            PreviewKeyDown -= OnKey;
        }
    }
}
