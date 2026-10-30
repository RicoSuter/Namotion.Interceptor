using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
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


    // --- Immutable dictionary values behind a broadly declared property ---

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndKeyIsAbsent_ThenSubjectPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[absent]");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndNumericKeyIsAbsent_ThenSubjectPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        // The text "1" is looked up as the string key "1", which is absent.
        var result = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[1]");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndIntermediateKeyIsAbsent_ThenPropertyPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[absent].Value");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndKeyExists_ThenSubjectPathResolves()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[key1]");

        // Assert
        Assert.NotNull(result);
        Assert.Same(item, result.Subject);
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
        var path = property.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Name", path);
    }

    [Fact]
    public void WhenUsingDefaultProviderOverload_ThenDelegatesToDefaultProvider()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the convenience overload uses DefaultPathProvider.Instance and threads rootSubject through
        var absolute = firstNameProperty.TryGetPath();
        var relative = firstNameProperty.TryGetPath(parent);

        // Assert
        Assert.Equal("Father.FirstName", absolute);
        Assert.Equal(firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null), absolute);
        Assert.Equal(firstNameProperty.TryGetPath(DefaultPathProvider.Instance, parent), relative);
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
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

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
        var path = firstNameProperty.TryGetPath(new DefaultPathProvider('/'), null);

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
        var pathFromParent = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, parent);
        var pathFromGrandparent = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, grandparent);
        var pathAbsolute = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

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
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, unrelated);

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
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

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
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, child);

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
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectReachableFromRootViaTwoAcyclicPaths_ThenTryGetPathResolves()
    {
        // Arrange - a diamond: 'shared' is reachable from 'root' through both Father->A and Mother->B,
        // so the per-branch cycle pruning must not falsely prune the shared subject.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        var shared = new Models.Person(context) { FirstName = "Shared" };
        root.Father = a;
        root.Mother = b;
        a.Father = shared; // shared's first parent
        b.Father = shared; // shared's second parent
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert - resolves via the first parent that reaches the root
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenSharedDeadEndIsMemoizedDuringSearch_ThenRootStillResolvesViaAnotherBranch()
    {
        // Arrange - 'leaf' has three parents. The first two (p1, p2) lead only to a shared dead-end
        // subject ('dead') that cannot reach the root, so the search explores 'dead' via p1, memoizes it
        // as unreachable, then hits that memo when reaching 'dead' again via p2. The third parent ('good')
        // reaches the root. This exercises the unreachable-memoization path and confirms a memoized
        // dead-end does not prevent the root being found through another branch.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var good = new Models.Person(context) { FirstName = "Good" };
        var p1 = new Models.Person(context) { FirstName = "P1" };
        var p2 = new Models.Person(context) { FirstName = "P2" };
        var dead = new Models.Person(context) { FirstName = "Dead" };
        var leaf = new Models.Person(context) { FirstName = "Leaf" };

        p1.Father = leaf;   // leaf's first parent
        p2.Father = leaf;   // leaf's second parent
        good.Father = leaf; // leaf's third parent (the only one reaching the root)
        dead.Father = p1;   // p1 -> dead (dead end)
        dead.Mother = p2;   // p2 -> the same shared dead end
        root.Father = good; // good -> root
        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenParentChainHasCycleButRootReachableViaOtherBranch_ThenTryGetPathResolves()
    {
        // Arrange - 'shared' has a cyclic first parent (cyclicX <-> cyclicY) that never reaches the root,
        // and a second parent 'good' that does. The cycle on the first branch must not prevent the search
        // from finding the root through the acyclic branch.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var good = new Models.Person(context) { FirstName = "Good" };
        var cyclicX = new Models.Person(context) { FirstName = "X" };
        var cyclicY = new Models.Person(context) { FirstName = "Y" };
        var shared = new Models.Person(context) { FirstName = "Shared" };

        cyclicX.Mother = shared;  // shared's first parent (leads into a cycle)
        good.Father = shared;     // shared's second parent (reaches the root)
        cyclicX.Father = cyclicY;
        cyclicY.Father = cyclicX; // cyclicX <-> cyclicY cycle
        root.Father = good;       // good is reachable from root
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenMultiParentChainIsCyclicAndRootUnreachable_ThenTryGetPathReturnsNull()
    {
        // Arrange - 'leaf' has two parents (forcing the multi-parent search): one into a cycle, one a
        // dead end. The provided root is reachable from neither, so the result is null (not a throw).
        var context = CreateContext();
        var unrelated = new Models.Person(context) { FirstName = "Unrelated" };
        var leaf = new Models.Person(context) { FirstName = "Leaf" };
        var cyclic = new Models.Person(context) { FirstName = "Cyclic" };
        var deadEnd = new Models.Person(context) { FirstName = "DeadEnd" };

        cyclic.Father = leaf;     // leaf's first parent
        deadEnd.Mother = leaf;    // leaf's second parent (forces the multi-parent search)
        leaf.Father = cyclic;     // leaf <-> cyclic cycle on the first branch
        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, unrelated);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenMultiParentChainIsVeryDeep_ThenTryGetPathResolvesWithoutStackOverflow()
    {
        // Arrange - a parent chain far deeper than recursion would tolerate, with a multi-parent branch at
        // the leaf to force the iterative search. Because the search uses an explicit stack (not the call
        // stack) and is not depth-bounded, it resolves the full path instead of overflowing or giving up.
        const int depth = 10_000;
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var chain = new List<Models.Person> { root };
        for (var i = 0; i < depth; i++)
        {
            var next = new Models.Person(context) { FirstName = $"P{i}" };
            chain[^1].Father = next; // chain[i] is the parent of chain[i+1]
            chain.Add(next);
        }

        var leaf = chain[^1];
        var extra = new Models.Person(context) { FirstName = "Extra" };
        extra.Mother = leaf; // second parent for the leaf forces the multi-parent search

        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert - resolves to "Father" repeated for each hop up the chain, then the leaf property
        Assert.NotNull(path);
        var segments = path!.Split('.');
        Assert.Equal(depth + 1, segments.Length);
        Assert.Equal("FirstName", segments[^1]);
        Assert.All(segments[..^1], segment => Assert.Equal("Father", segment));
    }

    [Fact]
    public void WhenPropertyIsCollectionElement_ThenDefaultProviderPathIncludesBracketIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new TryGetPathIndexRoot(context);
        root.Items = [new TryGetPathIndexChild(context), new TryGetPathIndexChild(context)];
        var nameProperty = root.Items[1].TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Items[1].Name", path);
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

    // --- Resolver options ---

    private sealed class ExcludingPathProvider(string excludedPropertyName) : PathProviderBase
    {
        public override bool IsPropertyIncluded(RegisteredSubjectProperty property) => property.Name != excludedPropertyName;
    }

    [Fact]
    public void WhenHoldingPropertyIsExcluded_ThenNoPathIsBuiltThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context)
        {
            Father = new Person(context) { FirstName = "Dad", Mother = new Person(context) { FirstName = "Grandma" } },
            Mother = new Person(context) { FirstName = "Mom" }
        };
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));

        // Act
        var throughExcluded = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!.TryGetPath(pathProvider, null);
        var deepThroughExcluded = person.TryGetRegisteredProperty(p => p.Father!.Mother!.FirstName)!.TryGetPath(pathProvider, person);
        var excludedItself = person.TryGetRegisteredProperty(p => p.Father)!.TryGetPath(pathProvider, null);
        var included = person.TryGetRegisteredProperty(p => p.Mother!.FirstName)!.TryGetPath(pathProvider, null);
        var belowExcludedFromItsSubject = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!.TryGetPath(pathProvider, person.Father);

        // Assert
        Assert.Null(throughExcluded);
        Assert.Null(deepThroughExcluded);
        Assert.Null(excludedItself);
        Assert.Equal("Mother.FirstName", included);
        Assert.Equal("FirstName", belowExcludedFromItsSubject);
    }

    [Fact]
    public void WhenSharedSubjectIsHeldThroughAnExcludedAndAnIncludedProperty_ThenPathGoesThroughTheIncludedOne()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person(context) { FirstName = "Shared" };
        var person = new Person(context) { Father = shared, Children = [shared] };
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));

        // Act
        var path = shared.TryGetRegisteredProperty(p => p.FirstName)!.TryGetPath(pathProvider, person);

        // Assert
        Assert.Equal("Children[0].FirstName", path);
    }

    [Theory]
    [InlineData(null, "Mother.Father.FirstName")]
    [InlineData(nameof(Person.Mother), "Children[0].Father.FirstName")]
    [InlineData(nameof(Person.Father), null)]
    public void WhenSubjectHasSeveralParents_ThenPathIsTheFirstInParentOrderThroughIncludedProperties(
        string? excludedPropertyName, string? expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "Shared" };
        var root = new Person(context)
        {
            Mother = new Person { Father = shared },
            Children = [new Person { Father = shared }]
        };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName ?? "");

        // Act
        var path = shared.TryGetRegisteredProperty(p => p.FirstName)!.TryGetPath(pathProvider, root);

        // Assert
        Assert.Equal(2, shared.TryGetRegisteredSubject()!.Parents.Length);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void WhenFirstParentIsOutsideTheRoot_ThenPathGoesThroughTheParentThatReachesIt()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "Shared" };
        var child = new Person { Father = shared };
        _ = new Person(context)
        {
            Mother = new Person { Father = shared },
            Children = [child]
        };

        // Act
        var path = shared.TryGetRegisteredProperty(p => p.FirstName)!.TryGetPath(DefaultPathProvider.Instance, child);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenHoldingPropertyIsExcluded_ThenNoPathResolvesThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.Father));
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var property = pathProvider.TryGetPropertyFromPath(registered, "Father.FirstName");
        var holdingProperty = pathProvider.TryGetPropertyFromPath(registered, "Father");
        var subject = pathProvider.TryGetSubjectFromPath(registered, "Father");
        var properties = pathProvider.GetPropertiesFromPaths(registered, ["Father.FirstName", "FirstName"]).ToList();

        // Assert
        Assert.Null(property);
        Assert.Null(holdingProperty);
        Assert.Null(subject);
        Assert.Equal("FirstName", Assert.Single(properties).Property.Name);
    }

    [Fact]
    public void WhenLeafIsExcluded_ThenItIsNeitherBuiltNorResolved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { LastName = "Doe" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));

        // Act
        var path = person.TryGetRegisteredProperty(p => p.Father!.LastName)!.TryGetPath(pathProvider, null);
        var property = pathProvider.TryGetPropertyFromPath(person.TryGetRegisteredSubject()!, "Father.LastName");

        // Assert
        Assert.Null(path);
        Assert.Null(property);
    }

    [Fact]
    public void WhenPropertyIsExcluded_ThenItsAttributesAreNeitherBuiltNorResolved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.FirstName));
        var firstName = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!;
        var attribute = firstName.TryGetAttribute("MaxLength")!;
        var attributeOfAttribute = attribute.TryGetAttribute("Unit")!;
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var attributePath = attribute.TryGetPath(pathProvider, person);
        var attributeOfAttributePath = attributeOfAttribute.TryGetPath(pathProvider, person);
        var resolvedAttribute = pathProvider.TryGetPropertyFromPath(registered, "Father.MaxLength");
        var resolvedAttributeOfAttribute = pathProvider.TryGetPropertyFromPath(registered, "Father.Unit");
        var paths = new[] { attribute, attributeOfAttribute }.GetPaths(pathProvider, person).ToList();

        // Assert
        Assert.Null(attributePath);
        Assert.Null(attributeOfAttributePath);
        Assert.Null(resolvedAttribute);
        Assert.Null(resolvedAttributeOfAttribute);
        Assert.Empty(paths);
    }

    [Fact]
    public void WhenAttributeIsExcluded_ThenItsAttributeIsNeitherBuiltNorResolvedButItsPropertyIs()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.FirstName_MaxLength));
        var firstName = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!;
        var attributeOfAttribute = firstName.TryGetAttribute("MaxLength")!.TryGetAttribute("Unit")!;
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var propertyPath = firstName.TryGetPath(pathProvider, person);
        var attributeOfAttributePath = attributeOfAttribute.TryGetPath(pathProvider, person);
        var resolvedAttributeOfAttribute = pathProvider.TryGetPropertyFromPath(registered, "Father.Unit");

        // Assert
        Assert.Equal("Father.FirstName", propertyPath);
        Assert.Null(attributeOfAttributePath);
        Assert.Null(resolvedAttributeOfAttribute);
    }

    [Fact]
    public void WhenPropertyAndItsAttributesAreIncluded_ThenAttributePathsAreBuiltAndResolved()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { Father = new Person(context) { FirstName = "Dad" } };
        var pathProvider = new ExcludingPathProvider(nameof(Person.LastName));
        var attribute = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!.TryGetAttribute("MaxLength")!;
        var attributeOfAttribute = attribute.TryGetAttribute("Unit")!;
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var attributePath = attribute.TryGetPath(pathProvider, person);
        var attributeOfAttributePath = attributeOfAttribute.TryGetPath(pathProvider, person);
        var resolvedAttribute = pathProvider.TryGetPropertyFromPath(registered, attributePath!);
        var resolvedAttributeOfAttribute = pathProvider.TryGetPropertyFromPath(registered, attributeOfAttributePath!);

        // Assert
        Assert.Equal("Father.MaxLength", attributePath);
        Assert.Equal("Father.Unit", attributeOfAttributePath);
        Assert.Same(attribute, resolvedAttribute?.Property);
        Assert.Same(attributeOfAttribute, resolvedAttributeOfAttribute?.Property);
    }

    [Fact]
    public void WhenAttributeHoldsSubjectBelowExcludedProperty_ThenNoPathIsBuiltOrResolvedThroughIt()
    {
        // Arrange
        var context = CreateContext();
        var child = new AttributeHolder(context) { Label = "Child" };
        var root = new AttributeHolder(context) { Label = "Root", Label_Child = child };
        var pathProvider = new ExcludingPathProvider(nameof(AttributeHolder.Label));
        var value = child.TryGetRegisteredProperty(c => c.Value)!;
        var registered = root.TryGetRegisteredSubject()!;

        // Act
        var pathWithRoot = value.TryGetPath(pathProvider, root);
        var pathWithoutRoot = value.TryGetPath(pathProvider, null);
        var resolvedProperty = pathProvider.TryGetPropertyFromPath(registered, "Child.Value");
        var resolvedSubject = pathProvider.TryGetSubjectFromPath(registered, "Child");

        // Assert
        Assert.Equal("Child.Value", value.TryGetPath(DefaultPathProvider.Instance, root));
        Assert.Null(pathWithRoot);
        Assert.Null(pathWithoutRoot);
        Assert.Null(resolvedProperty);
        Assert.Null(resolvedSubject);
    }

    [Fact]
    public void WhenSharedSubjectIsHeldByAttributeOfExcludedPropertyAndByIncludedProperty_ThenPathGoesThroughTheIncludedOne()
    {
        // Arrange
        var context = CreateContext();
        var shared = new AttributeHolder(context) { Label = "Shared" };
        var root = new AttributeHolder(context) { Label = "Root", Label_Child = shared };
        root.Other = shared;
        var pathProvider = new ExcludingPathProvider(nameof(AttributeHolder.Label));

        // Act
        var path = shared.TryGetRegisteredProperty(c => c.Value)!.TryGetPath(pathProvider, root);

        // Assert
        Assert.Equal(nameof(AttributeHolder.Label_Child), shared.TryGetRegisteredSubject()!.Parents[0].Property.Name);
        Assert.Equal("Other.Value", path);
    }

    [Fact]
    public void WhenInlinePathsDictionaryIsExcluded_ThenItsKeysAreNeitherBuiltNorResolved()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Server" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["Servers"] = child }
        };
        var pathProvider = new ExcludingPathProvider(nameof(TestInlineContainer.Children));
        var registered = root.TryGetRegisteredSubject()!;

        // Act
        var path = child.TryGetRegisteredProperty(c => c.Name)!.TryGetPath(pathProvider, root);
        var bareKey = pathProvider.TryGetPropertyFromPath(registered, "Servers.Name");
        var explicitKey = pathProvider.TryGetPropertyFromPath(registered, "Children[Servers].Name");
        var subject = pathProvider.TryGetSubjectFromPath(registered, "Servers");

        // Assert
        Assert.Null(path);
        Assert.Null(bareKey);
        Assert.Null(explicitKey);
        Assert.Null(subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Mother))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.FirstName))]
    [InlineData(nameof(Person.FirstName_MaxLength))]
    public void WhenPathIsBuiltWithAProvider_ThenItResolvesBackToTheSamePropertyAndPassesNoExcludedProperty(string? excludedPropertyName)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context)
        {
            FirstName = "Root",
            Father = new Person(context) { FirstName = "Dad", Mother = new Person(context) { FirstName = "Grandma" } },
            Mother = new Person(context) { FirstName = "Mom" },
            Children = [new Person(context) { FirstName = "Kid", Father = new Person(context) { FirstName = "Step" } }]
        };
        var pathProvider = new ExcludingPathProvider(excludedPropertyName ?? "");
        var registered = person.TryGetRegisteredSubject()!;

        // Act
        var results = registered
            .GetAllProperties()
            .Select(property => (Property: property, Path: property.TryGetPath(pathProvider, person)))
            .ToList();

        // Assert
        Assert.Contains(results, result => result.Path is not null);
        foreach (var (property, path) in results)
        {
            Assert.Equal(IsChainIncluded(property, person, pathProvider), path is not null);
            if (path is not null)
            {
                var resolved = pathProvider.TryGetPropertyFromPath(registered, path);
                Assert.NotNull(resolved);
                Assert.Same(property, resolved.Value.Property);
            }
        }
    }

    private static bool IsChainIncluded(RegisteredSubjectProperty property, IInterceptorSubject root, PathProviderBase pathProvider)
    {
        while (true)
        {
            if (!pathProvider.IsPropertyIncluded(property) ||
                (property.IsAttribute && !IsAttributedPropertyIncluded(property, pathProvider)))
            {
                return false;
            }

            if (property.Subject == root)
            {
                return true;
            }

            property = property.Parent.Parents[0].Property;
        }
    }

    private static bool IsAttributedPropertyIncluded(RegisteredSubjectProperty attribute, PathProviderBase pathProvider)
    {
        var property = attribute.GetAttributedProperty();
        return pathProvider.IsPropertyIncluded(property) &&
            (!property.IsAttribute || IsAttributedPropertyIncluded(property, pathProvider));
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

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexRoot
{
    public partial TryGetPathIndexChild[] Items { get; set; }

    public TryGetPathIndexRoot()
    {
        Items = [];
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexChild
{
    public partial string Name { get; set; }

    public TryGetPathIndexChild()
    {
        Name = "";
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class AttributeHolder
{
    public partial string? Label { get; set; }

    [Namotion.Interceptor.Registry.Attributes.PropertyAttribute(nameof(Label), "Child")]
    public partial AttributeHolder? Label_Child { get; set; }

    public partial AttributeHolder? Other { get; set; }

    public partial string? Value { get; set; }
}
