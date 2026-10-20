using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class MetadataLinkResolverTests
{
    private static MetadataLinkResolver CreateResolver() => CreateResolver(out _);

    private static MetadataLinkResolver CreateResolver(out MetadataLinkTestFolder root)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithService<IPropertyLifecycleHandler>(() => new PropertyAttributeInitializer(), handler => handler is PropertyAttributeInitializer);
        root = new MetadataLinkTestFolder(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Readme.md"] = new NavigationTestPage(context),
                ["Devices"] = new MetadataLinkTestFolder(context)
            }
        };

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(MetadataLinkResolverTests).Assembly);
        var shownRoot = root;
        return new MetadataLinkResolver(new SubjectPathResolver(() => shownRoot), new SubjectComponentRegistry(typeProvider));
    }

    [Fact]
    public void WhenALinkHasAUrl_ThenItOpensTheUrlInANewTab()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink(null, "https://example.com/pump.pdf", null));

        // Assert
        Assert.Equal(new ResolvedMetadataLink("https://example.com/pump.pdf", "https://example.com/pump.pdf", IsExternal: true), link);
    }

    [Fact]
    public void WhenALinkHasAMailtoUrl_ThenItOpensInPlaceRatherThanInANewTab()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink(null, "mailto:service@example.com", null));

        // Assert
        Assert.Equal(new ResolvedMetadataLink("mailto:service@example.com", "mailto:service@example.com", IsExternal: false), link);
    }

    [Fact]
    public void WhenAUrlIsNoAllowedAddress_ThenTheLinkIsBroken()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink("Manual", "javascript:alert(1)", null));

        // Assert
        Assert.True(link.IsBroken);
        Assert.False(link.IsExternal);
        Assert.Equal("Manual", link.Title);
    }

    [Fact]
    public void WhenAPathLeadsToAPage_ThenTheLinkOpensThePage()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink("Read me", null, "/Readme.md"));

        // Assert
        Assert.Equal(new ResolvedMetadataLink("Read me", "pages/Readme.md", IsExternal: false), link);
    }

    [Fact]
    public void WhenAPathLeadsToASubject_ThenTheLinkOpensItInTheBrowser()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink(null, null, "/Devices"));

        // Assert
        Assert.Equal("/browser/Devices", link.Href);
        Assert.Equal(nameof(MetadataLinkTestFolder), link.Title);
    }

    [Fact]
    public void WhenAPathLeadsToAProperty_ThenTheLinkOpensItsSubjectWithItsRowHighlighted()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink(null, null, "/Devices/Name"));

        // Assert
        Assert.Equal(new ResolvedMetadataLink($"{nameof(MetadataLinkTestFolder)} Name", "/browser/Devices?property=Name", IsExternal: false), link);
    }

    [Fact]
    public void WhenAPathLeadsToAPropertyOfTheRoot_ThenTheLinkOpensTheRootWithItsRowHighlighted()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink("Name", null, "/Name"));

        // Assert
        Assert.Equal("/browser?property=Name", link.Href);
    }

    [Fact]
    public void WhenAPathLeadsToNothing_ThenTheLinkIsBroken()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink(null, null, "/Gone/Away"));

        // Assert
        Assert.True(link.IsBroken);
        Assert.Equal("/Gone/Away", link.Title);
    }

    [Theory]
    [InlineData("/Devices/Internal")]
    [InlineData("/Devices/Children")]
    public void WhenAPathLeadsToAPropertyThatTheBrowserDoesNotShow_ThenTheLinkIsBroken(string path)
    {
        // Arrange
        var resolver = CreateResolver(out var root);

        // Act
        var link = resolver.Resolve(new MetadataLink(null, null, path));
        var propertyLink = resolver.Resolve(root.Children["Devices"], path.Split('/')[^1]);

        // Assert
        Assert.True(link.IsBroken);
        Assert.True(propertyLink.IsBroken);
    }
}

[InterceptorSubject]
public partial class MetadataLinkTestFolder
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    [State]
    public partial string? Name { get; set; }

    public partial string? Internal { get; set; }

    public MetadataLinkTestFolder()
    {
        Children = new();
    }
}
