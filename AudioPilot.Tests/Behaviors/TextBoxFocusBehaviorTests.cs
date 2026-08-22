using System.Windows.Controls;
using System.Windows.Input;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class TextBoxFocusBehaviorTests
{
    [Fact]
    public void InitialKeyboardFocus_AppendsAfterExistingTextAndPreservesLaterCaretChoices()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var textBox = new TextBox { Text = "Desk 🔊" };
            TextBoxFocusBehavior.SetCaretAtEndOnFirstFocus(textBox, true);

            RaiseKeyboardFocus(textBox);

            Assert.Equal(textBox.Text.Length, textBox.CaretIndex);
            textBox.CaretIndex = 0;
            TextBoxFocusBehavior.SetCaretAtEndOnFirstFocus(textBox, false);
            TextBoxFocusBehavior.SetCaretAtEndOnFirstFocus(textBox, true);
            RaiseKeyboardFocus(textBox);
            Assert.Equal(0, textBox.CaretIndex);
        });
    }

    [Theory]
    [InlineData(MouseButton.Left, 0, false)]
    [InlineData(MouseButton.Left, 3, false)]
    [InlineData(MouseButton.Left, 0, true)]
    [InlineData(MouseButton.Left, 3, true)]
    [InlineData(MouseButton.Right, 0, false)]
    [InlineData(MouseButton.Right, 3, false)]
    [InlineData(MouseButton.Right, 0, true)]
    [InlineData(MouseButton.Right, 3, true)]
    public void PointerFocus_PreservesClickPlacement(MouseButton button, int clickedPosition, bool alreadyHandled)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var textBox = new TextBox { Text = "Routine name" };
            TextBoxFocusBehavior.SetCaretAtEndOnFirstFocus(textBox, true);
            textBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Handled = alreadyHandled,
            });
            textBox.CaretIndex = clickedPosition;

            RaiseKeyboardFocus(textBox);

            Assert.Equal(clickedPosition, textBox.CaretIndex);
            textBox.CaretIndex = 3;
            RaiseKeyboardFocus(textBox);
            Assert.Equal(3, textBox.CaretIndex);
        });
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("caret")]
    [InlineData("readonly")]
    [InlineData("multiline")]
    [InlineData("disabled")]
    public void InitialFocus_PreservesExplicitEditingState(string state)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var textBox = new TextBox { Text = "Routine name", IsReadOnly = state == "readonly", AcceptsReturn = state == "multiline" };
            TextBoxFocusBehavior.SetCaretAtEndOnFirstFocus(textBox, true);
            if (state == "selection")
            {
                textBox.SelectAll();
            }
            else if (state == "caret")
            {
                textBox.CaretIndex = 3;
            }
            else if (state == "disabled")
            {
                TextBoxFocusBehavior.SetCaretAtEndOnFirstFocus(textBox, false);
            }
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;

            RaiseKeyboardFocus(textBox);

            Assert.Equal(selectionStart, textBox.SelectionStart);
            Assert.Equal(selectionLength, textBox.SelectionLength);
        });
    }

    private static void RaiseKeyboardFocus(TextBox textBox)
    {
        textBox.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, textBox)
        {
            RoutedEvent = Keyboard.GotKeyboardFocusEvent,
        });
    }
}
