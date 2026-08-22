using System.Windows;
using AudioPilot.Models;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed class OverlayWindowTests
{
    [Fact]
    public void TrySplitListenOverlayDeviceLines_ReturnsTrue_ForExpectedInput()
    {
        bool result = OverlayWindow.TrySplitListenOverlayDeviceLinesForTests(
            "Desk Mic\nTo: Headphones",
            out string inputLine,
            out string outputLine);

        Assert.True(result);
        Assert.Equal("Desk Mic", inputLine);
        Assert.Equal("To: Headphones", outputLine);
    }

    [Fact]
    public void TrySplitListenOverlayDeviceLines_ReturnsFalse_ForInvalidOutputPrefix()
    {
        bool result = OverlayWindow.TrySplitListenOverlayDeviceLinesForTests(
            "Desk Mic\nHeadphones",
            out string inputLine,
            out string outputLine);

        Assert.False(result);
        Assert.Equal("Desk Mic", inputLine);
        Assert.Equal("Headphones", outputLine);
    }

    [Theory]
    [InlineData(OverlayPosition.TopLeft, 0.2, -3, 0.5, 0)]
    [InlineData(OverlayPosition.BottomCenter, 12.0, 4, 10.0, 4)]
    public void ApplyDisplayOptions_ClampsDurationAndStackIndex(
        OverlayPosition position,
        double durationSeconds,
        int stackIndex,
        double expectedDurationSeconds,
        int expectedStackIndex)
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.ApplyDisplayOptions(position, durationSeconds, stackIndex);
                OverlayWindow.OverlayDisplayStateForTests displayState = window.GetDisplayStateForTests();

                Assert.Equal(position, displayState.Position);
                Assert.Equal(expectedStackIndex, displayState.StackIndex);
                Assert.Equal(expectedDurationSeconds, displayState.DurationSeconds);
                Assert.Equal(TimeSpan.FromSeconds(expectedDurationSeconds), displayState.CloseTimerInterval);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void BeginFadeOutAndClose_AttachesCompletionHandlerIdempotently()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                Assert.True(window.GetDisplayStateForTests().HasFadeOutStoryboard);

                window.BeginFadeOutAndCloseForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);

                window.BeginFadeOutAndCloseForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);

                window.StopFadeOutForTests();
                Assert.False(window.GetDisplayStateForTests().IsFadeOutCompletionHooked);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void StopFadeOut_DetachesFadeInCompletionHandler_AndClearsRunningState()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.BeginFadeInForTests();
                Assert.True(window.GetDisplayStateForTests().IsFadeInRunning);
                Assert.True(window.GetDisplayStateForTests().IsFadeInCompletionHooked);

                window.StopFadeOutForTests();

                Assert.False(window.GetDisplayStateForTests().IsFadeInRunning);
                Assert.False(window.GetDisplayStateForTests().IsFadeInCompletionHooked);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForMediaTrack_UsesDedicatedWrappedTextBlocks()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent("Next track", "A very long title that should stay in the title text block", "Artist");

                OverlayWindow.MediaOverlayTextStateForTests state = window.GetMediaOverlayTextStateForTests();
                Assert.Equal(Visibility.Collapsed, state.OverlayTextVisibility);
                Assert.Equal(Visibility.Collapsed, state.StructuredPanelVisibility);
                Assert.Equal(Visibility.Visible, state.MediaPanelVisibility);
                Assert.Equal("Next track", state.Header);
                Assert.Equal("A very long title that should stay in the title text block", state.Title);
                Assert.Equal("Artist", state.Artist);
                Assert.Equal(Visibility.Visible, state.ArtistVisibility);
                Assert.Equal(63, state.TitleMaxHeight);
                Assert.Equal("MediaOverlayInlineTextBlock", state.TitleElementType);
                Assert.Equal(3, state.TitleMaxLines);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_AfterMediaTrack_RestoresPlainOverlayText()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent("Current track", "Song", null);
                window.UpdateContent("Plain message");

                OverlayWindow.MediaOverlayTextStateForTests state = window.GetMediaOverlayTextStateForTests();
                Assert.Equal(Visibility.Visible, state.OverlayTextVisibility);
                Assert.Equal(Visibility.Collapsed, state.StructuredPanelVisibility);
                Assert.Equal(Visibility.Collapsed, state.MediaPanelVisibility);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForDevice_UsesStructuredTrimmedRows()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent(OverlayDeviceKind.Output, "Switched output device", "Very Long Speakers Device Name");

                OverlayWindow.StructuredOverlayTextStateForTests state = window.GetStructuredOverlayTextStateForTests();
                Assert.Equal(Visibility.Collapsed, state.OverlayTextVisibility);
                Assert.Equal(Visibility.Visible, state.StructuredPanelVisibility);
                Assert.Equal(Visibility.Collapsed, state.MediaPanelVisibility);
                Assert.Equal("Switched output device", state.Header);
                Assert.Equal(Visibility.Visible, state.Rows[0].Visibility);
                Assert.Equal(Visibility.Collapsed, state.Rows[0].LabelVisibility);
                Assert.Equal("Very Long Speakers Device Name", state.Rows[0].Value);
                Assert.Equal(TextAlignment.Center, state.Rows[0].ValueTextAlignment);
                Assert.Equal(60, state.Rows[0].ValueMaxHeight);
                Assert.Equal(TextTrimming.CharacterEllipsis, state.Rows[0].ValueTextTrimming);
                Assert.Equal(TextWrapping.Wrap, state.Rows[0].ValueTextWrapping);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForListenInput_SplitsInputAndOutputRows()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent(OverlayDeviceKind.Input, "Listen to input enabled", "Desk Mic\nTo: Headphones");

                OverlayWindow.StructuredOverlayTextStateForTests state = window.GetStructuredOverlayTextStateForTests();
                Assert.Equal("Listen to input enabled", state.Header);
                Assert.Equal("Desk Mic", state.Rows[0].Value);
                Assert.Equal(Visibility.Collapsed, state.Rows[0].LabelVisibility);
                Assert.Equal("To: ", state.Rows[1].Label);
                Assert.Equal("Headphones", state.Rows[1].Value);
                Assert.Equal(TextAlignment.Left, state.Rows[1].ValueTextAlignment);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateRoutinePartialContent_UsesLabeledStructuredRows()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateRoutinePartialContent(
                    "Desk - Partial",
                    "Speakers",
                    null,
                    null,
                    "Microphone With A Long Name");

                OverlayWindow.StructuredOverlayTextStateForTests state = window.GetStructuredOverlayTextStateForTests();
                Assert.Equal("Desk - Partial", state.Header);
                Assert.Equal("Output: ", state.Rows[0].Label);
                Assert.Equal("Speakers", state.Rows[0].Value);
                Assert.Equal("Input failed: ", state.Rows[1].Label);
                Assert.Equal("Microphone With A Long Name", state.Rows[1].Value);
                Assert.Equal(60, state.Rows[1].ValueMaxHeight);
                Assert.Equal(TextTrimming.CharacterEllipsis, state.Rows[1].ValueTextTrimming);
                Assert.Equal(TextWrapping.Wrap, state.Rows[1].ValueTextWrapping);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void UpdateContent_ForMediaTrack_RendersTitleThroughInlineBuilder()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                window.UpdateContent("Next track", "Launch day 😀 highlights", "Artist");

                OverlayWindow.MediaOverlayTextStateForTests state = window.GetMediaOverlayTextStateForTests();
                Assert.Contains("Launch day", state.Title, StringComparison.Ordinal);
                Assert.Contains("highlights", state.Title, StringComparison.Ordinal);
                Assert.Equal("MediaOverlayInlineTextBlock", state.TitleElementType);
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void Constructor_SharesEmojiCacheAcrossMediaTextElements()
    {
        TestExecutionGuards.RunSta(() =>
        {
            OverlayWindow window = CreateOverlayWindow();

            try
            {
                Assert.True(window.MediaEmojiFactoriesAreSharedForTests());
            }
            finally
            {
                window.Cleanup();
            }
        });
    }

    [Fact]
    public void CalculateOverlayPlacement_CentersWithinWorkArea()
    {
        OverlayWindow.OverlayPixelPlacement placement = OverlayWindow.CalculateOverlayPlacement(
            new Rect(120, 0, 880, 700),
            desiredWidthPx: 360,
            desiredHeightPx: 100,
            marginPxX: 8,
            marginPxY: 8,
            stackGapPx: 4,
            OverlayPosition.Center,
            stackIndex: 0);

        Assert.Equal(380, placement.Left);
        Assert.Equal(300, placement.Top);
        Assert.Equal(360, placement.Width);
        Assert.Equal(100, placement.Height);
    }

    [Fact]
    public void CalculateOverlayPlacement_ConstrainsOverlayToVerySmallWorkArea()
    {
        var workArea = new Rect(50, 70, 20, 15);

        OverlayWindow.OverlayPixelPlacement placement = OverlayWindow.CalculateOverlayPlacement(
            workArea,
            desiredWidthPx: 360,
            desiredHeightPx: 100,
            marginPxX: 8,
            marginPxY: 8,
            stackGapPx: 4,
            OverlayPosition.BottomRight,
            stackIndex: 3);

        Assert.True(placement.Width <= workArea.Width);
        Assert.True(placement.Height <= workArea.Height);
        Assert.InRange(placement.Left, (int)workArea.Left, (int)workArea.Right - placement.Width);
        Assert.InRange(placement.Top, (int)workArea.Top, (int)workArea.Bottom - placement.Height);
    }

    [Theory]
    [InlineData(OverlayPosition.TopRight)]
    [InlineData(OverlayPosition.BottomRight)]
    public void CalculateOverlayPlacement_ExplicitCumulativeOffsetKeepsVariableHeightCardsSeparated(OverlayPosition position)
    {
        var workArea = new Rect(0, 0, 1000, 800);
        OverlayWindow.OverlayPixelPlacement first = OverlayWindow.CalculateOverlayPlacement(
            workArea,
            desiredWidthPx: 360,
            desiredHeightPx: 140,
            marginPxX: 8,
            marginPxY: 8,
            stackGapPx: 5,
            position,
            stackIndex: 0,
            stackOffsetPx: 0);
        OverlayWindow.OverlayPixelPlacement second = OverlayWindow.CalculateOverlayPlacement(
            workArea,
            desiredWidthPx: 360,
            desiredHeightPx: 80,
            marginPxX: 8,
            marginPxY: 8,
            stackGapPx: 5,
            position,
            stackIndex: 1,
            stackOffsetPx: 145);

        if (position == OverlayPosition.TopRight)
        {
            Assert.Equal(first.Top + first.Height + 5, second.Top);
        }
        else
        {
            Assert.Equal(second.Top + second.Height + 5, first.Top);
        }
    }

    private static OverlayWindow CreateOverlayWindow()
    {
        return new OverlayWindow("Overlay Test");
    }
}
