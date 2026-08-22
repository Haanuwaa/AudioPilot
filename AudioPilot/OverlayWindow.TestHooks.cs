using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AudioPilot.Models;

namespace AudioPilot
{
    public partial class OverlayWindow
    {
        internal readonly record struct OverlayDisplayStateForTests(
            OverlayPosition Position,
            int StackIndex,
            double DurationSeconds,
            TimeSpan CloseTimerInterval,
            bool HasFadeOutStoryboard,
            bool IsFadeOutCompletionHooked,
            bool IsFadeInRunning,
            bool IsFadeInCompletionHooked);

        internal readonly record struct MediaOverlayTextStateForTests(
            Visibility OverlayTextVisibility,
            Visibility StructuredPanelVisibility,
            Visibility MediaPanelVisibility,
            string Header,
            string Title,
            string Artist,
            Visibility ArtistVisibility,
            string TitleElementType,
            double TitleMaxHeight,
            int TitleMaxLines);

        internal readonly record struct StructuredOverlayRowStateForTests(
            Visibility Visibility,
            Visibility LabelVisibility,
            string Label,
            string Value,
            TextAlignment ValueTextAlignment,
            double ValueMaxHeight,
            TextTrimming ValueTextTrimming,
            TextWrapping ValueTextWrapping);

        internal readonly record struct StructuredOverlayTextStateForTests(
            Visibility OverlayTextVisibility,
            Visibility StructuredPanelVisibility,
            Visibility MediaPanelVisibility,
            string Header,
            IReadOnlyList<StructuredOverlayRowStateForTests> Rows);

        internal OverlayDisplayStateForTests GetDisplayStateForTests()
        {
            return new OverlayDisplayStateForTests(
                _position,
                _stackIndex,
                _durationSeconds,
                _closeTimer.Interval,
                _fadeOutStoryboard != null,
                _isFadeOutCompletionHooked,
                _isFadeInRunning,
                _isFadeInCompletionHooked);
        }

        internal void BeginFadeInForTests() => BeginFadeIn();

        internal void BeginFadeOutAndCloseForTests() => BeginFadeOutAndClose();

        internal void StopFadeOutForTests() => StopFadeOut();

        internal bool MediaEmojiFactoriesAreSharedForTests()
        {
            return ReferenceEquals(
                MediaTitleText.EmojiImageSourceFactoryForTests,
                MediaArtistText.EmojiImageSourceFactoryForTests);
        }

        internal MediaOverlayTextStateForTests GetMediaOverlayTextStateForTests()
        {
            return new MediaOverlayTextStateForTests(
                OverlayText.Visibility,
                StructuredOverlayPanel.Visibility,
                MediaOverlayPanel.Visibility,
                ReadText(MediaHeaderText),
                MediaTitleText.Text,
                MediaArtistText.Text,
                MediaArtistText.Visibility,
                MediaTitleText.GetType().Name,
                MediaTitleText.MaxHeight,
                MediaTitleText.MaxLines);
        }

        internal StructuredOverlayTextStateForTests GetStructuredOverlayTextStateForTests()
        {
            return new StructuredOverlayTextStateForTests(
                OverlayText.Visibility,
                StructuredOverlayPanel.Visibility,
                MediaOverlayPanel.Visibility,
                ReadText(StructuredHeaderText),
                [
                    ReadStructuredOverlayRow(StructuredRow1, StructuredRow1LabelText, StructuredRow1ValueText),
                    ReadStructuredOverlayRow(StructuredRow2, StructuredRow2LabelText, StructuredRow2ValueText),
                    ReadStructuredOverlayRow(StructuredRow3, StructuredRow3LabelText, StructuredRow3ValueText),
                    ReadStructuredOverlayRow(StructuredRow4, StructuredRow4LabelText, StructuredRow4ValueText)
                ]);
        }

        private static StructuredOverlayRowStateForTests ReadStructuredOverlayRow(Grid row, TextBlock labelText, TextBlock valueText)
        {
            return new StructuredOverlayRowStateForTests(
                row.Visibility,
                labelText.Visibility,
                ReadText(labelText),
                ReadText(valueText),
                valueText.TextAlignment,
                valueText.MaxHeight,
                valueText.TextTrimming,
                valueText.TextWrapping);
        }

        private static string ReadText(TextBlock textBlock)
        {
            return new TextRange(textBlock.ContentStart, textBlock.ContentEnd)
                .Text
                .TrimEnd('\r', '\n');
        }

        internal static bool TrySplitListenOverlayDeviceLinesForTests(string deviceName, out string inputLine, out string outputLine)
            => TrySplitListenOverlayDeviceLines(deviceName, out inputLine, out outputLine);
    }
}
