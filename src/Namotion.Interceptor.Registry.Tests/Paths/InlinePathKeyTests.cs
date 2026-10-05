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

    [Fact]
    public void WhenSegmentIsInlinePropertyName_ThenItResolvesToThePropertyItself()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "root" };

        // Act
        var resolved = DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, "Children");

        // Assert
        Assert.Equal(nameof(TestInlineContainer.Children), resolved?.Property.Name);
        Assert.Null(resolved?.Index);
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

    [Theory]
    [InlineData("CNC01", "CNC01.name")]
    [InlineData("a.b", null)]
    public void WhenInlinePropertySegmentIsShadowed_ThenKeyNeedingExplicitFormHasNoPath(string key, string? expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var pathProvider = new AttributeBasedPathProvider("test");
        var child = new TestShadowedInlineContainer(context) { Name = "child", Label = "label" };
        _ = new TestShadowedInlineContainer(context)
        {
            Name = "root",
            Label = "label",
            Children = new Dictionary<string, TestShadowedInlineContainer> { [key] = child }
        };

        // Act
        var path = GetNameProperty(child).TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal(expectedPath, path);
    }
}
