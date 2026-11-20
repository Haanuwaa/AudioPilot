using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioPilot.Controls;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;
using Windows.Storage.Streams;
using Foundation = Windows.Foundation;

namespace AudioPilot.Tests;

[Collection("WpfApplicationIsolation")]
public sealed class OverlayWindowArtworkTests
{
    [Fact]
    public async Task ClosingDetachesTheOverlayWhileThumbnailCancellationIsStillRunning()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        var thumbnail = new BlockingThumbnail();
        var started = new TaskCompletionSource<(Task<BitmapSource?> Worker, int DispatcherThread)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task close = SharedStaDispatcherHost.RunAsync(async () =>
        {
            var window = new OverlayWindow("Media");
            try
            {
                TestWindowFactory.ShowWindowForLayoutTest(window);
                window.UpdateContent("Now playing", "Track A", null, reserveArtworkSpace: true);
                int dispatcherThread = Environment.CurrentManagedThreadId;
                Task presentation = window.LoadMediaArtworkAsync(token =>
                {
                    Task<BitmapSource?> worker = MediaOverlayArtwork.LoadAsync(thumbnail, token);
                    started.SetResult((worker, dispatcherThread));
                    return worker;
                }, isCurrent: null);
                await thumbnail.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
                window.Close();
                await presentation;
                Assert.Null(Assert.IsType<Border>(window.FindName("MediaCover")).Background);
            }
            finally { window.Close(); }
        });

        Task<BitmapSource?> worker;
        try
        {
            int dispatcherThread;
            (worker, dispatcherThread) = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            int cancelThread = await thumbnail.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            Assert.NotEqual(dispatcherThread, cancelThread);
            await close.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            Assert.False(worker.IsCompleted);
        }
        finally
        {
            thumbnail.Release.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(5), testToken);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(5), testToken));
    }

    [Fact]
    public Task LateCoverLeavesTheFirstTextLayoutInPlace() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        const string track = "Live from the Royal Albert Hall";
        const string performer = "The London Session Orchestra";
        var window = new OverlayWindow("Media") { Width = 360 };
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            window.UpdateContent("Now playing", track, performer);
            var cover = Assert.IsType<Border>(window.FindName("MediaCover"));
            var title = Assert.IsType<FrameworkElement>(window.FindName("MediaTitleText"), exactMatch: false);
            var artist = Assert.IsType<FrameworkElement>(window.FindName("MediaArtistText"), exactMatch: false);
            Size fullTitleSize = title.RenderSize;
            double center = title.TranslatePoint(new Point(title.ActualWidth / 2, 0), window).X;

            window.UpdateContent("Now playing", track, performer, reserveArtworkSpace: true);
            var read = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task loading = window.LoadMediaArtworkAsync(_ => read.Task, isCurrent: null);
            Assert.Equal(track, window.GetMediaOverlayTextStateForTests().Title);
            Assert.Equal(performer, window.GetMediaOverlayTextStateForTests().Artist);
            Assert.Equal(Visibility.Hidden, cover.Visibility);
            Assert.Null(cover.Background);
            Assert.False(loading.IsCompleted);
            Assert.True(title.ActualWidth < fullTitleSize.Width);
            Assert.True(title.ActualHeight > fullTitleSize.Height);
            Assert.Equal(center, title.TranslatePoint(new Point(title.ActualWidth / 2, 0), window).X, precision: 1);
            Size pendingSize = window.RenderSize;
            Size pendingTitleSize = title.RenderSize;
            Point pendingTitlePosition = title.TranslatePoint(new Point(), window);
            Point pendingArtistPosition = artist.TranslatePoint(new Point(), window);

            read.SetResult(CreateImage(2, 2));
            await loading;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, cover.Visibility);
            Assert.Equal(pendingSize, window.RenderSize);
            Assert.Equal(pendingTitleSize, title.RenderSize);
            Assert.Equal(pendingTitlePosition, title.TranslatePoint(new Point(), window));
            Assert.Equal(pendingArtistPosition, artist.TranslatePoint(new Point(), window));

            window.UpdateContent("Now playing", track, performer);
            Assert.Equal(Visibility.Collapsed, cover.Visibility);
            Assert.Equal(fullTitleSize, title.RenderSize);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RapidTrackReplacementShowsTextImmediatelyAndDiscardsLateArtwork() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var window = new OverlayWindow("Media") { Width = 360 };
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            window.UpdateContent("Now playing", "Track A", "Artist");
            var cover = Assert.IsType<Border>(window.FindName("MediaCover"));
            var title = Assert.IsType<FrameworkElement>(window.FindName("MediaTitleText"), exactMatch: false);
            var artist = Assert.IsType<FrameworkElement>(window.FindName("MediaArtistText"), exactMatch: false);
            var heading = Assert.IsType<DockPanel>(window.FindName("MediaHeadingPanel"));
            double fullTextWidth = title.ActualWidth;
            double originalTextLeft = title.TranslatePoint(new Point(), window).X;
            double center = originalTextLeft + fullTextWidth / 2;
            Assert.InRange(RenderedTextCenter(title, window) - center, -2, 2);
            Assert.InRange(RenderedTextCenter(artist, window) - center, -2, 2);

            window.UpdateContent("Now playing", "Track A", "Artist", reserveArtworkSpace: true);
            var firstRead = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken firstToken = default;
            Task first = window.LoadMediaArtworkAsync(token => { firstToken = token; return firstRead.Task; }, isCurrent: null);
            Assert.Equal("Track A", window.GetMediaOverlayTextStateForTests().Title);
            Assert.Equal(Visibility.Hidden, cover.Visibility);
            Assert.Null(cover.Background);

            window.UpdateContent("Now playing", "Track B", "Artist", reserveArtworkSpace: true);
            Assert.True(firstToken.IsCancellationRequested);
            Assert.Equal("Track B", window.GetMediaOverlayTextStateForTests().Title);
            Assert.Equal(Visibility.Hidden, cover.Visibility);
            BitmapSource current = CreateImage(2, 2);
            await window.LoadMediaArtworkAsync(_ => Task.FromResult<BitmapSource?>(current), isCurrent: null);
            firstRead.SetResult(CreateImage(1, 1));
            await first;

            Assert.Same(current, Assert.IsType<ImageBrush>(cover.Background).ImageSource);
            Assert.Equal(Visibility.Visible, cover.Visibility);
            window.UpdateLayout();
            Assert.Equal(48, cover.ActualWidth);
            Assert.Equal(48, cover.ActualHeight);
            double coverLeft = cover.TranslatePoint(new Point(), window).X;
            double textLeft = title.TranslatePoint(new Point(), window).X;
            Assert.Equal(originalTextLeft, coverLeft, precision: 1);
            Assert.Equal(10, textLeft - coverLeft - cover.ActualWidth, precision: 1);
            Assert.Equal(center, heading.TranslatePoint(new Point(heading.ActualWidth / 2, 0), window).X, precision: 1);
            Assert.InRange(RenderedTextCenter(title, window) - center, -2, 2);
            Assert.InRange(RenderedTextCenter(artist, window) - center, -2, 2);

            window.UpdateContent("Now playing", "Track C", "Artist");
            Assert.Equal(Visibility.Collapsed, cover.Visibility);
            Assert.Null(cover.Background);
            Assert.Equal(fullTextWidth, title.ActualWidth, precision: 0);
            Assert.InRange(RenderedTextCenter(title, window) - center, -2, 2);
            Assert.InRange(RenderedTextCenter(artist, window) - center, -2, 2);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("new request")]
    [InlineData("non-media message")]
    [InlineData("fade out")]
    [InlineData("hide")]
    public Task PendingArtworkCannotAppearAfterItsPresentationEnds(string interruption) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var window = new OverlayWindow("Media");
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            window.UpdateContent("Now playing", "Track A", null, reserveArtworkSpace: true);
            var read = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool isCurrent = true;
            Task loading = window.LoadMediaArtworkAsync(_ => read.Task, () => isCurrent);
            switch (interruption)
            {
                case "new request": isCurrent = false; break;
                case "non-media message": window.UpdateContent("Output muted"); break;
                case "fade out": window.BeginFadeOutAndCloseForTests(); break;
                case "hide": window.Hide(); break;
            }
            read.SetResult(CreateImage(1, 1));
            await loading;
            var cover = Assert.IsType<Border>(window.FindName("MediaCover"));
            Assert.NotEqual(Visibility.Visible, cover.Visibility);
            Assert.Null(cover.Background);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RapidForegroundAppSwitchKeepsTextCenteredAndDiscardsThePreviousProcessIcon() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        const uint pid = 42;
        var oldest = new ForegroundProcessTarget((int)pid, 100, "First app");
        var current = new ForegroundProcessTarget((int)pid, 200, "Second app");
        var withoutIcon = new ForegroundProcessTarget((int)pid, 300, "App without icon");
        var firstImage = CreateImage(16, 16);
        var currentImage = CreateImage(32, 32);
        var firstStarted = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        CancellationToken token = TestContext.Current.CancellationToken;
        using var icons = new MixerAppIconProvider((processId, started) =>
        {
            if (started == oldest.StartedUtcTicks)
            {
                firstStarted.SetResult(processId);
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
                return firstImage;
            }
            return started == current.StartedUtcTicks ? currentImage : null;
        });
        var window = new OverlayWindow("Foreground app", icons) { Width = 360 };
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            var icon = Assert.IsType<Image>(window.FindName("ForegroundAppIcon"));
            var heading = Assert.IsType<DockPanel>(window.FindName("StructuredHeadingPanel"));
            var appName = Assert.IsType<TextBlock>(window.FindName("StructuredRow1ValueText"));
            var glyph = Assert.IsType<System.Windows.Shapes.Path>(window.FindName("StructuredGlyph"));

            window.UpdateForegroundAppContent("App volume: 55%", oldest.Name, OverlayActionStateKind.Enabled, oldest);
            var firstNative = icons.GetProcessIconAsync(pid, oldest.StartedUtcTicks)!;
            Assert.Equal(pid, await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Same(currentImage, await icons.GetProcessIconAsync(pid, current.StartedUtcTicks)!.WaitAsync(TimeSpan.FromSeconds(5), token));
            Task firstPresentation = window.LoadPendingForegroundAppIconAsync();
            Assert.Equal(oldest.Name, window.GetStructuredOverlayTextStateForTests().Rows[0].Value);
            Assert.False(firstNative.IsCompleted);
            Assert.Null(icon.Source);

            window.UpdateForegroundAppContent("App muted", current.Name, null, current);
            window.SetIcon(OverlayIcon.SpeakerMuted);
            Assert.Equal(current.Name, window.GetStructuredOverlayTextStateForTests().Rows[0].Value);
            Assert.Null(icon.Source);
            Assert.Same(AppMenuGlyphs.OutputMuted, glyph.Data);
            double center = window.ActualWidth / 2;
            Assert.Equal(center, heading.TranslatePoint(new Point(heading.ActualWidth / 2, 0), window).X, precision: 1);
            Assert.InRange(RenderedTextCenter(appName, window) - center, -2, 2);
            Size beforeIcon = window.RenderSize;

            await window.LoadPendingForegroundAppIconAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Same(currentImage, icon.Source);
            window.UpdateLayout();
            Assert.Equal(32, icon.ActualWidth);
            Assert.Equal(32, icon.ActualHeight);
            Assert.Equal(10, appName.TranslatePoint(new Point(), window).X - icon.TranslatePoint(new Point(icon.ActualWidth, 0), window).X, precision: 1);
            Assert.Equal(beforeIcon, window.RenderSize);
            Assert.InRange(RenderedTextCenter(appName, window) - center, -2, 2);

            releaseFirst.Set();
            Assert.Same(firstImage, await firstNative.WaitAsync(TimeSpan.FromSeconds(5), token));
            await firstPresentation.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Same(currentImage, icon.Source);

            await icons.GetProcessIconAsync(pid, withoutIcon.StartedUtcTicks)!.WaitAsync(TimeSpan.FromSeconds(5), token);
            window.UpdateForegroundAppContent("App volume: 50%", withoutIcon.Name, OverlayActionStateKind.Disabled, withoutIcon);
            await window.LoadPendingForegroundAppIconAsync();
            Assert.Equal(withoutIcon.Name, window.GetStructuredOverlayTextStateForTests().Rows[0].Value);
            Assert.Equal(Visibility.Collapsed, icon.Visibility);
            Assert.Null(icon.Source);
            Assert.Same(AppMenuGlyphs.Output, glyph.Data);
        }
        finally
        {
            releaseFirst.Set();
            window.Close();
            await icons.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
    });

    [Fact]
    public Task ForegroundAppNameBoundsRichTextAndKeepsGraphemesWhole() => SharedStaDispatcherHost.RunAsync(() =>
    {
        string prefix = string.Concat(Enumerable.Repeat("🎵 A ", 50)) + "abcd";
        string name = prefix + "👩🏽‍💻" + string.Concat(Enumerable.Repeat("🎵 A ", 350));
        var window = new OverlayWindow("Foreground app") { Width = 360 };
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            window.UpdateForegroundAppContent("App volume: 55%", name, OverlayActionStateKind.Enabled, default);
            var appName = Assert.IsType<TextBlock>(window.FindName("StructuredRow1ValueText"));
            Assert.Equal(" A abcd…", Assert.IsType<Run>(appName.Inlines.LastInline).Text);
            Assert.Equal(100, appName.Inlines.Count);
            Assert.InRange(appName.ActualHeight, 20, 60);
            Assert.Equal(TextAlignment.Center, appName.TextAlignment);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ForegroundIconLookupReclaimsMissingSpaceOrShowsALateIconAtTheSameAnchor(bool hasIcon) => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var target = new ForegroundProcessTarget(42, 100, "Audio Workstation Session Manager");
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BitmapSource image = CreateImage(32, 32);
        CancellationToken token = TestContext.Current.CancellationToken;
        using var icons = new MixerAppIconProvider((_, _) =>
        {
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), token)) throw new TimeoutException();
            return hasIcon ? image : null;
        });
        var window = new OverlayWindow("Foreground app", icons) { Width = 360 };
        try
        {
            TestWindowFactory.ShowWindowForLayoutTest(window);
            window.ApplyDisplayOptions(OverlayPosition.BottomRight, 10, 0);
            window.UpdateForegroundAppContent("App muted", target.Name, null, target);
            window.SetIcon(OverlayIcon.SpeakerMuted);
            Task loading = window.LoadPendingForegroundAppIconAsync();
            window.ShowOverlay();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, token);
            var appName = Assert.IsType<TextBlock>(window.FindName("StructuredRow1ValueText"));
            var icon = Assert.IsType<Image>(window.FindName("ForegroundAppIcon"));
            var glyph = Assert.IsType<System.Windows.Shapes.Path>(window.FindName("StructuredGlyph"));
            Assert.Equal(Visibility.Hidden, icon.Visibility);
            Size pendingSize = appName.RenderSize;
            double pendingHeight = window.ActualHeight;
            int pendingStackAdvance = window.StackAdvancePixels;
            Assert.True(pendingStackAdvance > 1, "The overlay did not position its native window.");
            Point anchor = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));

            if (hasIcon)
            {
                await TestExecutionGuards.AssertDoesNotCompleteWithinAsync(loading, TimeSpan.FromMilliseconds(1100),
                    "The visible overlay stopped waiting for a slow icon.", token);
            }
            release.Set();
            await loading.WaitAsync(TimeSpan.FromSeconds(5), token);
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, token);
            Point updatedAnchor = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            Assert.InRange(Math.Abs(updatedAnchor.X - anchor.X), 0, 1);
            Assert.True(Math.Abs(updatedAnchor.Y - anchor.Y) <= 1,
                $"Bottom edge moved from {anchor.Y} to {updatedAnchor.Y}, height {pendingHeight} to {window.ActualHeight}, stack advance {pendingStackAdvance} to {window.StackAdvancePixels}.");

            if (hasIcon)
            {
                Assert.Same(image, icon.Source);
                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal(pendingSize, appName.RenderSize);
                window.Hide();
                Assert.Null(icon.Source);
            }
            else
            {
                Assert.Null(icon.Source);
                Assert.Equal(Visibility.Collapsed, icon.Visibility);
                Assert.Same(AppMenuGlyphs.OutputMuted, glyph.Data);
                Assert.Equal(84, appName.ActualWidth - pendingSize.Width, precision: 1);
                Assert.True(appName.ActualHeight < pendingSize.Height);
            }
        }
        finally
        {
            release.Set();
            window.Close();
            await icons.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
    });

    [Fact]
    public Task LoaderReadsAndDownscalesPortraitArtFromAWindowsThumbnailStream() => SharedStaDispatcherHost.RunAsync(async () =>
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateImage(256, 512)));
        using var png = new MemoryStream();
        encoder.Save(png);
        CancellationToken token = TestContext.Current.CancellationToken;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png.ToArray());
            await writer.StoreAsync().AsTask(token);
            _ = writer.DetachStream();
        }
        stream.Seek(0);
        BitmapSource? artwork = await MediaOverlayArtwork.LoadAsync(RandomAccessStreamReference.CreateFromStream(stream), token);
        Assert.NotNull(artwork);
        Assert.True(artwork.IsFrozen);
        Assert.Equal(96, artwork.PixelWidth);
        Assert.Equal(192, artwork.PixelHeight);
        byte[] pixel = new byte[4];
        artwork.CopyPixels(new Int32Rect(48, 96, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixel);
    });

    private static double RenderedTextCenter(FrameworkElement element, Window window)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var drawing = Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(element));
        Assert.False(drawing.Bounds.IsEmpty);
        return element.TranslatePoint(new Point(drawing.Bounds.Left + drawing.Bounds.Width / 2, 0), window).X;
    }

    private static BitmapSource CreateImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private sealed class BlockingThumbnail : IRandomAccessStreamReference, Foundation.IAsyncOperation<IRandomAccessStreamWithContentType>
    {
        private Foundation.AsyncOperationCompletedHandler<IRandomAccessStreamWithContentType>? _completed;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Id => 1;
        public Foundation.AsyncStatus Status { get; private set; } = Foundation.AsyncStatus.Started;
        public Exception ErrorCode => new OperationCanceledException();

        public Foundation.AsyncOperationCompletedHandler<IRandomAccessStreamWithContentType> Completed
        {
            get => _completed!;
            set
            {
                _completed = value;
                Ready.TrySetResult();
            }
        }

        public Foundation.IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync() => this;
        public IRandomAccessStreamWithContentType GetResults() => throw new OperationCanceledException();
        public void Close() { }

        public void Cancel()
        {
            Status = Foundation.AsyncStatus.Canceled;
            _completed?.Invoke(this, Status);
            Cancelling.TrySetResult(Environment.CurrentManagedThreadId);
            Release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
    }
}
