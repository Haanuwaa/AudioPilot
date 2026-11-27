using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class HotkeyPlaceholderBehaviorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Ctrl + Alt + O")]
    public void Prompt_TracksCaptureFocusWithoutChangingShortcut_AcrossFocusAndReload(string shortcut)
    {
        SharedStaDispatcherHost.Run(() =>
        {
            var field = new TextBox { Text = shortcut };
            var behavior = new HotkeyPlaceholderBehavior();
            behavior.Attach(field);
            try
            {
                Assert.Equal("Set a shortcut", TextBoxPlaceholderBehavior.GetText(field));
                Assert.False(TextBoxPlaceholderBehavior.GetShowWhileFocused(field));
                field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, field)
                { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
                Assert.Equal("Press a shortcut\u2026", TextBoxPlaceholderBehavior.GetText(field));
                Assert.True(TextBoxPlaceholderBehavior.GetShowWhileFocused(field));
                TextBoxPlaceholderBehavior.SetIsContextMenuOpen(field, true);
                field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, field, null)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                Assert.False(TextBoxPlaceholderBehavior.GetIsContextMenuOpen(field));
                Assert.Equal("Set a shortcut", TextBoxPlaceholderBehavior.GetText(field));
                Assert.False(TextBoxPlaceholderBehavior.GetShowWhileFocused(field));

                field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, field)
                { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
                TextBoxPlaceholderBehavior.SetIsContextMenuOpen(field, true);
                field.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.False(TextBoxPlaceholderBehavior.GetIsContextMenuOpen(field));
                Assert.False(TextBoxPlaceholderBehavior.GetShowWhileFocused(field));
                field.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal("Set a shortcut", TextBoxPlaceholderBehavior.GetText(field));
                Assert.Equal(shortcut, field.Text);
            }
            finally { behavior.Detach(); }

            Assert.False(TextBoxPlaceholderBehavior.GetShowWhileFocused(field));
            TextBoxPlaceholderBehavior.SetIsContextMenuOpen(field, true);
            field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, field)
            { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            field.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(TextBoxPlaceholderBehavior.GetIsContextMenuOpen(field));
            Assert.Equal("Set a shortcut", TextBoxPlaceholderBehavior.GetText(field));
        });
    }
}
