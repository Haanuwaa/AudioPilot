using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    internal interface IOverlayEmojiImageSourceFactory
    {
        ImageSource? Create(string emoji, double fontSize, double pixelsPerDip);
    }

    internal readonly record struct OverlayInlineToken(OverlayInlineTokenKind Kind, string Text);

    internal enum OverlayInlineTokenKind
    {
        Text = 0,
        Emoji = 1,
        LineBreak = 2
    }

    internal sealed class OverlayInlineBuilder(IOverlayEmojiImageSourceFactory emojiImageSourceFactory)
    {
        /// <summary>Restores breathing room around emoji bitmaps cropped to their visible bounds.</summary>
        internal const double EmojiHorizontalPaddingEm = 0.14d;

        private readonly IOverlayEmojiImageSourceFactory _emojiImageSourceFactory = emojiImageSourceFactory ?? throw new ArgumentNullException(nameof(emojiImageSourceFactory));

        /// <summary>
        /// Splits overlay content into plain-text, emoji, and line-break tokens so text can keep its
        /// configured brush while emoji render through the color-font image path.
        /// </summary>
        public void Append(TextBlock target, string text, FontWeight fontWeight, string? brushKey = null)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!OverlayEmojiClassifier.MightContainEmoji(text))
            {
                AppendPlainText(target, text, fontWeight, brushKey);
                return;
            }

            foreach (OverlayInlineToken token in Tokenize(text))
            {
                switch (token.Kind)
                {
                    case OverlayInlineTokenKind.LineBreak:
                        target.Inlines.Add(new LineBreak());
                        break;
                    case OverlayInlineTokenKind.Emoji:
                        if (!TryAppendEmojiInline(target, token.Text))
                        {
                            target.Inlines.Add(CreateTextRun(token.Text, fontWeight, brushKey));
                        }

                        break;
                    default:
                        target.Inlines.Add(CreateTextRun(token.Text, fontWeight, brushKey));
                        break;
                }
            }
        }

        /// <summary>
        /// Lazily enumerates Unicode text elements, preserving emoji sequences without allocating
        /// per-character strings or tokenizing a hidden remainder after layout stops consuming tokens.
        /// </summary>
        internal static IEnumerable<OverlayInlineToken> Tokenize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            string normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            int textStart = 0;
            int index = 0;
            while (index < normalizedText.Length)
            {
                int length = StringInfo.GetNextTextElementLength(normalizedText.AsSpan(index));
                bool isLineBreak = normalizedText[index] == '\n';
                if (isLineBreak || OverlayEmojiClassifier.IsEmoji(normalizedText.AsSpan(index, length)))
                {
                    if (index > textStart)
                        yield return new OverlayInlineToken(OverlayInlineTokenKind.Text, normalizedText[textStart..index]);

                    yield return new OverlayInlineToken(
                        isLineBreak ? OverlayInlineTokenKind.LineBreak : OverlayInlineTokenKind.Emoji,
                        normalizedText.Substring(index, length));
                    textStart = index + length;
                }

                index += length;
            }

            if (textStart < normalizedText.Length)
                yield return new OverlayInlineToken(OverlayInlineTokenKind.Text, normalizedText[textStart..]);
        }

        private static void AppendPlainText(TextBlock target, string text, FontWeight fontWeight, string? brushKey)
        {
            string normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            int segmentStart = 0;

            for (int index = 0; index < normalizedText.Length; index++)
            {
                if (normalizedText[index] != '\n')
                {
                    continue;
                }

                if (index > segmentStart)
                {
                    target.Inlines.Add(CreateTextRun(normalizedText[segmentStart..index], fontWeight, brushKey));
                }

                target.Inlines.Add(new LineBreak());
                segmentStart = index + 1;
            }

            if (segmentStart < normalizedText.Length)
            {
                target.Inlines.Add(CreateTextRun(normalizedText[segmentStart..], fontWeight, brushKey));
            }
        }

        private bool TryAppendEmojiInline(TextBlock target, string emoji)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(target).PixelsPerDip;
            ImageSource? imageSource = _emojiImageSourceFactory.Create(emoji, target.FontSize, pixelsPerDip);
            if (imageSource is null)
            {
                return false;
            }

            var image = new Image
            {
                Source = imageSource,
                Width = ResolveInlineImageWidth(target, imageSource),
                Height = ResolveInlineImageHeight(target, imageSource),
                Margin = new Thickness(target.FontSize * EmojiHorizontalPaddingEm, 0, target.FontSize * EmojiHorizontalPaddingEm, 0),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            target.Inlines.Add(new InlineUIContainer(image)
            {
                BaselineAlignment = BaselineAlignment.Center
            });

            return true;
        }

        private static double ResolveInlineImageWidth(TextBlock target, ImageSource imageSource)
        {
            double imageHeight = Math.Max(1d, imageSource.Height);
            double targetHeight = ResolveInlineImageHeight(target, imageSource);
            double scale = targetHeight / imageHeight;
            return Math.Max(1d, imageSource.Width * scale);
        }

        private static double ResolveInlineImageHeight(TextBlock target, ImageSource imageSource)
        {
            double imageHeight = Math.Max(1d, imageSource.Height);
            double maxHeight = ResolveInlineMaxHeight(target);
            return Math.Min(imageHeight, maxHeight);
        }

        private static double ResolveInlineMaxHeight(TextBlock target)
        {
            if (!double.IsNaN(target.LineHeight) && target.LineHeight > 0)
            {
                return Math.Max(1d, Math.Min(target.FontSize, target.LineHeight - 1d));
            }

            return Math.Max(1d, target.FontSize);
        }

        private static Run CreateTextRun(string text, FontWeight fontWeight, string? brushKey)
        {
            var run = new Run(text)
            {
                FontWeight = fontWeight
            };

            if (!string.IsNullOrWhiteSpace(brushKey))
            {
                run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            }

            return run;
        }
    }
}
