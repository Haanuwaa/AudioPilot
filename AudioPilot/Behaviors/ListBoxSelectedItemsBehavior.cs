using System.Collections;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public sealed class ListBoxSelectedItemsBehavior : Behavior<ListBox>
    {
        public static readonly System.Windows.DependencyProperty SelectedItemsProperty =
            System.Windows.DependencyProperty.Register(
                nameof(SelectedItems),
                typeof(IList),
                typeof(ListBoxSelectedItemsBehavior),
                new System.Windows.PropertyMetadata(null, OnSelectedItemsChanged));

        private bool _isSynchronizing;
        private INotifyCollectionChanged? _selectedItemsCollection;

        public IList? SelectedItems
        {
            get => (IList?)GetValue(SelectedItemsProperty);
            set => SetValue(SelectedItemsProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();

            AssociatedObject.SelectionChanged += OnAssociatedObjectSelectionChanged;
            AssociatedObject.PreviewKeyDown += OnAssociatedObjectPreviewKeyDown;
            AttachSelectedItemsCollection(SelectedItems);
            SynchronizeFromSelectedItems();
        }

        protected override void OnDetaching()
        {
            DetachSelectedItemsCollection(_selectedItemsCollection);

            AssociatedObject.SelectionChanged -= OnAssociatedObjectSelectionChanged;
            AssociatedObject.PreviewKeyDown -= OnAssociatedObjectPreviewKeyDown;

            base.OnDetaching();
        }

        private static void OnSelectedItemsChanged(System.Windows.DependencyObject d, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListBoxSelectedItemsBehavior behavior)
            {
                return;
            }

            behavior.DetachSelectedItemsCollection(e.OldValue as INotifyCollectionChanged);
            behavior.AttachSelectedItemsCollection(e.NewValue);
            behavior.SynchronizeFromSelectedItems();
        }

        private void AttachSelectedItemsCollection(object? selectedItems)
        {
            _selectedItemsCollection = selectedItems as INotifyCollectionChanged;
            _selectedItemsCollection?.CollectionChanged += OnSelectedItemsCollectionChanged;
        }

        private void DetachSelectedItemsCollection(INotifyCollectionChanged? selectedItemsCollection)
        {
            selectedItemsCollection?.CollectionChanged -= OnSelectedItemsCollectionChanged;

            if (ReferenceEquals(_selectedItemsCollection, selectedItemsCollection))
            {
                _selectedItemsCollection = null;
            }
        }

        private void OnSelectedItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            SynchronizeFromSelectedItems();
        }

        private void OnAssociatedObjectSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SynchronizeToSelectedItems();
        }

        private void OnAssociatedObjectPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.A || Keyboard.Modifiers != ModifierKeys.Control)
            {
                return;
            }

            if (AssociatedObject.SelectionMode == SelectionMode.Single)
            {
                return;
            }

            AssociatedObject.SelectAll();
            e.Handled = true;
        }

        private void SynchronizeToSelectedItems()
        {
            if (_isSynchronizing || AssociatedObject.SelectionMode == SelectionMode.Single || SelectedItems == null)
            {
                return;
            }

            _isSynchronizing = true;
            try
            {
                SelectedItems.Clear();
                foreach (object item in AssociatedObject.SelectedItems)
                {
                    SelectedItems.Add(item);
                }
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void SynchronizeFromSelectedItems()
        {
            if (_isSynchronizing || AssociatedObject == null || AssociatedObject.SelectionMode == SelectionMode.Single || SelectedItems == null)
            {
                return;
            }

            _isSynchronizing = true;
            try
            {
                AssociatedObject.SelectedItems.Clear();
                foreach (object item in SelectedItems)
                {
                    if (AssociatedObject.Items.Contains(item))
                    {
                        AssociatedObject.SelectedItems.Add(item);
                    }
                }
            }
            finally
            {
                _isSynchronizing = false;
            }
        }
    }
}
