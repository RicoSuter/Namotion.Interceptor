using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests;

public class MetadataStoreExtensionsTests
{
    private static (OrderTestStore Store, OrderTestFolder Folder) CreateTree(params string[] childNames)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithParents();
        var folder = new OrderTestFolder
        {
            Children = childNames.ToDictionary(name => name, IInterceptorSubject (_) => new OrderTestFolder())
        };
        var store = new OrderTestStore(context) { Folder = folder };
        return (store, folder);
    }

    [Fact]
    public async Task WhenOrderIsSet_ThenTheNamesComeFirstAndTheOtherChildrenFollowInTheirShownOrder()
    {
        // Arrange
        var (store, folder) = CreateTree("a.md", "b.md", "c.md", "d.md");
        folder.ChildOrder = ["d.md", "c.md", "b.md", "a.md"];

        // Act
        await folder.SetOrderAsync(["b.md", "d.md"], CancellationToken.None);

        // Assert
        var (subject, propertyName, values) = Assert.Single(store.Writes);
        Assert.Same(folder, subject);
        Assert.Null(propertyName);
        Assert.Equal("""{"order":["b.md","d.md","c.md","a.md"]}""", values.GetRawText());
    }

    [Fact]
    public async Task WhenOrderHasAnUnknownName_ThenItFailsNamingItAndTheChildren()
    {
        // Arrange
        var (store, folder) = CreateTree("a.md", "b.md");

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => folder.SetOrderAsync(["b.md", "gone.md"], CancellationToken.None));

        // Assert
        Assert.Contains("'gone.md'", exception.Message);
        Assert.Contains("'a.md', 'b.md'", exception.Message);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task WhenOrderHasANameTwice_ThenItFailsNamingIt()
    {
        // Arrange
        var (store, folder) = CreateTree("a.md", "b.md");

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => folder.SetOrderAsync(["b.md", "a.md", "b.md"], CancellationToken.None));

        // Assert
        Assert.Contains("'b.md'", exception.Message);
        Assert.DoesNotContain("'a.md'", exception.Message);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task WhenOrderIsSetOnASubjectThatIsNoFolder_ThenItFails()
    {
        // Arrange
        var (store, _) = CreateTree("a.md");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetOrderAsync(["a.md"], CancellationToken.None));
    }
}

[InterceptorSubject]
public partial class OrderTestFolder : IMetadataContainer
{
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public partial ImmutableArray<string> ChildOrder { get; set; }

    public OrderTestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
        ChildOrder = [];
    }
}

[InterceptorSubject]
public partial class OrderTestStore : IMetadataStore
{
    public partial OrderTestFolder? Folder { get; set; }

    public List<(IInterceptorSubject Subject, string? PropertyName, JsonElement Values)> Writes { get; } = [];

    public bool CanStoreMetadata(IInterceptorSubject subject) => true;

    public Task SetMetadataAsync(IInterceptorSubject subject, string? propertyName, JsonElement values, CancellationToken cancellationToken)
    {
        Writes.Add((subject, propertyName, values));
        return Task.CompletedTask;
    }
}
