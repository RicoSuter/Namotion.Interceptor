using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathExtensionsTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    // --- TryGetPropertyFromPath ---

    [Fact]
    public void TryGetPropertyFromPath_SimpleProperty_ReturnsPropertyWithNullIndex()
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
    public void TryGetPropertyFromPath_DictionaryPath_ReturnsPropertyWithStringIndex()
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
    public void TryGetPropertyFromPath_NestedDictionaryProperty_ReturnsLeafProperty()
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
    public void TryGetPropertyFromPath_NonExistentPath_ReturnsNull()
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
    public void TryGetPropertyFromPath_NonExistentDictionaryKey_ReturnsNull()
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
    public void TryGetPropertyFromPath_EmptyPath_ReturnsNull()
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

    // --- TryGetSubjectFromPath ---

    [Fact]
    public void TryGetSubjectFromPath_DictionaryIndex_ReturnsChildSubject()
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
    public void TryGetSubjectFromPath_NestedDictionaries_ResolvesDeepSubject()
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
    public void TryGetSubjectFromPath_NonExistentKey_ReturnsNull()
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
    public void TryGetSubjectFromPath_NonExistentProperty_ReturnsNull()
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

    // --- GetPropertiesFromPaths ---

    [Fact]
    public void GetPropertiesFromPaths_MultiplePaths_ReturnsTuplesWithIndices()
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
    public void GetPropertiesFromPaths_SkipsInvalidPaths()
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

    [Fact]
    public void TryGetPropertyFromPath_InlinePaths_SegmentIsUsedAsDictionaryKey()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Child" };
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act — "Servers" is the dictionary key, not a property name
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Servers");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Children", property.Name);
        Assert.Equal("Servers", index);
    }

    [Fact]
    public void TryGetPropertyFromPath_InlinePaths_NestedProperty()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "OpcUaServer" };
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act — navigate through InlinePaths to a property on the child
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Servers.Name");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Name", property.Name);
        Assert.Null(index);
        Assert.Equal("OpcUaServer", property.GetValue());
    }

    [Fact]
    public void TryGetSubjectFromPath_InlinePaths_ResolvesChildSubject()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Server" };
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Servers");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(child, resolved.Subject);
    }

    [Fact]
    public void TryGetSubjectFromPath_InlinePaths_NestedContainers()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new TestInlineContainer(context) { Name = "OpcUaServer" };
        var middle = new TestInlineContainer(context) { Name = "Servers" };
        middle.Children["OpcUaServer"] = leaf;
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = middle;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act — two levels of InlinePaths flattening
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Servers.OpcUaServer");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(leaf, resolved.Subject);
    }

    [Fact]
    public void TryGetPath_InlinePaths_EmitsKeyAsPlainSegment()
    {
        // Arrange — set dictionary via property setter to trigger lifecycle tracking
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Server" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["Servers"] = child }
        };
        var pathProvider = DefaultPathProvider.Instance;

        var childRegistered = child.TryGetRegisteredSubject()!;
        var nameProperty = childRegistered.Properties
            .First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(pathProvider, root);

        // Assert — should be "Servers.Name", not "Children[Servers].Name"
        Assert.Equal("Servers.Name", path);
    }

    [Fact]
    public void TryGetPath_InlinePaths_NestedContainers()
    {
        // Arrange — set dictionaries via property setters
        var context = CreateContext();
        var leaf = new TestInlineContainer(context) { Name = "OpcUaServer" };
        var middle = new TestInlineContainer(context)
        {
            Name = "Servers",
            Children = new Dictionary<string, TestInlineContainer> { ["OpcUaServer"] = leaf }
        };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["Servers"] = middle }
        };
        var pathProvider = DefaultPathProvider.Instance;

        var leafRegistered = leaf.TryGetRegisteredSubject()!;
        var nameProperty = leafRegistered.Properties
            .First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(pathProvider, root);

        // Assert — should be "Servers.OpcUaServer.Name"
        Assert.Equal("Servers.OpcUaServer.Name", path);
    }

    [Fact]
    public void TryGetPath_MixedInlineAndRegular_FormatsCorrectly()
    {
        // Arrange — set dictionary via property setter
        var context = CreateContext();
        var inlineChild = new TestInlineContainer(context) { Name = "Leaf" };
        var inlineRoot = new TestInlineContainer(context)
        {
            Name = "Folder",
            Children = new Dictionary<string, TestInlineContainer> { ["Leaf"] = inlineChild }
        };
        var pathProvider = DefaultPathProvider.Instance;

        var leafRegistered = inlineChild.TryGetRegisteredSubject()!;
        var nameProperty = leafRegistered.Properties
            .First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(pathProvider, inlineRoot);

        // Assert — just one level of inline: "Leaf.Name"
        Assert.Equal("Leaf.Name", path);
    }

    [Fact]
    public void TryGetPropertyFromPath_InlinePaths_NonExistentKey_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "NonExistent.Name");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetPropertyFromPath_WithoutInlinePaths_BracketNotationStillWorks()
    {
        // Arrange — regular TestContainer with bracket notation
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act — bracket notation still works for non-InlinePaths dictionaries
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[key1].Value");

        // Assert
        Assert.NotNull(result);
        var (property, _) = result.Value;
        Assert.Equal("Value", property.Name);
    }

    [Fact]
    public void WhenRootProperty_ThenTryGetPathReturnsPropertyName()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = property.TryGetPath();

        // Assert
        Assert.Equal("Name", path);
    }

    [Fact]
    public void WhenNestedSubjectProperty_ThenTryGetPathReturnsDottedPath()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath();

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenCustomSeparator_ThenTryGetPathUsesSeparator()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath("/");

        // Assert
        Assert.Equal("Father/FirstName", path);
    }

    [Fact]
    public void WhenRootSubjectProvided_ThenTryGetPathStopsAtRoot()
    {
        // Arrange
        var context = CreateContext();
        var grandparent = new Models.Person(context) { FirstName = "Grandparent" };
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        grandparent.Father = parent;
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var pathFromParent = firstNameProperty.TryGetPath(rootSubject: parent);
        var pathFromGrandparent = firstNameProperty.TryGetPath(rootSubject: grandparent);
        var pathAbsolute = firstNameProperty.TryGetPath();

        // Assert
        Assert.Equal("Father.FirstName", pathFromParent);
        Assert.Equal("Father.Father.FirstName", pathFromGrandparent);
        Assert.Equal("Father.Father.FirstName", pathAbsolute);
    }

    [Fact]
    public void WhenRootSubjectNotInParentChain_ThenTryGetPathReturnsNull()
    {
        // Arrange - 'unrelated' is a separate root that is not an ancestor of the property
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var unrelated = new Models.Person(context) { FirstName = "Unrelated" };
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the property is not reachable from 'unrelated', so there is no relative path
        var path = firstNameProperty.TryGetPath(rootSubject: unrelated);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectIsReachableFromRootViaSecondParent_ThenTryGetPathResolvesViaThatParent()
    {
        // Arrange - 'shared' has two parents; only the second is reachable from 'root'
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var other = new Models.Person(context) { FirstName = "Other" };
        var shared = new Models.Person(context) { FirstName = "Shared" };

        // 'shared' is referenced first by 'other' (not under root) and then by root.Father
        other.Father = shared;
        root.Father = shared;
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the first parent ('other') does not reach 'root', so the search must use the second
        var path = firstNameProperty.TryGetPath(rootSubject: root);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenRootSubjectIsPropertyOwner_ThenTryGetPathReturnsPropertyName()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(rootSubject: child);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Fact]
    public void WhenSubjectParentChainHasCycle_ThenTryGetPathReturnsNull()
    {
        // Arrange - mutual Father references form a cycle in the parent chain
        var context = CreateContext();
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var firstNameProperty = a.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - a cycle has no finite path, so it is reported as null rather than throwing
        var path = firstNameProperty.TryGetPath();

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectParentChainHasCycle_ThenTryGetPathWithProviderReturnsNull()
    {
        // Arrange - mutual Father references form a cycle in the parent chain
        var context = CreateContext();
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var firstNameProperty = a.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, rootSubject: null);

        // Assert
        Assert.Null(path);
    }
}
