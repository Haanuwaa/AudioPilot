using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AudioPilot.Behaviors;
using AudioPilot.Helpers;
using AudioPilot.Tests.Helpers;

namespace AudioPilot.Tests.Behaviors;

public sealed class ListBoxReorderBehaviorTests
{
    [Theory]
    [InlineData("own")]
    [InlineData("foreign")]
    [InlineData("changed")]
    public void DropAcceptsOnlyTheUnchangedSourceList_AndPreservesBoundMultiSelection(string source)
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var items = new ObservableCollection<object> { "A", "B", "C" };
            object[] original = [.. items];
            object[] moving = [items[0], items[2]];
            var selected = new ObservableCollection<object>();
            var list = new ListBox { Width = 220, Height = 160, ItemsSource = items, SelectionMode = SelectionMode.Extended };
            var root = new AdornerDecorator { Child = list };
            root.Measure(new Size(220, 160));
            root.Arrange(new Rect(0, 0, 220, 160));
            root.UpdateLayout();
            int drops = 0;
            using var command = new RelayCommand(parameter => { drops++; ((ListReorderRequest)parameter!).Apply(items); });
            var selectionBehavior = new ListBoxSelectedItemsBehavior { SelectedItems = selected };
            var behavior = new ListBoxReorderBehavior { ReorderCommand = command };
            selectionBehavior.Attach(list);
            behavior.Attach(list);
            try
            {
                foreach (object item in moving) list.SelectedItems.Add(item);
                TestPrivateAccess.SetField(behavior, "_dragItems", moving);
                TestPrivateAccess.SetField(behavior, "_originalOrder", original);
                if (source == "changed") items.Move(0, 1);
                object[] transferred = source == "foreign" ? [.. moving] : moving;
                var data = new DataObject("AudioPilot.ListReorder", transferred, false);
                var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, list, new Point(20, 150)], culture: null)!;
                args.RoutedEvent = DragDrop.PreviewDropEvent;
                list.RaiseEvent(args);
                Assert.True(args.Handled);
                Assert.Equal(source == "own" ? 1 : 0, drops);
                Assert.Equal(source == "own" ? DragDropEffects.Move : DragDropEffects.None, args.Effects);
                if (source == "own")
                {
                    Assert.Equal(new[] { original[1], original[0], original[2] }, items);
                    Assert.Equal(moving, selected);
                    Assert.Equal(moving, list.SelectedItems.Cast<object>());
                }
            }
            finally
            {
                behavior.Detach();
                selectionBehavior.Detach();
            }
        });
    }

    [Fact]
    public void DropPlacementUsesRowMidpoints_AndRejectsOutsideTheList()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var list = new ListBox { Width = 220, Height = 160 };
            list.Items.Add(new ListBoxItem { Content = "A", Height = 30 });
            list.Items.Add(new ListBoxItem { Content = "B", Height = 50 });
            list.Items.Add(new ListBoxItem { Content = "C", Height = 30 });
            var root = new AdornerDecorator { Child = list };
            root.Measure(new Size(220, 160));
            root.Arrange(new Rect(0, 0, 220, 160));
            root.UpdateLayout();
            var behavior = new ListBoxReorderBehavior();
            behavior.Attach(list);
            try
            {
                Assert.True(list.AllowDrop);
                var middle = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                Point top = middle.TranslatePoint(new Point(), list);
                Assert.Equal(1, behavior.UpdateDropTarget(new Point(20, top.Y + 5)));
                Assert.Equal(2, behavior.UpdateDropTarget(new Point(20, top.Y + 45)));
                Assert.Equal(3, behavior.UpdateDropTarget(new Point(20, 150)));
                Assert.NotEmpty(AdornerLayer.GetAdornerLayer(list)!.GetAdorners(list) ?? []);
                Assert.Equal(-1, behavior.UpdateDropTarget(new Point(-1, 50)));
            }
            finally { behavior.Detach(); }
            Assert.False(list.AllowDrop);
            Assert.Empty(AdornerLayer.GetAdornerLayer(list)!.GetAdorners(list) ?? []);
        });
    }

    [Fact]
    public void InteractiveContentDoesNotStartAReorderGesture()
    {
        TestExecutionGuards.RunOnSharedSta(() =>
        {
            var text = new TextBlock { Text = "Button text" };
            var button = new Button { Content = text };
            Assert.True(ListBoxReorderBehavior.IsInteractive(text));
            Assert.True(ListBoxReorderBehavior.IsInteractive(new Slider()));
            Assert.True(ListBoxReorderBehavior.IsInteractive(new TextBox()));
            Assert.True(ListBoxReorderBehavior.IsInteractive(new System.Windows.Controls.Primitives.ScrollBar()));
            Assert.False(ListBoxReorderBehavior.IsInteractive(new TextBlock { Text = "Row label" }));
            GC.KeepAlive(button);
        });
    }
}
