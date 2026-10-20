using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class MetadataLinkResolverTests
{
    private static MetadataLinkResolver CreateResolver() => CreateResolver(out _);

    private static MetadataLinkResolver CreateResolver(out NavigationTestFolder root)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        root = new NavigationTestFolder(context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["Readme.md"] = new NavigationTestPage(context),
                ["Devices"] = new NavigationTestFolder(context)
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

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("example.com/pump.pdf")]
    public void WhenAUrlIsNoAllowedAddress_ThenTheLinkIsBroken(string url)
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink("Manual", url, null));

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
        Assert.Equal(nameof(NavigationTestFolder), link.Title);
    }

    [Fact]
    public void WhenAPathLeadsToAProperty_ThenTheLinkOpensItsSubjectWithItsRowHighlighted()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink("Order", null, "/Devices/ChildOrder"));

        // Assert
        Assert.Equal(new ResolvedMetadataLink("Order", "/browser/Devices?property=ChildOrder", IsExternal: false), link);
    }

    [Fact]
    public void WhenAPathLeadsToAPropertyOfTheRoot_ThenTheLinkOpensTheRootWithItsRowHighlighted()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act
        var link = resolver.Resolve(new MetadataLink("Order", null, "/ChildOrder"));

        // Assert
        Assert.Equal("/browser?property=ChildOrder", link.Href);
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

    [Fact]
    public void WhenASubjectAndAPropertyAreGiven_ThenTheLinkOpensTheSubjectWithTheRowOfThePropertyHighlighted()
    {
        // Arrange
        var resolver = CreateResolver(out var root);
        var devices = root.Children["Devices"];

        // Act
        var subjectLink = resolver.Resolve(devices, null);
        var propertyLink = resolver.Resolve(devices, "ChildOrder");

        // Assert
        Assert.Equal(new ResolvedMetadataLink(nameof(NavigationTestFolder), "/browser/Devices", IsExternal: false), subjectLink);
        Assert.Equal(new ResolvedMetadataLink($"{nameof(NavigationTestFolder)} ChildOrder", "/browser/Devices?property=ChildOrder", IsExternal: false), propertyLink);
    }
}
