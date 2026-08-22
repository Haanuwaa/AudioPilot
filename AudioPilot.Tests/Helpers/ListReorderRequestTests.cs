using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public sealed class ListReorderRequestTests
{
    [Theory]
    [InlineData("B", 0, "BACDE")]
    [InlineData("B", 5, "ACDEB")]
    [InlineData("DB", 5, "ACEBD")]
    [InlineData("BD", 2, "ABDCE")]
    [InlineData("BC", 1, "ABCDE")]
    [InlineData("BC", 3, "ABCDE")]
    [InlineData("ABCDE", 5, "ABCDE")]
    public void ReorderPreservesIdentityAndListOrder_AndOnlyRaisesMoveEvents(string selection, int gap, string expected)
    {
        var collection = new ObservableCollection<Item>("ABCDE".Select(character => new Item(character)));
        Item[] original = [.. collection];
        object[] selected = [.. selection.Select(character => (object)original.Single(item => item.Name == character))];
        int notifications = 0;
        collection.CollectionChanged += (_, e) => { Assert.Equal(NotifyCollectionChangedAction.Move, e.Action); notifications++; };
        Assert.Equal(expected != "ABCDE", new ListReorderRequest(selected, gap).Apply(collection));
        Assert.Equal(expected, new string([.. collection.Select(item => item.Name)]));
        Assert.All(collection, item => Assert.Contains(item, original));
        if (expected == "ABCDE") Assert.Equal(0, notifications);
    }

    [Fact]
    public void InvalidOrStaleRequestsLeaveTheListUnchanged()
    {
        Item first = new('A');
        Item second = new('B');
        var collection = new ObservableCollection<Item> { first, second };
        foreach (ListReorderRequest request in new ListReorderRequest[]
        {
            new([first], -1), new([first], 3), new([], 1), new([first, first], 2), new([new Item('A')], 2), new(["A"], 2),
        }) Assert.False(request.Apply(collection));
        Assert.Equal(new[] { first, second }, collection);
    }

    private sealed class Item(char name)
    {
        internal char Name { get; } = name;
    }
}
