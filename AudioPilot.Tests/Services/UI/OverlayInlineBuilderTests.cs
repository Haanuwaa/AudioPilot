using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioPilot.Logging;
using AudioPilot.Services.UI.Interop;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Services.UI;

public sealed class OverlayInlineBuilderTests
{
    [Fact]
    public void Tokenize_SplitsEmojiAndLineBreaks()
    {
        IReadOnlyList<OverlayInlineToken> tokens = OverlayInlineBuilder.Tokenize("Desk 😀\nMic");

        Assert.Collection(
            tokens,
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Text, token.Kind);
                Assert.Equal("Desk ", token.Text);
            },
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Emoji, token.Kind);
                Assert.Equal("😀", token.Text);
            },
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.LineBreak, token.Kind);
                Assert.Equal("\n", token.Text);
            },
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Text, token.Kind);
                Assert.Equal("Mic", token.Text);
            });
    }

    [Fact]
    public void Tokenize_RecognizesFlagAndKeycapEmojiSequences()
    {
        IReadOnlyList<OverlayInlineToken> tokens = OverlayInlineBuilder.Tokenize("Flags 🇨🇦 5️⃣");

        Assert.Collection(
            tokens,
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Text, token.Kind);
                Assert.Equal("Flags ", token.Text);
            },
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Emoji, token.Kind);
                Assert.Equal("🇨🇦", token.Text);
            },
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Text, token.Kind);
                Assert.Equal(" ", token.Text);
            },
            token =>
            {
                Assert.Equal(OverlayInlineTokenKind.Emoji, token.Kind);
                Assert.Equal("5️⃣", token.Text);
            });
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("⛽")]
    [InlineData("☕")]
    [InlineData("✅")]
    public void Append_InsertsEmojiInlineContainer_WhenFactoryProvidesImage(string emoji)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var builder = new OverlayInlineBuilder(new RecordingEmojiFactory());
            var textBlock = new TextBlock { FontSize = 16 };

            builder.Append(textBlock, $"Desk {emoji} ready", FontWeights.Bold, "OverlayPrimaryTextBrush");

            Assert.Collection(
                textBlock.Inlines.Cast<Inline>(),
                inline =>
                {
                    Run run = Assert.IsType<Run>(inline);
                    Assert.Equal("Desk ", run.Text);
                    Assert.Equal(FontWeights.Bold, run.FontWeight);
                },
                inline =>
                {
                    InlineUIContainer container = Assert.IsType<InlineUIContainer>(inline);
                    Image image = Assert.IsType<Image>(container.Child);
                    Assert.NotNull(image.Source);
                },
                inline =>
                {
                    Run run = Assert.IsType<Run>(inline);
                    Assert.Equal(" ready", run.Text);
                    Assert.Equal(FontWeights.Bold, run.FontWeight);
                });
        });
    }

    [Fact]
    public void Append_ScalesEmojiInlineToFitFixedLineHeight()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var builder = new OverlayInlineBuilder(new TallEmojiFactory());
            var textBlock = new TextBlock
            {
                FontSize = 17,
                LineHeight = 21,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };

            builder.Append(textBlock, "Before 🔨 after", FontWeights.Bold, "OverlayPrimaryTextBrush");

            InlineUIContainer container = Assert.IsType<InlineUIContainer>(textBlock.Inlines.Cast<Inline>().ElementAt(1));
            Image image = Assert.IsType<Image>(container.Child);
            Assert.Equal(20, image.Height);
            Assert.Equal(25, image.Width);
            Assert.Equal(Stretch.Uniform, image.Stretch);
        });
    }

    [Fact]
    public void Append_FallsBackToPlainText_WhenEmojiRenderingFails()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var builder = new OverlayInlineBuilder(new NullEmojiFactory());
            var textBlock = new TextBlock { FontSize = 16 };

            builder.Append(textBlock, "😀", FontWeights.Normal);

            Run run = Assert.IsType<Run>(Assert.Single(textBlock.Inlines.Cast<Inline>()));
            Assert.Equal("😀", run.Text);
        });
    }

    [Fact]
    public void Append_SkipsEmojiFactory_ForPlainText()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new CountingEmojiFactory();
            var builder = new OverlayInlineBuilder(factory);
            var textBlock = new TextBlock { FontSize = 16 };

            builder.Append(textBlock, "Desk ready", FontWeights.Bold, "OverlayPrimaryTextBrush");

            Run run = Assert.IsType<Run>(Assert.Single(textBlock.Inlines.Cast<Inline>()));
            Assert.Equal("Desk ready", run.Text);
            Assert.Equal(0, factory.CreateCallCount);
        });
    }

    [Fact]
    public void Append_SkipsEmojiFactory_ForPlainTextWithLineBreaks()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new CountingEmojiFactory();
            var builder = new OverlayInlineBuilder(factory);
            var textBlock = new TextBlock { FontSize = 16 };

            builder.Append(textBlock, "Desk\r\nMic", FontWeights.Bold, "OverlayPrimaryTextBrush");

            Assert.Collection(
                textBlock.Inlines.Cast<Inline>(),
                inline =>
                {
                    Run run = Assert.IsType<Run>(inline);
                    Assert.Equal("Desk", run.Text);
                },
                inline => Assert.IsType<LineBreak>(inline),
                inline =>
                {
                    Run run = Assert.IsType<Run>(inline);
                    Assert.Equal("Mic", run.Text);
                });
            Assert.Equal(0, factory.CreateCallCount);
        });
    }

    [Theory]
    [InlineData("😀", 1.0)]
    [InlineData("⛽", 1.0)]
    [InlineData("⛽", 1.5)]
    [InlineData("☕", 2.0)]
    [InlineData("✅", 1.0)]
    public void ColorEmojiImageSourceFactory_RendersNonMonochromeEmoji(string emoji, double pixelsPerDip)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new ColorEmojiImageSourceFactory();

            ImageSource? rendered = factory.Create(emoji, 16, pixelsPerDip);
            Assert.True(
                rendered is BitmapSource,
                factory.LastRenderFailureForTests ?? "Renderer returned null without an exception message.");

            BitmapSource image = (BitmapSource)rendered;
            Assert.True(image.PixelWidth > 0);
            Assert.True(image.PixelHeight > 0);

            int stride = image.PixelWidth * 4;
            byte[] pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);

            bool sawOpaquePixel = false;
            bool sawNonGrayPixel = false;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                byte blue = pixels[index];
                byte green = pixels[index + 1];
                byte red = pixels[index + 2];
                byte alpha = pixels[index + 3];

                if (alpha == 0)
                {
                    continue;
                }

                sawOpaquePixel = true;
                if (!(red == green && green == blue))
                {
                    sawNonGrayPixel = true;
                    break;
                }
            }

            Assert.True(sawOpaquePixel);
            Assert.True(sawNonGrayPixel);
        });
    }

    [Theory]
    [InlineData("⛽", true)]
    [InlineData("⌚", true)]
    [InlineData("⚡", true)]
    [InlineData("⭐", true)]
    [InlineData("❤️", true)]
    [InlineData("👩🏽‍💻", true)]
    [InlineData("❤", false)]
    [InlineData("⛽\uFE0E", false)]
    [InlineData("😀\uFE0E", false)]
    [InlineData("123 + © ∑", false)]
    [InlineData("क्\u200Dष", false)]
    [InlineData("A\uFE0F", false)]
    [InlineData("A\u20E3", false)]
    [InlineData("A🏽", false)]
    [InlineData("5\uFE0F", false)]
    [InlineData("\U0001F0A1", false)]
    [InlineData("🏳", false)]
    [InlineData("🏳️", true)]
    [InlineData("🏳️‍🌈", true)]
    [InlineData("👨‍👩‍👧‍👦", true)]
    [InlineData("👍🏽", true)]
    [InlineData("1\u20E3", true)]
    [InlineData("#\uFE0F\u20E3", true)]
    [InlineData("🇨🇦", true)]
    [InlineData("\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F", true)]
    public void Tokenize_RespectsEmojiAndTextPresentation(string text, bool isEmoji)
    {
        OverlayInlineToken token = Assert.Single(OverlayInlineBuilder.Tokenize(text));
        Assert.Equal(isEmoji ? OverlayInlineTokenKind.Emoji : OverlayInlineTokenKind.Text, token.Kind);
        Assert.Equal(text, token.Text);
    }

    [Theory]
    [InlineData("⛽")]
    [InlineData("😀")]
    public void MediaOverlayInlineTextBlock_RendersEmojiInColour_WithThemedText(string emoji)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var textBlock = new MediaOverlayInlineTextBlock
            {
                Text = emoji,
                FontSize = 24,
                Foreground = Brushes.White,
            };
            textBlock.Measure(new Size(100, 60));
            textBlock.Arrange(new Rect(0, 0, 100, 60));
            var bitmap = new RenderTargetBitmap(100, 60, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(textBlock);
            byte[] pixels = new byte[100 * 60 * 4];
            bitmap.CopyPixels(pixels, 100 * 4, 0);

            bool hasColour = false;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] > 0 && (pixels[index] != pixels[index + 1] || pixels[index + 1] != pixels[index + 2]))
                {
                    hasColour = true;
                    break;
                }
            }

            Assert.True(hasColour, "The overlay must render coloured emoji pixels independently of its white text brush.");
        });
    }

    [Fact]
    public void MediaOverlayInlineTextBlock_Measure_UsesEmojiFactoryAndHonorsMaxLines()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new CountingEmojiFactory();
            var textBlock = new MediaOverlayInlineTextBlock(factory)
            {
                Text = "🔨LONGUNBROKENMEDIATITLEWITHCUSTOMEMOJIANDMORETEXT",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                LineHeight = 21,
                MaxLines = 2
            };

            textBlock.Measure(new Size(120, double.PositiveInfinity));

            Assert.True(factory.CreateCallCount > 0);
            Assert.Equal(42, textBlock.DesiredSize.Height);
        });
    }

    [Fact]
    public void MediaOverlayInlineTextBlock_Render_WrapsEmojiDenseMediaTextWithinMaxLines()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new CountingEmojiFactory();
            var textBlock = new MediaOverlayInlineTextBlock(factory)
            {
                Text = "🔨 LIVE 🔨 HERE 🔨 DRAMA 🔨 NEWS 🔨 VIDEOS 🔨 REACTS 🔨 VIDEOS 🔨 ADDRESSING THE FAKE 14:18 ALLEG...",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DeepSkyBlue,
                LineHeight = 20,
                MaxLines = 3
            };

            textBlock.Measure(new Size(180, double.PositiveInfinity));
            double arrangedHeight = Math.Max(1d, textBlock.DesiredSize.Height);
            textBlock.Arrange(new Rect(0, 0, 180, arrangedHeight));

            Assert.True(factory.CreateCallCount > 0);
            Assert.True(textBlock.DesiredSize.Height <= 60);
            Assert.True(textBlock.DesiredSize.Width <= 180);

            var bitmap = new RenderTargetBitmap(
                180,
                (int)Math.Ceiling(arrangedHeight),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(textBlock);

            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            Assert.Contains(pixels.Chunk(4), pixel => pixel[3] > 0);
        });
    }

    [Fact]
    public void ColorEmojiNativeInterop_CanCreateWicBitmap()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ColorEmojiComHandle<IColorEmojiWicImagingFactory>? factory = null;
            ColorEmojiComHandle<IColorEmojiWicBitmap>? bitmap = null;

            try
            {
                factory = ColorEmojiWicInterop.CreateFactory();

                Guid pixelFormat = ColorEmojiWicInterop.PixelFormat32bppPbgra;
                factory.Interface.CreateBitmap(16, 16, ref pixelFormat, ColorEmojiWicBitmapCreateCacheOption.CacheOnLoad, out IntPtr bitmapPtr);
                bitmap = ColorEmojiComRuntime.Wrap<IColorEmojiWicBitmap>(bitmapPtr);

                Assert.NotNull(factory);
                Assert.NotNull(bitmap);
            }
            finally
            {
                bitmap?.Dispose();
                factory?.Dispose();
            }
        });
    }

    [Theory]
    [InlineData("⛽⛽\n", 20)]
    [InlineData("", 8)]
    public void MediaOverlayInlineTextBlock_DoesNotRenderHiddenEmojiAfterLineLimit(string prefix, double width)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new CountingEmojiFactory();
            var textBlock = new MediaOverlayInlineTextBlock(factory)
            {
                Text = prefix + new string('⛽', 2000),
                FontSize = 16,
                MaxLines = 1,
            };

            textBlock.Measure(new Size(width, 40));

            Assert.InRange(factory.CreateCallCount, 1, 12);
            Assert.InRange(textBlock.DesiredSize.Height, 1, 24);
        });
    }

    [Fact]
    public void ColorEmojiNativeInterop_CanCreateDWriteTextFormat()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ColorEmojiComHandle<IColorEmojiDWriteFactory>? factory = null;
            ColorEmojiComHandle<IColorEmojiDWriteTextFormat>? textFormat = null;

            try
            {
                factory = ColorEmojiDWriteInterop.CreateFactory();
                factory.Interface.CreateTextFormat(
                    "Segoe UI Emoji",
                    IntPtr.Zero,
                    ColorEmojiDWriteFontWeight.Regular,
                    ColorEmojiDWriteFontStyle.Normal,
                    ColorEmojiDWriteFontStretch.Normal,
                    16f,
                    System.Globalization.CultureInfo.CurrentUICulture.Name,
                    out IntPtr textFormatPtr);
                textFormat = ColorEmojiComRuntime.Wrap<IColorEmojiDWriteTextFormat>(textFormatPtr);

                textFormat.Interface.SetTextAlignment(ColorEmojiDWriteTextAlignment.Leading);
                textFormat.Interface.SetParagraphAlignment(ColorEmojiDWriteParagraphAlignment.Near);

                Assert.NotNull(factory);
                Assert.NotNull(textFormat);
            }
            finally
            {
                textFormat?.Dispose();
                factory?.Dispose();
            }
        });
    }

    [Fact]
    public void ColorEmojiNativeInterop_CanBeginAndEndDrawOnWicRenderTarget()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ColorEmojiComHandle<IColorEmojiWicImagingFactory>? wicFactory = null;
            ColorEmojiComHandle<IColorEmojiWicBitmap>? bitmap = null;
            ColorEmojiComHandle<IColorEmojiD2DFactory>? d2dFactory = null;
            ColorEmojiComHandle<IColorEmojiD2DRenderTarget>? renderTarget = null;

            try
            {
                wicFactory = ColorEmojiWicInterop.CreateFactory();
                Guid pixelFormat = ColorEmojiWicInterop.PixelFormat32bppPbgra;
                wicFactory.Interface.CreateBitmap(32, 32, ref pixelFormat, ColorEmojiWicBitmapCreateCacheOption.CacheOnLoad, out IntPtr bitmapPtr);
                bitmap = ColorEmojiComRuntime.Wrap<IColorEmojiWicBitmap>(bitmapPtr);

                d2dFactory = ColorEmojiD2DInterop.CreateFactory();
                ColorEmojiD2DRenderTargetProperties properties = new()
                {
                    Type = ColorEmojiD2DRenderTargetType.Default,
                    PixelFormat = new ColorEmojiD2DPixelFormat
                    {
                        Format = ColorEmojiDxgiFormat.Unknown,
                        AlphaMode = ColorEmojiD2DAlphaMode.Unknown
                    },
                    DpiX = 96f,
                    DpiY = 96f,
                    Usage = ColorEmojiD2DRenderTargetUsage.None,
                    MinLevel = ColorEmojiD2DFeatureLevel.Default
                };

                d2dFactory.Interface.CreateWicBitmapRenderTarget(bitmap.Pointer, ref properties, out IntPtr renderTargetPtr);
                renderTarget = ColorEmojiComRuntime.Wrap<IColorEmojiD2DRenderTarget>(renderTargetPtr);
                ColorEmojiD2DInterop.BeginDraw(renderTarget.Pointer);
                ColorEmojiD2DColorF transparent = ColorEmojiD2DColorF.Transparent;
                ColorEmojiD2DInterop.Clear(renderTarget.Pointer, ref transparent);
                ColorEmojiD2DInterop.EndDraw(renderTarget.Pointer, out _, out _);

                Assert.NotNull(renderTarget);
            }
            finally
            {
                renderTarget?.Dispose();
                d2dFactory?.Dispose();
                bitmap?.Dispose();
                wicFactory?.Dispose();
            }
        });
    }

    [Fact]
    public void ColorEmojiImageSourceFactory_CachesRepeatedRequests()
    {
        TestExecutionGuards.RunSta(() =>
        {
            int renderCalls = 0;
            var factory = new ColorEmojiImageSourceFactory(
                (emoji, fontSize, pixelsPerDip) =>
                {
                    renderCalls++;
                    return CreateTestBitmap();
                },
                new NoOpLogger());

            ImageSource? first = factory.Create("😀", 16, 1.0);
            ImageSource? second = factory.Create("😀", 16, 1.0);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(1, renderCalls);

            ColorEmojiRenderMetricsSnapshot metrics = factory.GetMetricsSnapshotForTests();
            Assert.Equal(2, metrics.RequestCount);
            Assert.Equal(1, metrics.CacheHitCount);
            Assert.Equal(1, metrics.RenderCount);
            Assert.Equal(0, metrics.EvictionCount);
        });
    }

    [Fact]
    public void ColorEmojiImageSourceFactory_FailuresAreCachedBriefly_AndRecoverWithoutLogSpam()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            using var logger = TestLoggerScope.CreateInMemory("emoji-recovery.log");
            var clock = new EmojiTestTimeProvider();
            int renderCalls = 0;
            bool canRender = false;
            var factory = new ColorEmojiImageSourceFactory(
                (_, _, _) =>
                {
                    renderCalls++;
                    return canRender ? CreateTestBitmap() : null;
                }, logger.Logger, clock);

            Assert.Null(factory.Create("😀", 16, 1));
            for (int index = 0; index < 10; index++)
            {
                Assert.Null(factory.Create("😀", 16, 1));
            }

            Assert.Equal(1, renderCalls);
            Assert.Null(factory.Create("⛽", 16, 1));
            Assert.Equal(2, factory.GetMetricsSnapshotForTests().FailureCount);
            canRender = true;
            clock.Advance(TimeSpan.FromSeconds(5));
            ImageSource? recovered = factory.Create("😀", 16, 1);
            Assert.NotNull(recovered);
            Assert.True(recovered.IsFrozen);
            Assert.Same(recovered, factory.Create("😀", 16, 1));
            Assert.Equal(3, renderCalls);

            string log = logger.DisposeAndReadLogText();
            Assert.Single(log.Split('\n'), line => line.Contains("overlay-emoji-render-failed", StringComparison.Ordinal));
            Assert.DoesNotContain("😀", log, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(16, double.NaN)]
    [InlineData(16, double.PositiveInfinity)]
    [InlineData(16, 0)]
    [InlineData(16, -1)]
    public void ColorEmojiImageSourceFactory_RejectsInvalidSizesBeforeRendering(double fontSize, double pixelsPerDip)
    {
        var factory = new ColorEmojiImageSourceFactory((_, _, _) => throw new InvalidOperationException("Renderer must not run."), new NoOpLogger());

        Assert.Null(factory.Create("😀", fontSize, pixelsPerDip));
        Assert.Equal(0, factory.CachedEntryCountForTests);
    }

    [Fact]
    public void ColorEmojiImageSourceFactory_RejectsOversizedTextAndBitmap()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new ColorEmojiImageSourceFactory();
            Assert.Null(factory.Create("😀" + new string('\u0301', 256), 16, 1));
            Assert.Equal(0, factory.CachedEntryCountForTests);
            Assert.Null(factory.Create("😀", 128, 100));
            Assert.Equal("bitmap-size-limit", factory.LastRenderFailureForTests);
        });
    }

    [Fact]
    public void ColorEmojiImageSourceFactory_DpiAndSizeChangesUseDistinctFrozenImages()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var factory = new ColorEmojiImageSourceFactory();
            ImageSource? normal = factory.Create("😀", 16, 1);
            ImageSource? highDpi = factory.Create("😀", 16, 2);
            ImageSource? larger = factory.Create("😀", 24, 1);

            Assert.NotNull(normal);
            Assert.NotNull(highDpi);
            Assert.NotNull(larger);
            Assert.True(normal.IsFrozen && highDpi.IsFrozen && larger.IsFrozen);
            Assert.NotSame(normal, highDpi);
            Assert.NotSame(normal, larger);
            Assert.True(((BitmapSource)highDpi).PixelWidth > ((BitmapSource)normal).PixelWidth);
            Assert.Same(normal, factory.Create("😀", 16, 1));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColorEmojiImageSourceFactory_BoundsCacheUsingLeastRecentlyUsedEviction(bool renderFails)
    {
        TestExecutionGuards.RunSta(() =>
        {
            int renderCalls = 0;
            var factory = new ColorEmojiImageSourceFactory(
                (emoji, fontSize, pixelsPerDip) =>
                {
                    renderCalls++;
                    return renderFails ? null : CreateTestBitmap();
                },
                new NoOpLogger());

            for (int index = 0; index < ColorEmojiImageSourceFactory.MaxCacheEntriesForTests; index++)
            {
                ImageSource? image = factory.Create($"emoji-{index}", 16, 1.0);
                Assert.Equal(renderFails, image is null);
            }

            ImageSource? cachedHotEntry = factory.Create("emoji-0", 16, 1.0);
            Assert.Equal(renderFails, cachedHotEntry is null);

            ImageSource? overflowEntry = factory.Create("emoji-overflow", 16, 1.0);
            Assert.Equal(renderFails, overflowEntry is null);

            ImageSource? evictedEntry = factory.Create("emoji-1", 16, 1.0);
            Assert.Equal(renderFails, evictedEntry is null);

            Assert.Equal(ColorEmojiImageSourceFactory.MaxCacheEntriesForTests, factory.CachedEntryCountForTests);
            Assert.Equal(ColorEmojiImageSourceFactory.MaxCacheEntriesForTests + 2, renderCalls);

            ColorEmojiRenderMetricsSnapshot metrics = factory.GetMetricsSnapshotForTests();
            Assert.Equal(ColorEmojiImageSourceFactory.MaxCacheEntriesForTests + 3, metrics.RequestCount);
            Assert.Equal(1, metrics.CacheHitCount);
            Assert.Equal(ColorEmojiImageSourceFactory.MaxCacheEntriesForTests + 2, metrics.RenderCount);
            Assert.Equal(2, metrics.EvictionCount);
            Assert.Equal(ColorEmojiImageSourceFactory.MaxCacheEntriesForTests, metrics.CacheSize);
        });
    }

    private static BitmapSource CreateTestBitmap()
    {
        byte[] pixels =
        [
            0x00, 0x00, 0xFF, 0xFF,
            0x00, 0xFF, 0x00, 0xFF,
            0xFF, 0x00, 0x00, 0xFF,
            0xFF, 0xFF, 0x00, 0xFF
        ];

        return BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
    }

    private sealed class EmojiTestTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class RecordingEmojiFactory : IOverlayEmojiImageSourceFactory
    {
        public ImageSource? Create(string emoji, double fontSize, double pixelsPerDip)
        {
            return CreateTestBitmap();
        }
    }

    private sealed class TallEmojiFactory : IOverlayEmojiImageSourceFactory
    {
        public ImageSource? Create(string emoji, double fontSize, double pixelsPerDip)
        {
            return BitmapSource.Create(30, 24, 96, 96, PixelFormats.Pbgra32, null, new byte[30 * 24 * 4], 30 * 4);
        }
    }

    private sealed class CountingEmojiFactory : IOverlayEmojiImageSourceFactory
    {
        public int CreateCallCount { get; private set; }

        public ImageSource? Create(string emoji, double fontSize, double pixelsPerDip)
        {
            CreateCallCount++;
            return CreateTestBitmap();
        }
    }

    private sealed class NullEmojiFactory : IOverlayEmojiImageSourceFactory
    {
        public ImageSource? Create(string emoji, double fontSize, double pixelsPerDip) => null;
    }

    private sealed class NoOpLogger : ILogger
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.None;

        public bool IsEnabled(LogLevel level) => false;
        public void Log(LogLevel level, string category, string message, string? methodName = null, Exception? exception = null) { }
        public void Log(LogLevel level, string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null) { }
        public void Trace(string category, string message, string? methodName = null) { }
        public void Trace(string category, Func<string> messageFactory, string? methodName = null) { }
        public void Debug(string category, string message, string? methodName = null) { }
        public void Debug(string category, Func<string> messageFactory, string? methodName = null) { }
        public void Info(string category, string message, string? methodName = null) { }
        public void Info(string category, Func<string> messageFactory, string? methodName = null) { }
        public void Warning(string category, string message, string? methodName = null, Exception? exception = null) { }
        public void Warning(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null) { }
        public void Error(string category, string message, string? methodName = null, Exception? exception = null) { }
        public void Error(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null) { }
        public void Fatal(string category, string message, string? methodName = null, Exception? exception = null) { }
        public void Fatal(string category, Func<string> messageFactory, string? methodName = null, Exception? exception = null) { }
        public void Dispose() { }
    }
}
