using System.Collections.ObjectModel;

namespace AudioPilot.Helpers;

/// <summary>Moves existing items to a gap in their original list, preserving their relative order.</summary>
public sealed record ListReorderRequest(IReadOnlyList<object> Items, int InsertionIndex)
{
    internal bool Apply<T>(ObservableCollection<T> collection) where T : class
    {
        if (InsertionIndex < 0 || InsertionIndex > collection.Count || Items.Count == 0) return false;
        var selected = new HashSet<object>(Items, ReferenceEqualityComparer.Instance);
        if (selected.Count != Items.Count || Items.Any(item => item is not T || !collection.Any(candidate => ReferenceEquals(candidate, item)))) return false;

        T[] moving = [.. collection.Where(item => selected.Contains(item))];
        int destination = InsertionIndex - collection.Take(InsertionIndex).Count(item => selected.Contains(item));
        var reordered = collection.Where(item => !selected.Contains(item)).ToList();
        reordered.InsertRange(destination, moving);
        bool changed = false;
        for (int index = 0; index < reordered.Count; index++)
        {
            if (ReferenceEquals(collection[index], reordered[index])) continue;
            collection.Move(collection.IndexOf(reordered[index]), index);
            changed = true;
        }
        return changed;
    }
}
