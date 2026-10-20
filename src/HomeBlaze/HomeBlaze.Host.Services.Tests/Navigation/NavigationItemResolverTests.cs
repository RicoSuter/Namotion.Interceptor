using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class NavigationItemResolverTests
{
    private readonly IInterceptorSubjectContext _context;
    private readonly NavigationTestParent _root;
    private readonly NavigationItemResolver _resolver;

    public NavigationItemResolverTests()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _root = new NavigationTestParent(_context);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(NavigationItemResolverTests).Assembly);

        _resolver = new NavigationItemResolver(
            new SubjectComponentRegistry(typeProvider),
            new SubjectPathResolver(() => _root));
    }

    [Fact]
    public void WhenPagesAreReferencedFromTwoProperties_ThenChildItemsContainBoth()
    {
        // Arrange
        _root.First = new NavigationTestPage(_context);
        _root.Second = new NavigationTestPage(_context);

        // Act
        var items = _resolver.GetChildItems(_root).ToList();

        // Assert
        Assert.Equal(["/First", "/Second"], items.Select(item => item.Path).Order());
        Assert.All(items, item => Assert.True(item.IsPage));
    }

    [Fact]
    public void WhenSamePageIsReferencedFromTwoProperties_ThenChildItemsContainItOnce()
    {
        // Arrange
        var page = new NavigationTestPage(_context);
        _root.First = page;
        _root.Second = page;

        // Act
        var items = _resolver.GetChildItems(_root).ToList();

        // Assert
        var item = Assert.Single(items);
        Assert.Same(page, item.Subject);
    }

    [Fact]
    public void WhenPagesAreInTheAppBarMenu_ThenTheirItemsHaveTheMenuAlignmentAndAreSortedByPositionThenTitle()
    {
        // Arrange
        _root.MenuPages = new Dictionary<string, NavigationTestMenuPage>
        {
            ["c.md"] = new(_context) { NavigationTitle = "Charlie", PagePosition = 10 },
            ["b.md"] = new(_context) { NavigationTitle = "Bravo", PagePosition = 20 },
            ["a.md"] = new(_context) { NavigationTitle = "Alpha", PagePosition = 20 },
            ["r.md"] = new(_context) { NavigationTitle = "Right", PagePosition = 1, AppBarAlignment = AppBarAlignment.Right }
        };

        // Act
        var items = _resolver.GetChildItems(_root)
            .Where(item => item.Location == NavigationLocation.AppBar && item.Alignment == AppBarAlignment.Menu)
            .ToList();

        // Assert
        Assert.Equal(["Charlie", "Alpha", "Bravo"], items.Select(item => item.Title));
        Assert.All(items, item => Assert.Equal("Lightbulb", item.Icon));
    }
}

[InterceptorSubject]
public partial class NavigationTestParent
{
    public partial NavigationTestPage? First { get; set; }

    public partial NavigationTestPage? Second { get; set; }

    public partial Dictionary<string, NavigationTestMenuPage>? MenuPages { get; set; }
}

[InterceptorSubject]
public partial class NavigationTestPage
{
}

[SubjectComponent(SubjectComponentType.Page, typeof(NavigationTestPage))]
public class NavigationTestPageComponent
{
}

[InterceptorSubject]
public partial class NavigationTestMenuPage : IPage
{
    public string? NavigationTitle { get; init; }

    public string? NavigationIconName => "Lightbulb";

    public int? PagePosition { get; init; }

    public NavigationLocation PageLocation => NavigationLocation.AppBar;

    public AppBarAlignment AppBarAlignment { get; init; } = AppBarAlignment.Menu;
}

[SubjectComponent(SubjectComponentType.Page, typeof(NavigationTestMenuPage))]
public class NavigationTestMenuPageComponent
{
}
