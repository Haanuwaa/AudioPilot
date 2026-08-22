using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class TrimmedTextPopupBehaviorTests
{
    [Fact]
    public void PointerPressCancelsPendingHover_EvenWhenTheListHandlesSelection()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TextBlock text = CreateArrangedTextBlock("A long routine name that needs the popup", 80);
            var behavior = new TrimmedTextPopupBehavior();
            behavior.Attach(text);
            try
            {
                var pending = new CancellationTokenSource();
                CancellationToken token = pending.Token;
                TestPrivateAccess.SetField(behavior, "_hoverDelayCts", pending);
                text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent,
                    Handled = true,
                });
                Assert.True(token.IsCancellationRequested);
                Assert.Null(TestPrivateAccess.GetField<CancellationTokenSource?>(behavior, "_hoverDelayCts"));
            }
            finally { behavior.Detach(); }
        });
    }

    [Fact]
    public void IsTextTrimmed_ReturnsFalseWhenTextFits()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TextBlock textBlock = CreateArrangedTextBlock("Speakers", 160);

            Assert.False(TrimmedTextPopupBehavior.IsTextTrimmed(textBlock));
        });
    }

    [Fact]
    public void IsTextTrimmed_ReturnsTrueWhenTextMeaningfullyOverflows()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TextBlock textBlock = CreateArrangedTextBlock("A very long application audio session name", 80);

            Assert.True(TrimmedTextPopupBehavior.IsTextTrimmed(textBlock));
        });
    }

    [Fact]
    public void IsTextTrimmed_RequiresAnEllipsisPolicy()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            TextBlock textBlock = CreateArrangedTextBlock("A very long application audio session name", 80);
            textBlock.TextTrimming = TextTrimming.None;

            Assert.False(TrimmedTextPopupBehavior.IsTextTrimmed(textBlock));
        });
    }

    [Theory]
    [InlineData(100, 100, 1)]
    [InlineData(100.75, 100, 1)]
    [InlineData(100.5, 100, 1.5)]
    public void ExceedsAvailableWidth_IgnoresLayoutRoundingDifferences(
        double textWidth,
        double availableWidth,
        double dpiScale)
    {
        Assert.False(TrimmedTextPopupBehavior.ExceedsAvailableWidth(textWidth, availableWidth, dpiScale));
    }

    [Theory]
    [InlineData(101.01, 100, 1)]
    [InlineData(100.67, 100, 1.5)]
    [InlineData(100.51, 100, 2)]
    public void ExceedsAvailableWidth_DetectsMeaningfulOverflowAtCurrentDpi(
        double textWidth,
        double availableWidth,
        double dpiScale)
    {
        Assert.True(TrimmedTextPopupBehavior.ExceedsAvailableWidth(textWidth, availableWidth, dpiScale));
    }

    [Theory]
    [InlineData(double.NaN, 100, 1)]
    [InlineData(100, double.NaN, 1)]
    [InlineData(100, -1, 1)]
    public void ExceedsAvailableWidth_RejectsInvalidLayoutMeasurements(
        double textWidth,
        double availableWidth,
        double dpiScale)
    {
        Assert.False(TrimmedTextPopupBehavior.ExceedsAvailableWidth(textWidth, availableWidth, dpiScale));
    }

    private static TextBlock CreateArrangedTextBlock(string text, double width)
    {
        TextBlock textBlock = new()
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Width = width,
        };

        textBlock.Measure(new Size(width, 40));
        textBlock.Arrange(new Rect(0, 0, width, 40));
        return textBlock;
    }
}
