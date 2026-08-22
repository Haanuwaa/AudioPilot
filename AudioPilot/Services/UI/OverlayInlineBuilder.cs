using System.Globalization;
using System.Text;
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

            if (!MightContainEmoji(text))
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
        /// Enumerates Unicode text elements to keep multi-codepoint emoji sequences intact.
        /// </summary>
        internal static IReadOnlyList<OverlayInlineToken> Tokenize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return [];
            }

            string normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            List<OverlayInlineToken> tokens = [];
            StringBuilder textBuffer = new();

            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(normalizedText);
            while (enumerator.MoveNext())
            {
                string element = enumerator.GetTextElement();
                if (element == "\n")
                {
                    FlushBufferedText(tokens, textBuffer);
                    tokens.Add(new OverlayInlineToken(OverlayInlineTokenKind.LineBreak, element));
                    continue;
                }

                if (LooksLikeEmoji(element))
                {
                    FlushBufferedText(tokens, textBuffer);
                    tokens.Add(new OverlayInlineToken(OverlayInlineTokenKind.Emoji, element));
                    continue;
                }

                textBuffer.Append(element);
            }

            FlushBufferedText(tokens, textBuffer);
            return tokens;
        }

        private static bool MightContainEmoji(string text)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                int value = rune.Value;
                if (value is 0x200D or 0x20E3 or 0xFE0F || value is >= 0xE0020 and <= 0xE007F)
                {
                    return true;
                }

                if (value is >= 0x1F000 and <= 0x1FAFF)
                {
                    return true;
                }
            }

            return false;
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

        private static void FlushBufferedText(List<OverlayInlineToken> tokens, StringBuilder textBuffer)
        {
            if (textBuffer.Length == 0)
            {
                return;
            }

            tokens.Add(new OverlayInlineToken(OverlayInlineTokenKind.Text, textBuffer.ToString()));
            textBuffer.Clear();
        }

        private static bool LooksLikeEmoji(string textElement)
        {
            bool sawExtendedEmojiScalar = false;
            bool sawRegionalIndicator = false;

            foreach (Rune rune in textElement.EnumerateRunes())
            {
                int value = rune.Value;

                if (value is 0x200D or 0x20E3 or 0xFE0F || value is >= 0xE0020 and <= 0xE007F)
                {
                    return true;
                }

                if (value is >= 0x1F3FB and <= 0x1F3FF)
                {
                    return true;
                }

                if (value is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    sawRegionalIndicator = true;
                    continue;
                }

                if (value is >= 0x1F000 and <= 0x1FAFF)
                {
                    sawExtendedEmojiScalar = true;
                }
            }

            return sawExtendedEmojiScalar || sawRegionalIndicator;
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
                return Math.Max(1d, target.LineHeight - 1d);
            }

            return Math.Max(1d, target.FontSize * 1.2d);
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
