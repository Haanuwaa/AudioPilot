using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPilot.Behaviors;
using AudioPilot.Tests.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Behaviors;

public partial class HotkeyCaptureBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_SelectAllPreservesTheCurrentDraftIncludingRejectedInput(bool rejected)
    {
        SharedStaDispatcherHost.Run(() =>
        {
            using var editor = new InspectionEditor("Ctrl+F8");
            if (rejected) editor.Press(Key.A);
            string original = editor.Model.ToHotkeyString();
            string text = editor.Field.Text;
            HotkeyWarningKind warning = editor.Model.WarningKind;

            editor.Modifiers = ModifierKeys.Control;
            editor.Press(Key.LeftCtrl);
            Assert.Equal(original, editor.Model.ToHotkeyString());
            Assert.False(editor.Press(Key.A).Handled);
            ApplicationCommands.SelectAll.Execute(null, editor.Field);
            Assert.Equal(text.Length, editor.Field.SelectionLength);
            editor.Modifiers = ModifierKeys.None;
            editor.Release(Key.LeftCtrl);

            Assert.Equal(original, editor.Model.ToHotkeyString());
            Assert.Equal(text, editor.Field.Text);
            Assert.Equal(warning, editor.Model.WarningKind);
            Assert.Equal(text.Length, editor.Field.SelectionLength);

            editor.Modifiers = ModifierKeys.Control | ModifierKeys.Shift;
            editor.Press(Key.LeftCtrl);
            editor.Press(Key.LeftShift);
            Assert.True(editor.Press(Key.F9).Handled);
            Assert.Equal("Ctrl+Shift+F9", editor.Model.ToHotkeyString());
            Assert.Equal(editor.Model.DisplayText, editor.Field.Text);
            Assert.True(BindingOperations.IsDataBound(editor.Field, TextBox.TextProperty));
        });
    }

    [Fact]
    public void Capture_BlankFieldRetainsModifierPreviewAndClearsOnlyItsOwnPreview()
    {
        SharedStaDispatcherHost.Run(() =>
        {
            using var editor = new InspectionEditor(string.Empty);
            editor.Modifiers = ModifierKeys.Control;
            editor.Press(Key.LeftCtrl);
            Assert.Equal("Ctrl", editor.Field.Text);
            editor.Modifiers |= ModifierKeys.Shift;
            editor.Press(Key.LeftShift);
            Assert.Equal("Ctrl + Shift", editor.Field.Text);
            editor.Modifiers = ModifierKeys.Shift;
            editor.Release(Key.LeftCtrl);
            Assert.Equal("Shift", editor.Field.Text);
            editor.Modifiers = ModifierKeys.None;
            editor.Release(Key.LeftShift);
            Assert.Empty(editor.Field.Text);
            Assert.Empty(editor.Model.ToHotkeyString());

            editor.Modifiers = ModifierKeys.Control;
            editor.Press(Key.LeftCtrl);
            editor.Model.SetRejectedHotkey(HotkeyMainInput.FromKeyboard(Key.A), []);
            editor.Modifiers = ModifierKeys.None;
            editor.Release(Key.LeftCtrl);
            Assert.Equal("A", editor.Model.ToHotkeyString());
            Assert.Equal(HotkeyWarningKind.ModifierRequired, editor.Model.WarningKind);
        });
    }

    [Fact]
    public void Capture_NativeEditingAndDropsCannotChangeTheDisplayedShortcut()
    {
        SharedStaDispatcherHost.Run(() =>
        {
            using var editor = new InspectionEditor("Ctrl+F8");
            Assert.True(editor.Field.IsReadOnly);
            Assert.True(editor.Field.IsReadOnlyCaretVisible);
            Assert.False(editor.Field.AllowDrop);
            editor.Field.SelectAll();
            string text = editor.Field.Text;
            EditingCommands.Delete.Execute(null, editor.Field);
            Assert.Equal(text, editor.Field.Text);

            var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, [new DataObject(DataFormats.UnicodeText, "test-drop"), DragDropKeyStates.None,
                    DragDropEffects.Copy, editor.Field, new Point(10, 12)], culture: null)!;
            drop.RoutedEvent = DragDrop.DropEvent;
            editor.Field.RaiseEvent(drop);
            Assert.Equal(text, editor.Field.Text);
            Assert.Equal("Ctrl+F8", editor.Model.ToHotkeyString());

            editor.Modifiers = ModifierKeys.Control;
            editor.Press(Key.LeftCtrl);
            editor.Press(Key.Delete);
            text = editor.Field.Text;
            editor.Field.SelectAll();
            Assert.True(editor.Press(Key.Delete, repeat: true).Handled);
            EditingCommands.DeleteNextWord.Execute(null, editor.Field);
            Assert.Equal(text, editor.Field.Text);
            Assert.Equal("Ctrl+Delete", editor.Model.ToHotkeyString());
            editor.Modifiers = ModifierKeys.None;
            Assert.True(editor.Press(Key.F9).Handled);
            Assert.Equal("F9", editor.Model.ToHotkeyString());
            Assert.Equal(editor.Model.DisplayText, editor.Field.Text);
        });
    }

    [Theory]
    [InlineData(Key.Left, ModifierKeys.None)]
    [InlineData(Key.End, ModifierKeys.None)]
    [InlineData(Key.A, ModifierKeys.Control)]
    [InlineData(Key.Tab, ModifierKeys.Shift)]
    public void Capture_RepeatedInspectionAndFocusNavigationRemainAvailable(Key key, ModifierKeys modifiers)
    {
        SharedStaDispatcherHost.Run(() =>
        {
            using var editor = new InspectionEditor("Ctrl+F8");
            editor.Modifiers = modifiers;
            Assert.False(editor.Press(key, repeat: true).Handled);
            Assert.Equal("Ctrl+F8", editor.Model.ToHotkeyString());
        });
    }

    [Fact]
    public void Capture_UnchangedDisplayRefreshPreservesSelectionAndHorizontalInspection()
    {
        SharedStaDispatcherHost.Run(() =>
        {
            using var editor = new InspectionEditor("Ctrl+Shift+Alt+Win+WheelUp");
            editor.Field.Select(5, 4);
            editor.Field.ScrollToHorizontalOffset(12);
            editor.Layout();
            int start = editor.Field.SelectionStart;
            int length = editor.Field.SelectionLength;
            double offset = editor.Field.HorizontalOffset;
            Assert.True(offset > 0);
            editor.Model.RefreshDisplayText();
            editor.Layout();
            Assert.Equal(start, editor.Field.SelectionStart);
            Assert.Equal(length, editor.Field.SelectionLength);
            Assert.Equal(offset, editor.Field.HorizontalOffset);

            editor.Modifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            editor.Field.RaiseEvent(wheel);
            editor.Layout();
            Assert.True(wheel.Handled);
            Assert.Equal("Ctrl+Shift+Alt+Win+WheelUp", editor.Model.ToHotkeyString());
            Assert.Equal(editor.Field.Text.Length, editor.Field.CaretIndex);
            Assert.Equal(0, editor.Field.SelectionLength);
        });
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public void Capture_DetachRestoresInteractionPropertiesWithoutRemovingBindings(bool readOnly, bool caretVisible, bool allowDrop)
    {
        SharedStaDispatcherHost.Run(() =>
        {
            var original = new { ReadOnly = readOnly, CaretVisible = caretVisible, AllowDrop = allowDrop };
            var field = new TextBox();
            field.SetBinding(TextBoxBase.IsReadOnlyProperty, new Binding(nameof(original.ReadOnly)) { Source = original });
            field.SetBinding(TextBoxBase.IsReadOnlyCaretVisibleProperty, new Binding(nameof(original.CaretVisible)) { Source = original });
            field.SetBinding(UIElement.AllowDropProperty, new Binding(nameof(original.AllowDrop)) { Source = original });
            var behavior = new HotkeyCaptureBehavior(new HotkeyCaptureSession(_ => false), () => 0);
            behavior.Attach(field);
            try
            {
                Assert.True(field.IsReadOnly);
                Assert.True(field.IsReadOnlyCaretVisible);
                Assert.False(field.AllowDrop);
            }
            finally { behavior.Detach(); }
            Assert.Equal(readOnly, field.IsReadOnly);
            Assert.Equal(caretVisible, field.IsReadOnlyCaretVisible);
            Assert.Equal(allowDrop, field.AllowDrop);
            Assert.True(BindingOperations.IsDataBound(field, TextBoxBase.IsReadOnlyProperty));
            Assert.True(BindingOperations.IsDataBound(field, TextBoxBase.IsReadOnlyCaretVisibleProperty));
            Assert.True(BindingOperations.IsDataBound(field, UIElement.AllowDropProperty));
        });
    }

    private sealed class InspectionEditor : IDisposable
    {
        public HotkeyViewModel Model { get; } = new();
        public TextBox Field { get; }
        public ModifierKeys Modifiers { get; set; }
        private readonly HwndSource _source;
        private readonly HotkeyCaptureBehavior _behavior;

        public InspectionEditor(string binding)
        {
            if (binding.Length != 0) Assert.True(Model.LoadFromString(binding));
            Field = new TextBox
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Width = 190,
                Height = 30,
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1)
            };
            Field.SetBinding(TextBox.TextProperty, new Binding(nameof(HotkeyViewModel.DisplayText)) { Source = Model, Mode = BindingMode.OneWay });
            _source = new HwndSource(new HwndSourceParameters("Shortcut text inspection")
            {
                Width = 190,
                Height = 30,
                PositionX = -20000,
                PositionY = -20000,
                WindowStyle = unchecked((int)0x80000000),
            })
            {
                RootVisual = Field
            };
            _behavior = new HotkeyCaptureBehavior(new HotkeyCaptureSession(_ => false), () => 0, IsKeyDown, () => Modifiers) { Target = Model };
            _behavior.Attach(Field);
            Layout();
        }

        private bool IsKeyDown(Key key) => key switch
        {
            Key.LeftCtrl => Modifiers.HasFlag(ModifierKeys.Control),
            Key.LeftShift => Modifiers.HasFlag(ModifierKeys.Shift),
            Key.LeftAlt => Modifiers.HasFlag(ModifierKeys.Alt),
            Key.LWin => Modifiers.HasFlag(ModifierKeys.Windows),
            _ => false,
        };

        public KeyEventArgs Press(Key key, bool repeat = false)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, _source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
            Field.RaiseEvent(args);
            return args;
        }

        public void Release(Key key) => Field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, _source, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });

        public void Layout()
        {
            Field.Measure(new Size(190, 30));
            Field.Arrange(new Rect(0, 0, 190, 30));
            Field.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        }

        public void Dispose() { _behavior.Detach(); _source.Dispose(); }
    }
}
