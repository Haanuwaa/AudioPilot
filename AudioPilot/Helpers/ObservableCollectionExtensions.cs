using System.Collections.ObjectModel;

namespace AudioPilot.Helpers
{
    public static class ObservableCollectionExtensions
    {
        public static void InsertSorted<T>(this ObservableCollection<T> collection, T item, Comparison<T> comparison)
        {
            if (collection.Count == 0)
            {
                collection.Add(item);
                return;
            }

            int low = 0;
            int high = collection.Count;

            while (low < high)
            {
                int mid = low + ((high - low) / 2);
                int cmp = comparison(collection[mid], item);

                if (cmp <= 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            collection.Insert(low, item);
        }

        public static void InsertSortedRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items, Comparison<T> comparison)
        {
            ArgumentNullException.ThrowIfNull(collection);
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(comparison);

            List<T> sortedItems = [.. items];
            if (sortedItems.Count == 0)
            {
                return;
            }

            sortedItems.Sort(comparison);

            int insertIndex = 0;
            foreach (T item in sortedItems)
            {
                while (insertIndex < collection.Count && comparison(collection[insertIndex], item) <= 0)
                {
                    insertIndex++;
                }

                collection.Insert(insertIndex, item);
                insertIndex++;
            }
        }
    }
}
