using System.Windows;
using System.Windows.Controls;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class ComboBoxPlaceholderTests
{
    [Theory]
    [InlineData("Dark", false)]
    [InlineData("Light", false)]
    [InlineData("Dark", true)]
    [InlineData("Light", true)]
    public void Placeholder_DisappearsOnSelectionOrTypingAndReturnsWhenCleared(string theme, bool editable)
    {
        TestExecutionGuards.RunSta(() =>
        {
            var host = new Grid();
            host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/AudioPilot;component/Themes/{theme}Theme.xaml", UriKind.Relative) });
            var picker = new ComboBox { Width = 240, Height = 32, IsEditable = editable };
            TextBoxPlaceholderBehavior.SetText(picker, "Choose a device");
            picker.Items.Add("Headset");
            host.Children.Add(picker);
            host.Measure(new Size(300, 60));
            host.Arrange(new Rect(0, 0, 300, 60));
            host.UpdateLayout();
            var placeholder = Assert.IsType<TextBlock>(picker.Template.FindName("ComboPlaceholder", picker));
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            Assert.False(placeholder.IsHitTestVisible);
            Assert.False(placeholder.Focusable);
            Assert.Equal("", picker.Text);
            Assert.Null(picker.SelectedItem);
            picker.SelectedIndex = 0;
            host.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            picker.SelectedIndex = -1;
            picker.Text = "";
            host.UpdateLayout();
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            if (editable)
            {
                picker.Text = "New group";
                host.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
                Assert.Null(picker.SelectedItem);
            }
        });
    }
}
