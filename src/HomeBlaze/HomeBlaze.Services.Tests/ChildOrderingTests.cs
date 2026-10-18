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

    [Fact]
    public void WhenTheOrderOrTheReadmeDecides_ThenNoTitleIsRead()
    {
        // Arrange
        var titledNames = new List<string>();

        // Act
        var ordered = ChildOrdering.Order(["b.md", "Readme.md", "a.md", "c.md"], ["c.md"], static name => name, name =>
        {
            titledNames.Add(name);
            return name;
        }).ToArray();

        // Assert
        Assert.Equal(["c.md", "Readme.md", "a.md", "b.md"], ordered);
        Assert.Equal(["a.md", "b.md"], titledNames.Order());
    }

    [Fact]
    public void WhenTheOrderIsLong_ThenUnknownAndRepeatedNamesAreIgnoredAsWell()
    {
        // Arrange
        var names = Enumerable.Range(0, 20).Select(index => $"{index:D2}.md").ToArray();
        ImmutableArray<string> childOrder = ["gone.md", .. names.Reverse(), "00.md"];

        // Act
        var ordered = Order(names, childOrder);

        // Assert
        Assert.Equal(names.Reverse(), ordered);
    }
}
