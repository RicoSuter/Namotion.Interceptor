using System.Collections.Immutable;

namespace HomeBlaze.Services.Tests;

public class ChildOrderingTests
{
    private static string[] Order(string[] names, ImmutableArray<string> childOrder)
        => ChildOrdering.Order(names, childOrder, static name => name, static name => name).ToArray();

    [Fact]
    public void WhenThereIsNoOrder_ThenReadmeAndIndexComeFirstAndTheRestByTitle()
    {
        // Act
        var ordered = Order(["b.md", "index.md", "a.md", "Readme.md"], []);

        // Assert
        Assert.Equal(["Readme.md", "index.md", "a.md", "b.md"], ordered);
    }

    [Fact]
    public void WhenOrderListsSomeChildren_ThenTheyComeFirstInListOrder()
    {
        // Act
        var ordered = Order(["a.md", "b.md", "c.md", "Readme.md"], ["c.md", "a.md"]);

        // Assert
        Assert.Equal(["c.md", "a.md", "Readme.md", "b.md"], ordered);
    }

    [Fact]
    public void WhenOrderHasUnknownAndRepeatedNames_ThenTheyAreIgnored()
    {
        // Act
        var ordered = Order(["a.md", "b.md"], ["gone.md", "b.md", "a.md", "b.md"]);

        // Assert
        Assert.Equal(["b.md", "a.md"], ordered);
    }

    [Fact]
    public void WhenTitlesAreEqual_ThenNamesDecide()
    {
        // Act
        var ordered = ChildOrdering.Order(["b", "a"], [], static name => name, static _ => "Same").ToArray();

        // Assert
        Assert.Equal(["a", "b"], ordered);
    }
}
