using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathResolutionTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    [Fact]
    public void WhenPathNamesAPropertyOfTheRoot_ThenItResolvesWithNullIndex()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Name");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Name", property.Name);
        Assert.Null(index);
    }

    [Fact]
    public void WhenPathIndexesADictionary_ThenDictionaryPropertyResolvesWithTheKeyAsIndex()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[key1]");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Items", property.Name);
        Assert.Equal("key1", index);
    }

    [Fact]
    public void WhenPathContinuesThroughADictionaryItem_ThenLeafPropertyResolvesWithNullIndex()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[key1].Value");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Value", property.Name);
        Assert.Null(index);
    }

    [Fact]
    public void WhenPathNamesAMissingProperty_ThenPropertyPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "NonExistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPathContinuesThroughAMissingDictionaryKey_ThenPropertyPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["exists"] = new TestItem(context) { Value = "v" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act - the key "missing" does not exist in the dictionary,
        // so navigating through it to .Value should fail
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[missing].Value");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPathIsEmpty_ThenPropertyPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenSubjectPathIndexesADictionary_ThenItResolvesToTheItem()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[key1]");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(item, resolved.Subject);
    }

    [Fact]
    public void WhenSubjectPathIndexesNestedDictionaries_ThenItResolvesToTheInnermostItem()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new TestItem(context) { Value = "leaf" };
        var middle = new TestItem(context) { Value = "middle" };
        middle.Children["leaf"] = leaf;
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["middle"] = middle;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[middle].Children[leaf]");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(leaf, resolved.Subject);
    }

    [Fact]
    public void WhenSubjectPathIndexesAMissingDictionaryKey_ThenSubjectPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[nonexistent]");

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public void WhenSubjectPathIndexesAMissingProperty_ThenSubjectPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Bogus[key]");

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public void WhenSeveralPathsAreResolved_ThenEachPropertyIsReturnedInOrderWithItsIndex()
    {
        // Arrange
        var context = CreateContext();
        var item1 = new TestItem(context) { Value = "v1" };
        var item2 = new TestItem(context) { Value = "v2" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["a"] = item1;
        container.Items["b"] = item2;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        var paths = new[] { "Name", "Items[a]", "Items[b].Value" };

        // Act
        var results = pathProvider.GetPropertiesFromPaths(rootRegistered, paths).ToList();

        // Assert
        Assert.Equal(3, results.Count);

        // "Name" - simple property, no index
        Assert.Equal("Name", results[0].Property.Name);
        Assert.Null(results[0].Index);

        // "Items[a]" - dictionary property with string index
        Assert.Equal("Items", results[1].Property.Name);
        Assert.Equal("a", results[1].Index);

        // "Items[b].Value" - nested property, no index on the leaf
        Assert.Equal("Value", results[2].Property.Name);
        Assert.Null(results[2].Index);
    }

    [Fact]
    public void WhenSeveralPathsAreResolved_ThenPathsThatDoNotResolveAreSkipped()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        var paths = new[] { "Name", "DoesNotExist", "AlsoMissing" };

        // Act
        var results = pathProvider.GetPropertiesFromPaths(rootRegistered, paths).ToList();

        // Assert
        Assert.Single(results);
        Assert.Equal("Name", results[0].Property.Name);
    }

    [Theory]
    [InlineData("Items[jazz")]
    [InlineData("Items[jazz]x")]
    [InlineData("Items[]")]
    public void WhenPathIsMalformed_ThenResolversReturnNull(string path)
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["jazz"] = new TestItem(context) { Value = "v" };
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var property = DefaultPathProvider.Instance.TryGetPropertyFromPath(rootRegistered, path);
        var subject = DefaultPathProvider.Instance.TryGetSubjectFromPath(rootRegistered, path);

        // Assert
        Assert.Null(property);
        Assert.Null(subject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenReferenceOnTheWayIsNull_ThenHookDecidesWhetherPathResolves(bool createsSubject)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        var result = DefaultPathProvider.Instance.TryGetPropertyFromPath(
            person.TryGetRegisteredSubject()!, "Father.FirstName",
            createMissingSubject: _ => createsSubject ? person.Father = new Person(context) : null);

        // Assert
        Assert.Equal(createsSubject, result is not null);
        Assert.Equal(createsSubject, person.Father is not null);
        if (createsSubject)
        {
            Assert.Same(person.Father, result!.Value.Property.Subject);
        }
    }
}
