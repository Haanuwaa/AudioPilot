using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioPilot;

public partial class OverlayWindow
{
    private CancellationTokenSource? _overlayImageRequest;
    private MixerAppIconProvider? _foregroundAppIcons;
    private Task<ImageSource?>? _pendingForegroundAppIcon;

    internal Task LoadMediaArtworkAsync(Func<CancellationToken, Task<BitmapSource?>> load, Func<bool>? isCurrent) =>
        MediaCover.Visibility == Visibility.Hidden
            ? LoadOverlayImageAsync(load, ApplyMediaArtwork, isCurrent, timeout: TimeSpan.FromSeconds(1))
            : Task.CompletedTask;

    private void ApplyMediaArtwork(BitmapSource? image)
    {
        if (image == null || MediaOverlayPanel.Visibility != Visibility.Visible) return;
        MediaCover.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        MediaCover.Visibility = Visibility.Visible;
    }

    internal Task LoadPendingForegroundAppIconAsync()
    {
        Task<ImageSource?>? pending = _pendingForegroundAppIcon;
        _pendingForegroundAppIcon = null;
        if (pending == null) return Task.CompletedTask;
        if (pending.IsCompletedSuccessfully)
        {
            CancelOverlayImage();
            if (IsVisible) ApplyForegroundAppIcon(pending.Result);
            return Task.CompletedTask;
        }
        return LoadOverlayImageAsync(_ => pending, ApplyForegroundAppIcon);
    }

    private void ApplyForegroundAppIcon(ImageSource? image)
    {
        if (image == null)
        {
            ResetForegroundAppIcon();
            QueueReposition();
            return;
        }

        ForegroundAppIcon.Source = image;
        ForegroundAppIcon.Visibility = Visibility.Visible;
    }

    private async Task LoadOverlayImageAsync<TImage>(Func<CancellationToken, Task<TImage?>> load, Action<TImage?> apply,
        Func<bool>? isCurrent = null, TimeSpan? timeout = null) where TImage : ImageSource
    {
        CancelOverlayImage();
        using var request = new CancellationTokenSource();
        if (timeout is { } limit) request.CancelAfter(limit);
        _overlayImageRequest = request;
        CancellationToken token = request.Token;
        Task<TImage?>? pending = null;
        try
        {
            if (isCurrent?.Invoke() == false) return;
            pending = load(token);
            TImage? image = await pending.WaitAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(_overlayImageRequest, request)
                || !IsVisible || isCurrent?.Invoke() == false) return;
            apply(image);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (pending != null)
            {
                _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            _logger.Trace("OverlayWindow", $"overlay-image-unavailable | error={ex.GetType().Name}", nameof(LoadOverlayImageAsync));
        }
        finally
        {
            if (ReferenceEquals(_overlayImageRequest, request)) _overlayImageRequest = null;
        }
    }

    private void CancelOverlayImage()
    {
        _pendingForegroundAppIcon = null;
        CancellationTokenSource? request = _overlayImageRequest;
        _overlayImageRequest = null;
        request?.Cancel();
    }

    private void ResetOverlayImages(bool reserveMediaArtwork = false)
    {
        CancelOverlayImage();
        ResetForegroundAppIcon();
        MediaCover.Background = null;
        // Hidden artwork reserves its height so revealing the bitmap does not move or resize the overlay.
        MediaCover.Visibility = reserveMediaArtwork ? Visibility.Hidden : Visibility.Collapsed;
        double gutter = reserveMediaArtwork ? MediaCover.Width + MediaCover.Margin.Left + MediaCover.Margin.Right : 0;
        MediaTrackText.Margin = new Thickness(gutter, 0, gutter, 0);
    }

    private void ResetForegroundAppIcon()
    {
        ForegroundAppIcon.Source = null;
        ForegroundAppIcon.Visibility = Visibility.Collapsed;
        StructuredOverlayPanel.Margin = default;
    }
}
