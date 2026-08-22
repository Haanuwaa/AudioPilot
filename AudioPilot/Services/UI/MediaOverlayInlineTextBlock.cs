using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace AudioPilot.Services.UI
{
    public sealed class MediaOverlayInlineTextBlock : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(
                nameof(FontSize),
                typeof(double),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(
                nameof(FontWeight),
                typeof(FontWeight),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(
                nameof(FontFamily),
                typeof(FontFamily),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontStyleProperty =
            DependencyProperty.Register(
                nameof(FontStyle),
                typeof(FontStyle),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontStretchProperty =
            DependencyProperty.Register(
                nameof(FontStretch),
                typeof(FontStretch),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(FontStretches.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(
                nameof(Foreground),
                typeof(Brush),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineHeightProperty =
            DependencyProperty.Register(
                nameof(LineHeight),
                typeof(double),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MaxLinesProperty =
            DependencyProperty.Register(
                nameof(MaxLines),
                typeof(int),
                typeof(MediaOverlayInlineTextBlock),
                new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        private IOverlayEmojiImageSourceFactory _emojiImageSourceFactory;

        public MediaOverlayInlineTextBlock()
            : this(new ColorEmojiImageSourceFactory())
        {
        }

        internal MediaOverlayInlineTextBlock(IOverlayEmojiImageSourceFactory emojiImageSourceFactory)
        {
            _emojiImageSourceFactory = emojiImageSourceFactory ?? throw new ArgumentNullException(nameof(emojiImageSourceFactory));
        }

        internal void SetEmojiImageSourceFactory(IOverlayEmojiImageSourceFactory emojiImageSourceFactory)
        {
            _emojiImageSourceFactory = emojiImageSourceFactory ?? throw new ArgumentNullException(nameof(emojiImageSourceFactory));
            InvalidateMeasure();
            InvalidateVisual();
        }

        internal IOverlayEmojiImageSourceFactory EmojiImageSourceFactoryForTests => _emojiImageSourceFactory;

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => (FontWeight)GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public FontStyle FontStyle
        {
            get => (FontStyle)GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        public FontStretch FontStretch
        {
            get => (FontStretch)GetValue(FontStretchProperty);
            set => SetValue(FontStretchProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public double LineHeight
        {
            get => (double)GetValue(LineHeightProperty);
            set => SetValue(LineHeightProperty, value);
        }

        public int MaxLines
        {
            get => (int)GetValue(MaxLinesProperty);
            set => SetValue(MaxLinesProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 0d : Math.Max(0d, availableSize.Width);
            List<InlineLayoutLine> lines = BuildLines(width);
            double desiredHeight = lines.Count * ResolveLineHeight();
            return new Size(width, desiredHeight);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            double lineHeight = ResolveLineHeight();
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            List<InlineLayoutLine> lines = BuildLines(Math.Max(0d, ActualWidth));
            double y = 0d;

            foreach (InlineLayoutLine line in lines)
            {
                double x = Math.Max(0d, (ActualWidth - line.Width) / 2d);
                foreach (InlineLayoutToken token in line.Tokens)
                {
                    if (token.ImageSource is ImageSource imageSource)
                    {
                        double imageY = y + Math.Max(0d, (lineHeight - token.Height) / 2d);
                        drawingContext.DrawImage(imageSource, new Rect(x, imageY, token.Width, token.Height));
                    }
                    else if (!string.IsNullOrEmpty(token.Text))
                    {
                        FormattedText formattedText = CreateFormattedText(token.Text, pixelsPerDip);
                        double textY = y + Math.Max(0d, (lineHeight - formattedText.Height) / 2d);
                        drawingContext.DrawText(formattedText, new Point(x, textY));
                    }

                    x += token.Width;
                }

                y += lineHeight;
            }
        }

        private List<InlineLayoutLine> BuildLines(double availableWidth)
        {
            int maxLines = Math.Max(1, MaxLines);
            double lineHeight = ResolveLineHeight();
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            List<InlineLayoutToken> tokens = ExpandOversizedTokens(
                CreateLayoutTokens(pixelsPerDip, lineHeight),
                availableWidth,
                pixelsPerDip);
            List<InlineLayoutLine> lines = [];
            List<InlineLayoutToken> currentLine = [];
            double currentWidth = 0d;
            bool overflowed = false;

            foreach (InlineLayoutToken token in tokens)
            {
                if (token.IsLineBreak)
                {
                    AddLine(lines, currentLine, currentWidth);
                    currentLine = [];
                    currentWidth = 0d;
                    if (lines.Count >= maxLines)
                    {
                        overflowed = true;
                        break;
                    }

                    continue;
                }

                bool shouldWrap = availableWidth > 0d &&
                    currentLine.Count > 0 &&
                    currentWidth + token.Width > availableWidth;
                if (shouldWrap)
                {
                    AddLine(lines, currentLine, currentWidth);
                    currentLine = [];
                    currentWidth = 0d;
                    if (lines.Count >= maxLines)
                    {
                        overflowed = true;
                        break;
                    }
                }

                if (currentLine.Count == 0 && token.IsWhiteSpaceText)
                {
                    continue;
                }

                currentLine.Add(token);
                currentWidth += token.Width;
            }

            if (!overflowed && currentLine.Count > 0 && lines.Count < maxLines)
            {
                AddLine(lines, currentLine, currentWidth);
            }
            else if (lines.Count == 0)
            {
                AddLine(lines, [], 0d);
            }

            if (overflowed && lines.Count > 0)
            {
                lines[^1] = ApplyEllipsis(lines[^1], availableWidth, pixelsPerDip);
            }

            return lines;
        }

        private List<InlineLayoutToken> ExpandOversizedTokens(List<InlineLayoutToken> tokens, double availableWidth, double pixelsPerDip)
        {
            if (availableWidth <= 0d)
            {
                return tokens;
            }

            List<InlineLayoutToken> expandedTokens = [];
            foreach (InlineLayoutToken token in tokens)
            {
                if (token.IsLineBreak ||
                    string.IsNullOrEmpty(token.Text) ||
                    token.Width <= availableWidth)
                {
                    expandedTokens.Add(token);
                    continue;
                }

                foreach (string textElement in EnumerateTextElements(token.Text))
                {
                    expandedTokens.Add(CreateTextToken(textElement, pixelsPerDip));
                }
            }

            return expandedTokens;
        }

        private List<InlineLayoutToken> CreateLayoutTokens(double pixelsPerDip, double lineHeight)
        {
            List<InlineLayoutToken> tokens = [];
            foreach (OverlayInlineToken token in OverlayInlineBuilder.Tokenize(Text ?? string.Empty))
            {
                switch (token.Kind)
                {
                    case OverlayInlineTokenKind.LineBreak:
                        tokens.Add(InlineLayoutToken.LineBreak);
                        break;
                    case OverlayInlineTokenKind.Emoji:
                        tokens.Add(CreateEmojiToken(token.Text, pixelsPerDip, lineHeight) ?? CreateTextToken(token.Text, pixelsPerDip));
                        break;
                    default:
                        foreach (string textElement in SplitTextForLayout(token.Text))
                        {
                            tokens.Add(CreateTextToken(textElement, pixelsPerDip));
                        }

                        break;
                }
            }

            return tokens;
        }

        private static IEnumerable<string> SplitTextForLayout(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            StringBuilder buffer = new();
            bool? bufferIsWhiteSpace = null;
            foreach (string textElement in EnumerateTextElements(text))
            {
                bool isWhiteSpace = string.IsNullOrWhiteSpace(textElement);
                if (buffer.Length > 0 && bufferIsWhiteSpace != isWhiteSpace)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                buffer.Append(textElement);
                bufferIsWhiteSpace = isWhiteSpace;
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
            }
        }

        private InlineLayoutToken? CreateEmojiToken(string emoji, double pixelsPerDip, double lineHeight)
        {
            ImageSource? imageSource = _emojiImageSourceFactory.Create(emoji, FontSize, pixelsPerDip);
            if (imageSource is null)
            {
                return null;
            }

            double height = Math.Min(Math.Max(1d, imageSource.Height), Math.Max(1d, lineHeight - 1d));
            double width = Math.Max(1d, imageSource.Width * (height / Math.Max(1d, imageSource.Height)));
            return InlineLayoutToken.Image(imageSource, width, height);
        }

        private InlineLayoutToken CreateTextToken(string text, double pixelsPerDip)
        {
            FormattedText formattedText = CreateFormattedText(text, pixelsPerDip);
            return InlineLayoutToken.TextRun(
                text,
                Math.Max(0d, formattedText.WidthIncludingTrailingWhitespace),
                Math.Max(1d, formattedText.Height));
        }

        private InlineLayoutLine ApplyEllipsis(InlineLayoutLine line, double availableWidth, double pixelsPerDip)
        {
            if (availableWidth <= 0d)
            {
                return line;
            }

            InlineLayoutToken ellipsis = CreateTextToken("...", pixelsPerDip);
            List<InlineLayoutToken> tokens = [.. line.Tokens];
            double width = line.Width;
            while (tokens.Count > 0 && width + ellipsis.Width > availableWidth)
            {
                InlineLayoutToken removed = tokens[^1];
                tokens.RemoveAt(tokens.Count - 1);
                width -= removed.Width;
            }

            tokens.Add(ellipsis);
            width += ellipsis.Width;
            return new InlineLayoutLine(tokens, width);
        }

        private static void AddLine(List<InlineLayoutLine> lines, List<InlineLayoutToken> tokens, double width)
        {
            while (tokens.Count > 0 && tokens[^1].IsWhiteSpaceText)
            {
                width -= tokens[^1].Width;
                tokens.RemoveAt(tokens.Count - 1);
            }

            lines.Add(new InlineLayoutLine([.. tokens], Math.Max(0d, width)));
        }

        private static IEnumerable<string> EnumerateTextElements(string text)
        {
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                yield return enumerator.GetTextElement();
            }
        }

        private FormattedText CreateFormattedText(string text, double pixelsPerDip)
        {
            Typeface typeface = new(FontFamily, FontStyle, FontWeight, FontStretch);
            return new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Foreground,
                pixelsPerDip);
        }

        private double ResolveLineHeight()
        {
            if (!double.IsNaN(LineHeight) && LineHeight > 0)
            {
                return LineHeight;
            }

            return Math.Max(1d, FontSize * 1.2d);
        }

        private readonly record struct InlineLayoutLine(IReadOnlyList<InlineLayoutToken> Tokens, double Width);

        private readonly record struct InlineLayoutToken(
            string? Text,
            ImageSource? ImageSource,
            double Width,
            double Height,
            bool IsLineBreak)
        {
            public bool IsWhiteSpaceText => !string.IsNullOrEmpty(Text) && string.IsNullOrWhiteSpace(Text);

            public static InlineLayoutToken LineBreak => new(null, null, 0d, 0d, true);

            public static InlineLayoutToken TextRun(string text, double width, double height)
                => new(text, null, width, height, false);

            public static InlineLayoutToken Image(ImageSource imageSource, double width, double height)
                => new(null, imageSource, width, height, false);
        }
    }
}
