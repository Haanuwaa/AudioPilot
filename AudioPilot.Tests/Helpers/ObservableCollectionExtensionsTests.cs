using System.Collections.ObjectModel;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public class ObservableCollectionExtensionsTests
{

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

