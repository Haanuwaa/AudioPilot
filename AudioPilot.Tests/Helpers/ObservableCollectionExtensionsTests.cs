using System.Collections.ObjectModel;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public class ObservableCollectionExtensionsTests
{
    private static readonly int[] SortedInts = [1, 2, 3, 4, 5];
    private static readonly string[] SortedNames = ["alpha", "charlie", "delta"];

    [Fact]
    public void Sort_OrdersCollectionByComparison()
    {
        var collection = new ObservableCollection<int> { 5, 1, 4, 2, 3 };

        collection.Sort((a, b) => a.CompareTo(b));

        Assert.Equal(SortedInts, collection);
    }

    [Fact]
    public void Sort_WorksWithReferenceTypes()
    {
        var collection = new ObservableCollection<string> { "delta", "alpha", "charlie" };

        collection.Sort((a, b) => string.Compare(a, b, StringComparison.Ordinal));

        Assert.Equal(SortedNames, collection);
    }

    [Fact]
    public void InsertSortedRange_MergesPendingItemsInSortedOrder()
    {
        var collection = new ObservableCollection<int> { 1, 4, 7 };

        collection.InsertSortedRange([6, 2, 5, 3], static (a, b) => a.CompareTo(b));

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], collection);
    }

    [Fact]
    public void InsertSortedRange_PreservesStablePlacementForEqualValues()
    {
        var collection = new ObservableCollection<string> { "alpha", "charlie" };

        collection.InsertSortedRange(["charlie", "bravo"], static (a, b) => string.Compare(a, b, StringComparison.Ordinal));

        Assert.Equal(["alpha", "bravo", "charlie", "charlie"], collection);
    }
}

