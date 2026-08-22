using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AudioPilot.Behaviors;
using AudioPilot.Constants;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

[Collection("WpfApplicationIsolation")]
public sealed class HoverInfoPopupBehaviorTests
{
    [Fact]
    public void DisabledCheckboxHasReadableHelpWithoutEnablingOrChangingIt()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var checkbox = new CheckBox { IsEnabled = false, IsChecked = true };
            var behavior = new HoverInfoPopupBehavior { Text = "Enable Run at startup first." };
            behavior.Attach(checkbox);
            try
            {
                ToolTip tooltip = Assert.IsType<ToolTip>(checkbox.ToolTip);
                TextBlock text = Assert.IsType<TextBlock>(tooltip.Content);
                Assert.Equal(behavior.Text, text.Text);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.True(ToolTipService.GetShowOnDisabled(checkbox));
                Assert.Equal(AppConstants.Timing.TooltipHoverDelayMs, ToolTipService.GetInitialShowDelay(checkbox));
                Assert.False(tooltip.Focusable);
                Assert.False(tooltip.IsHitTestVisible);
                Assert.False(checkbox.IsEnabled);
                Assert.True(checkbox.IsChecked);

                behavior.Text = "Updated explanation";
                Assert.Same(tooltip, checkbox.ToolTip);
                Assert.Equal(behavior.Text, text.Text);
                checkbox.IsEnabled = true;
                Assert.Null(checkbox.ToolTip);
                Assert.False(tooltip.IsOpen);
                Assert.Equal(DependencyProperty.UnsetValue, checkbox.ReadLocalValue(ToolTipService.ShowOnDisabledProperty));
            }
            finally
            {
                behavior.Detach();
            }
        });
    }

    [Fact]
    public void AncestorDisableAndEmptyHelp_UpdateFallbackAndRestoreTooltipSettings()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var parent = new Grid();
            var field = new TextBox();
            parent.Children.Add(field);
            ToolTipService.SetInitialShowDelay(field, 700);
            var behavior = new HoverInfoPopupBehavior { Text = "Help" };
            behavior.Attach(field);
            try
            {
                Assert.Null(field.ToolTip);
                parent.IsEnabled = false;
                Assert.IsType<ToolTip>(field.ToolTip);
                Assert.Equal(700, ToolTipService.GetInitialShowDelay(field));

                behavior.Text = " ";
                Assert.Null(field.ToolTip);
                Assert.Equal(DependencyProperty.UnsetValue, field.ReadLocalValue(ToolTipService.ShowDurationProperty));
                behavior.Text = "Help again";
                Assert.IsType<ToolTip>(field.ToolTip);
            }
            finally
            {
                behavior.Detach();
            }

            Assert.Null(field.ToolTip);
            Assert.Equal(700, ToolTipService.GetInitialShowDelay(field));
            Assert.Equal(DependencyProperty.UnsetValue, field.ReadLocalValue(ToolTipService.ShowOnDisabledProperty));
        });
    }

    [Fact]
    public void ExistingTooltipBindingIsPreservedEvenWhenItInitiallyReturnsNull()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var checkbox = new CheckBox { IsEnabled = false };
            var source = new TextBox();
            BindingOperations.SetBinding(checkbox, FrameworkElement.ToolTipProperty, new Binding(nameof(FrameworkElement.Tag)) { Source = source });
            var behavior = new HoverInfoPopupBehavior { Text = "Fallback" };
            behavior.Attach(checkbox);
            try
            {
                Assert.Null(checkbox.ToolTip);
                source.Tag = "Existing explanation";
                Assert.Equal("Existing explanation", checkbox.ToolTip);
            }
            finally
            {
                behavior.Detach();
            }

            Assert.True(BindingOperations.IsDataBound(checkbox, FrameworkElement.ToolTipProperty));
            Assert.Equal("Existing explanation", checkbox.ToolTip);
        });
    }

    [Fact]
    public void DetachPreservesTooltipAndServiceValuesReplacedByTheOwner()
    {
        TestExecutionGuards.RunSta(() =>
        {
            var button = new Button { IsEnabled = false };
            var behavior = new HoverInfoPopupBehavior { Text = "Help" };
            behavior.Attach(button);
            button.ToolTip = "Replacement";
            ToolTipService.SetShowDuration(button, 12000);
            behavior.Detach();

            Assert.Equal("Replacement", button.ToolTip);
            Assert.Equal(12000, ToolTipService.GetShowDuration(button));
            Assert.Equal(DependencyProperty.UnsetValue, button.ReadLocalValue(ToolTipService.InitialShowDelayProperty));
        });
    }
}
