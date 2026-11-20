using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AudioPilot;

public partial class PackagedAppPickerWindow
{
    private const int MaxCachedIcons = 64;
    private static readonly SemaphoreSlim IconReaders = new(2);
    private readonly Dictionary<Image, CancellationTokenSource> _iconRequests = [];
    private readonly Dictionary<Func<ImageSource?>, ImageSource?> _iconCache = [];

    private async void AppIcon_Loaded(object sender, RoutedEventArgs e) => await UpdateAppIconAsync((Image)sender);

    private async void AppIcon_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Image { IsLoaded: true } image) await UpdateAppIconAsync(image);
    }

    private void AppIcon_Unloaded(object sender, RoutedEventArgs e) => ClearAppIcon((Image)sender);

    private void ClearAppIcon(Image image)
    {
        if (_iconRequests.Remove(image, out var request)) request.Cancel();
        // Recycled containers need to display the fallback until their own icon arrives.
        image.Source = null;
    }

    private void ResetAppIcons()
    {
        var requests = _iconRequests.Values.ToArray();
        _iconRequests.Clear();
        foreach (var request in requests) request.Cancel();
        _iconCache.Clear();
    }

    private async Task UpdateAppIconAsync(Image image)
    {
        ClearAppIcon(image);
        if (_isClosed || image.DataContext is not AudioDeviceHelper.PackagedAppIdentity app) return;
        if (app.IconSource != null)
        {
            image.Source = app.IconSource;
            return;
        }
        if (app.LoadIcon is not { } load) return;
        if (_iconCache.TryGetValue(load, out var cached))
        {
            image.Source = cached;
            return;
        }

        using var request = new CancellationTokenSource();
        _iconRequests.Add(image, request);
        CancellationToken token = request.Token;
        Task<ImageSource?> pending = ReadAppIconAsync(load, token);
        try
        {
            var icon = await pending.WaitAsync(token);
            if (token.IsCancellationRequested) return;
            if (_iconCache.Count == MaxCachedIcons && !_iconCache.ContainsKey(load))
                _iconCache.Remove(_iconCache.Keys.First());
            _iconCache[load] = icon;
            image.Source = icon;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = pending.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception ex) { _logger.Trace("PackagedAppPickerWindow", () => $"packaged-app-icon-unavailable | error={ex.GetType().Name}"); }
        finally
        {
            if (ReferenceEquals(_iconRequests.GetValueOrDefault(image), request)) _iconRequests.Remove(image);
        }
    }

    private static async Task<ImageSource?> ReadAppIconAsync(Func<ImageSource?> load, CancellationToken token)
    {
        await IconReaders.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(load, token).ConfigureAwait(false); }
        finally { IconReaders.Release(); }
    }
}
