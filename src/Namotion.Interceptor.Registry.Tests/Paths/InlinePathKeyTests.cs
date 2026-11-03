using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class InlinePathKeyTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    private static RegisteredSubjectProperty GetNameProperty(IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

    [Theory]
    [InlineData("CNC01", "CNC01.Name")]
    [InlineData("x]y", "x]y.Name")]
    [InlineData("a'b", "a'b.Name")]
    [InlineData("a[b", "Children[a[b].Name")]
    [InlineData("192.168.0.1", "Children[192.168.0.1].Name")]
    [InlineData("Name", "Children[Name].Name")]
    [InlineData("Children", "Children[Children].Name")]
    [InlineData("Report [2024]", "Children['Report [2024]'].Name")]
    [InlineData("a.b]", "Children['a.b]'].Name")]
    [InlineData("", "Children[''].Name")]
    public void WhenInlineKeyIsWritten_ThenPathRoundTrips(string key, string expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "child" };
        var root = new TestInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestInlineContainer> { [key] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(DefaultPathProvider.Instance, null);
        var resolved = DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Same(child, resolved?.Property.Subject);
    }

    [Fact]
    public void WhenInlineDictionaryHasIntKeys_ThenPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestNumberedInlineContainer(context) { Name = "child" };
        var root = new TestNumberedInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<int, TestNumberedInlineContainer> { [7] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(DefaultPathProvider.Instance, null);
        var resolved = DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal("7.Name", path);
        Assert.Same(child, resolved?.Property.Subject);
    }

    [Fact]
    public void WhenWrongNameCarriesIndexOnInlineFallback_ThenPathDoesNotResolve()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestInlineContainer> { ["x"] = new(context) { Name = "child" } }
        };

        // Act
        var resolved = DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, "Typo[x].Name");

        // Assert
        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("CNC01", "CNC01.name")]
    [InlineData("name", "Children[name].name")]
    [InlineData("a.b", "Children[a.b].name")]
    public void WhenInlinePropertyIsUnmapped_ThenAttributeProviderPathRoundTrips(string key, string expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestMappedInlineContainer(context) { Name = "child" };
        var root = new TestMappedInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestMappedInlineContainer> { [key] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);
        var resolved = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Same(child, resolved?.Property.Subject);
    }

    [Fact]
    public void WhenInlinePropertySegmentIsShadowed_ThenBareKeyRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestShadowedInlineContainer(context) { Name = "child", Label = "label" };
        var root = new TestShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["CNC01"] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);
        var resolved = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal("CNC01.name", path);
        Assert.Same(GetNameProperty(child), resolved?.Property);
    }

    [Fact]
    public void WhenInlinePropertySegmentIsShadowed_ThenKeyNeedingExplicitFormHasNoPath()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestShadowedInlineContainer(context) { Name = "child", Label = "label" };
        _ = new TestShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { ["a.b"] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenInlinePropertySegmentIsShadowed_ThenInlinePropertyHasNoPath()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var root = new TestShadowedInlineContainer(context) { Name = "root", Label = "label" };
        var children = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestShadowedInlineContainer.Children))!;

        // Act
        var path = children.TryGetPath(pathProvider, null);
        var pathFromRoot = children.TryGetPath(pathProvider, root);

        // Assert
        Assert.Null(path);
        Assert.Null(pathFromRoot);
    }

    [Fact]
    public void WhenAPropertyDeclaredAfterTheInlinePropertyHasItsSegment_ThenThatPropertyRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var root = new TestLateShadowedInlineContainer(context) { Name = "root", Label = "label" };
        var registered = root.TryGetRegisteredSubject()!;

        // Act
        var path = registered.TryGetProperty(nameof(TestLateShadowedInlineContainer.Label))!.TryGetPath(pathProvider, null);
        var resolved = pathProvider.TryGetPropertyFromPath(registered, path!);

        // Assert
        Assert.Equal("Children", path);
        Assert.Equal(nameof(TestLateShadowedInlineContainer.Label), resolved?.Property.Name);
        Assert.Null(resolved?.Index);
    }

    [Fact]
    public void WhenAPropertyDeclaredAfterTheInlinePropertyHasItsSegment_ThenBareKeyRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestLateShadowedInlineContainer(context) { Name = "child", Label = "label" };
        var root = new TestLateShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestLateShadowedInlineContainer> { ["CNC01"] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);
        var resolved = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal("CNC01.name", path);
        Assert.Same(GetNameProperty(child), resolved?.Property);
    }

    [Fact]
    public void WhenAPropertyDeclaredAfterTheInlinePropertyHasItsSegment_ThenKeyNeedingExplicitFormHasNoPath()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestLateShadowedInlineContainer(context) { Name = "child", Label = "label" };
        _ = new TestLateShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestLateShadowedInlineContainer> { ["a.b"] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenAPropertyDeclaredAfterTheInlinePropertyHasItsSegment_ThenInlinePropertyHasNoPath()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var root = new TestLateShadowedInlineContainer(context) { Name = "root", Label = "label" };
        var children = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestLateShadowedInlineContainer.Children))!;

        // Act
        var path = children.TryGetPath(pathProvider, null);
        var pathFromRoot = children.TryGetPath(pathProvider, root);

        // Assert
        Assert.Null(path);
        Assert.Null(pathFromRoot);
    }

    [Fact]
    public void WhenAnAttributeIsNamedLikeTheInlineProperty_ThenTheAttributeTakesTheSegment()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "child" };
        var root = new TestInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestInlineContainer> { ["CNC01"] = child }
        };
        var registered = root.TryGetRegisteredSubject()!;
        var attribute = GetNameProperty(root).AddAttribute<string>(nameof(TestInlineContainer.Children), _ => "value");
        var children = registered.TryGetProperty(nameof(TestInlineContainer.Children))!;

        // Act
        var attributePath = attribute.TryGetPath(DefaultPathProvider.Instance, null);
        var resolvedAttribute = DefaultPathProvider.Instance.TryGetPropertyFromPath(registered, attributePath!);
        var childrenPath = children.TryGetPath(DefaultPathProvider.Instance, null);
        var keyPath = GetNameProperty(child).TryGetPath(DefaultPathProvider.Instance, null);
        var resolvedKey = DefaultPathProvider.Instance.TryGetPropertyFromPath(registered, keyPath!);

        // Assert
        Assert.Equal("Children", attributePath);
        Assert.Same(attribute, resolvedAttribute?.Property);
        Assert.Null(childrenPath);
        Assert.Equal("CNC01.Name", keyPath);
        Assert.Same(GetNameProperty(child), resolvedKey?.Property);
    }

    [Fact]
    public void WhenInlinePropertySegmentIsNotShadowed_ThenInlinePropertyPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "child" };
        var root = new TestInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestInlineContainer> { ["CNC01"] = child }
        };
        var registered = root.TryGetRegisteredSubject()!;
        var rootChildren = registered.TryGetProperty(nameof(TestInlineContainer.Children))!;
        var childChildren = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(TestInlineContainer.Children))!;

        // Act
        var rootPath = rootChildren.TryGetPath(DefaultPathProvider.Instance, null);
        var resolvedRoot = DefaultPathProvider.Instance.TryGetPropertyFromPath(registered, rootPath!);
        var childPath = childChildren.TryGetPath(DefaultPathProvider.Instance, null);
        var resolvedChild = DefaultPathProvider.Instance.TryGetPropertyFromPath(registered, childPath!);

        // Assert
        Assert.Equal("Children", rootPath);
        Assert.Same(rootChildren, resolvedRoot?.Property);
        Assert.Null(resolvedRoot?.Index);
        Assert.Equal("CNC01.Children", childPath);
        Assert.Same(childChildren, resolvedChild?.Property);
        Assert.Null(resolvedChild?.Index);
    }

    [Fact]
    public void WhenInlinePropertyIsUnmappedForAttributeProvider_ThenInlinePropertyPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var root = new TestMappedInlineContainer(context) { Name = "root" };
        var registered = root.TryGetRegisteredSubject()!;
        var children = registered.TryGetProperty(nameof(TestMappedInlineContainer.Children))!;

        // Act
        var path = children.TryGetPath(pathProvider, null);
        var resolved = pathProvider.TryGetPropertyFromPath(registered, path!);

        // Assert
        Assert.Equal("Children", path);
        Assert.Same(children, resolved?.Property);
        Assert.Null(resolved?.Index);
    }

    [Fact]
    public void WhenInlinePropertyHasItsOwnSegment_ThenInlinePropertyPathRoundTrips()
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var root = new TestRenamedInlineContainer(context) { Name = "root" };
        var registered = root.TryGetRegisteredSubject()!;
        var children = registered.TryGetProperty(nameof(TestRenamedInlineContainer.Children))!;

        // Act
        var path = children.TryGetPath(pathProvider, null);
        var pathFromRoot = children.TryGetPath(pathProvider, root);
        var resolved = pathProvider.TryGetPropertyFromPath(registered, path!);

        // Assert
        Assert.Equal("kids", path);
        Assert.Equal("kids", pathFromRoot);
        Assert.Same(children, resolved?.Property);
        Assert.Null(resolved?.Index);
    }

    [Theory]
    [InlineData("k1", "k1.name")]
    [InlineData("a.b", "kids[a.b].name")]
    [InlineData("name", "kids[name].name")]
    public void WhenInlinePropertyHasItsOwnSegment_ThenKeyPathRoundTrips(string key, string expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestRenamedInlineContainer(context) { Name = "child" };
        var root = new TestRenamedInlineContainer(context)
        {
            Name = "root",
            Children = new Dictionary<string, TestRenamedInlineContainer> { [key] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);
        var resolved = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Same(GetNameProperty(child), resolved?.Property);
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
}
