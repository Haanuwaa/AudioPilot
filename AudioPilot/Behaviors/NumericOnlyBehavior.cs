using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors;

public sealed class NumericOnlyBehavior : Behavior<TextBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(AssociatedObject, OnPaste);
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        AssociatedObject.PreviewTextInput -= OnPreviewTextInput;
        DataObject.RemovePastingHandler(AssociatedObject, OnPaste);
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (!IsNumeric(e.Text))
        {
            e.Handled = true;
            return;
        }

        string newText = GetProposedText(e.Text);
        if (!IsValidNumber(newText))
        {
            e.Handled = true;
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            string text = (string)e.DataObject.GetData(DataFormats.Text) ?? string.Empty;
            if (!IsNumeric(text) || !IsValidNumber(GetProposedText(text)))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private static bool IsNumeric(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsDigit(c) && c != '.')
            {
                return false;
            }
        }
        return true;
    }

    private string GetProposedText(string textToAdd)
    {
        TextBox textBox = AssociatedObject;
        int selectionStart = textBox.SelectionStart;
        int selectionLength = textBox.SelectionLength;
        string currentText = textBox.Text;

        string newText = currentText.Remove(selectionStart, selectionLength);
        newText = newText.Insert(selectionStart, textToAdd);

        return newText;
    }

    private static bool IsValidNumber(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        int decimalPointCount = text.Count(c => c == '.');
        if (decimalPointCount > 1)
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }
}

public sealed class IntegerOnlyBehavior : Behavior<TextBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(AssociatedObject, OnPaste);
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        AssociatedObject.PreviewTextInput -= OnPreviewTextInput;
        DataObject.RemovePastingHandler(AssociatedObject, OnPaste);
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (!IsInteger(e.Text))
        {
            e.Handled = true;
            return;
        }

        string newText = GetProposedText(e.Text);
        if (!IsValidInteger(newText))
        {
            e.Handled = true;
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            string text = (string)e.DataObject.GetData(DataFormats.Text) ?? string.Empty;
            if (!IsInteger(text) || !IsValidInteger(GetProposedText(text)))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private static bool IsInteger(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private string GetProposedText(string textToAdd)
    {
        TextBox textBox = AssociatedObject;
        int selectionStart = textBox.SelectionStart;
        int selectionLength = textBox.SelectionLength;
        string currentText = textBox.Text;

        string newText = currentText.Remove(selectionStart, selectionLength);
        newText = newText.Insert(selectionStart, textToAdd);

        return newText;
    }

    private static bool IsValidInteger(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
