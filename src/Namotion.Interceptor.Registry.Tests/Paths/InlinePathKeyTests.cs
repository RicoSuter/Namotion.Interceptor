using System.Globalization;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class InlinePathKeyTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

    [Theory]
    [InlineData("CNC01", "CNC01.Name")]
    [InlineData("192.168.0.1", "Children[192.168.0.1].Name")]
    [InlineData("Name", "Children[Name].Name")]
    [InlineData("Children", "Children[Children].Name")]
    [InlineData("a]b", "a]b.Name")]
    [InlineData("notes]v2", "notes]v2.Name")]
    [InlineData("a[b", "Children[a[b].Name")]
    public void WhenInlineKeyIsWritten_ThenPathRoundTrips(string key, string expectedPath)
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Child" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { [key] = child }
        };
        var nameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(DefaultPathProvider.Instance, root);
        var result = DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Same(child, result?.Property.Subject);
        Assert.Equal("Name", result?.Property.Name);
    }

    [Theory]
    [InlineData("Report [2024].md")]
    [InlineData("a.b]")]
    public void WhenInlineKeyNeedsExplicitFormAndContainsClosingBracket_ThenTryGetPathReturnsNull(string key)
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Child" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { [key] = child }
        };
        var nameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenWrongNameCarriesIndexOnInlineFallback_ThenPathDoesNotResolve()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Child" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["a"] = child }
        };
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act
        var wrongName = DefaultPathProvider.Instance.TryGetPropertyFromPath(registeredRoot, "Typo[a].Name");
        var rightName = DefaultPathProvider.Instance.TryGetPropertyFromPath(registeredRoot, "Children[a].Name");

        // Assert
        Assert.Null(wrongName);
        Assert.Same(child, rightName?.Property.Subject);
        Assert.Equal("Name", rightName?.Property.Name);
    }

    [Fact]
    public void WhenSegmentIsInlinePropertyName_ThenItResolvesToThePropertyItself()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["a"] = new TestInlineContainer(context) { Name = "Child" };

        // Act
        var result = DefaultPathProvider.Instance.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, "Children");

        // Assert
        Assert.Equal("Children", result?.Property.Name);
        Assert.Null(result?.Index);
    }

    [Theory]
    [InlineData("plain", "plain/name")]
    [InlineData("name", "Children[name]/name")]
    [InlineData("Children", "Children[Children]/name")]
    [InlineData("a/b", "Children[a/b]/name")]
    public void WhenInlinePropertyIsUnmapped_ThenAttributeProviderPathRoundTrips(string key, string expectedPath)
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("test", '/');
        var context = CreateContext();
        var child = new TestMappedInlineContainer(context) { Name = "Child" };
        var root = new TestMappedInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestMappedInlineContainer> { [key] = child }
        };
        var nameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(pathProvider, root);
        var result = pathProvider.TryGetPropertyFromPath(root.TryGetRegisteredSubject()!, path!);

        // Assert
        Assert.Equal(expectedPath, path);
        Assert.Same(child, result?.Property.Subject);
    }

    [Theory]
    [InlineData("plain", "plain/name")]
    [InlineData("a/b", null)]
    public void WhenInlinePropertySegmentIsShadowed_ThenKeyNeedingExplicitFormHasNoPath(string key, string? expectedPath)
    {
        // Arrange
        var pathProvider = new AttributeBasedPathProvider("test", '/');
        var context = CreateContext();
        var child = new TestShadowedInlineContainer(context) { Label = "", Name = "Child" };
        var root = new TestShadowedInlineContainer(context)
        {
            Label = "",
            Name = "Root",
            Children = new Dictionary<string, TestShadowedInlineContainer> { [key] = child }
        };
        var nameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(pathProvider, root);

        // Assert
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void WhenKeysAreFormatted_ThenTextIsInvariantAndEmptyTextHasNone()
    {
        // Arrange
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            // Act
            var number = PathExtensions.FormatPathIndex(1.5);
            var color = PathExtensions.FormatPathIndex(TestColor.Blue);
            var text = PathExtensions.FormatPathIndex("abc");
            var empty = PathExtensions.FormatPathIndex("");

            // Assert
            Assert.Equal("1.5", number);
            Assert.Equal("Blue", color);
            Assert.Equal("abc", text);
            Assert.Null(empty);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
