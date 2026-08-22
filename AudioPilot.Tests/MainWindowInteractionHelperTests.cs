using System.Windows.Controls;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests;

public sealed class MainWindowInteractionHelperTests
{
    [Fact]
    public void ShouldClearRootFocus_ReturnsFalse_WhenSourceIsNull()
    {
        Assert.False(MainWindowInteractionHelper.ShouldClearRootFocus(null, null));
    }

    [Fact]
    public void ShouldClearRootFocus_ReturnsFalse_WhenComboBoxDropDownIsOpen()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ComboBox comboBox = new()
            {
                IsDropDownOpen = true
            };

            bool result = MainWindowInteractionHelper.ShouldClearRootFocus(comboBox, comboBox);

            Assert.False(result);
        });
    }

    [Fact]
    public void ShouldClearRootFocus_ReturnsFalse_ForInteractiveSource()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Button button = new();

            bool result = MainWindowInteractionHelper.ShouldClearRootFocus(button, new Grid());

            Assert.False(result);
        });
    }

    [Fact]
    public void ShouldClearRootFocus_ReturnsTrue_ForPlainRootSource()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            Border border = new();

            bool result = MainWindowInteractionHelper.ShouldClearRootFocus(border, new Grid());

            Assert.True(result);
        });
    }

    [Theory]
    [InlineData(50, 100, 0, 50)]
    [InlineData(50, 100, 120, 2)]
    [InlineData(50, 100, -120, 98)]
    [InlineData(0, 100, 120, 0)]
    [InlineData(100, 100, -120, 100)]
    public void ClampMouseWheelOffset_ReturnsExpectedValue(double currentOffset, double scrollableHeight, int delta, double expected)
    {
        double result = MainWindowInteractionHelper.ClampMouseWheelOffset(currentOffset, scrollableHeight, delta);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void UnselectListBoxIfSelected_Unselects_WhenSelectionExists()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ListBox listBox = new();
            listBox.Items.Add("Desk");
            listBox.Items.Add("Headset");
            listBox.SelectedIndex = 1;

            bool result = MainWindowInteractionHelper.UnselectListBoxIfSelected(listBox);

            Assert.True(result);
            Assert.Equal(-1, listBox.SelectedIndex);
        });
    }

    [Fact]
    public void UnselectListBoxIfSelected_ReturnsFalse_WhenNothingSelected()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            ListBox listBox = new();
            listBox.Items.Add("Desk");

            bool result = MainWindowInteractionHelper.UnselectListBoxIfSelected(listBox);

            Assert.False(result);
        });
    }
}
