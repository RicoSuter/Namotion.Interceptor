using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests.Metadata;

public class ChildOrderingTests
{
    private static string[] Order(string[] names, ImmutableArray<string> childOrder)
        => ChildOrdering.Order(names, childOrder, static name => name).ToArray();

    [Fact]
    public void WhenThereIsNoOrder_ThenReadmeAndIndexComeFirstAndTheRestByName()
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
    public void WhenNamesAreEqual_ThenTheInputOrderDecides()
    {
        // Act
        var ordered = ChildOrdering.Order([("b", 0), ("a", 1), ("b", 2), ("a", 3)], [], static child => child.Item1).ToArray();

        // Assert
        Assert.Equal([("a", 1), ("a", 3), ("b", 0), ("b", 2)], ordered);
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

    [Fact]
    public void WhenTheShownNamesAreRead_ThenTheyFollowTheOrderOfTheContainer()
    {
        // Arrange
        var folder = new OrderingTestFolder(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry())
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["b.md"] = new OrderingTestFolder(),
                ["Readme.md"] = new OrderingTestFolder(),
                ["a.md"] = new OrderingTestFolder()
            },
            ChildOrder = ["a.md"]
        };

        // Act
        var names = ChildOrdering.GetShownNames(folder);

        // Assert
        Assert.Equal(["a.md", "Readme.md", "b.md"], names);
    }

    [Fact]
    public void WhenTheChildrenHaveNavigationTitles_ThenTheirNamesDecideTheOrder()
    {
        // Arrange
        var first = new OrderingTestFolder();
        var second = new OrderingTestFolder();
        var folder = new OrderingTestFolder(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry())
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["b.md"] = second, ["a.md"] = first }
        };
        TestMetadata.Set(first, """{ "navTitle": "Zeta" }""");
        TestMetadata.Set(second, """{ "navTitle": "Alpha" }""");

        // Act
        var names = ChildOrdering.GetShownNames(folder);

        // Assert
        Assert.Equal(["a.md", "b.md"], names);
    }

    [Fact]
    public void WhenTheReaderIsListedEveryNameOfTheOrderInTheMetadata_ThenFilterOrderReturnsTheValueUnchanged()
    {
        // Arrange
        var (member, metadata) = CreateFolderWithOrderMetadata();

        // Act
        var filtered = ChildOrdering.FilterOrder(member, metadata, 0, static (_, _) => new HashSet<string> { "a.md", "b.md" });

        // Assert
        Assert.Same(metadata, filtered);
    }

    [Fact]
    public void WhenTheReaderIsNotListedANameOfTheOrderInTheMetadata_ThenFilterOrderRemovesIt()
    {
        // Arrange
        var (member, metadata) = CreateFolderWithOrderMetadata();

        // Act
        var filtered = ChildOrdering.FilterOrder(member, metadata, 0, static (_, _) => new HashSet<string> { "a.md" });

        // Assert
        var order = Assert.IsType<ImmutableDictionary<string, JsonElement>>(filtered)[MetadataKeys.Order];
        Assert.Equal(["a.md"], order.EnumerateArray().Select(static item => item.GetString()));
    }

    private static (RegisteredSubjectProperty Member, ImmutableDictionary<string, JsonElement> Metadata) CreateFolderWithOrderMetadata()
    {
        var folder = new OrderingTestFolder(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry())
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["a.md"] = new OrderingTestFolder(), ["b.md"] = new OrderingTestFolder() }
        };
        TestMetadata.Set(folder, """{ "order": ["b.md", "a.md"] }""");
        var member = folder.TryGetRegisteredSubject()!.TryGetProperty(KnownAttributes.Metadata)!;
        return (member, (ImmutableDictionary<string, JsonElement>)member.GetValue()!);
    }
}

[InterceptorSubject]
public partial class OrderingTestFolder : IMetadataContainer
{
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public partial ImmutableArray<string> ChildOrder { get; set; }

    public OrderingTestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
        ChildOrder = [];
    }
}
