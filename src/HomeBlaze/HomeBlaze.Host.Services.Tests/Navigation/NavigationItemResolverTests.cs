using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using HomeBlaze.Storage.Abstractions.Metadata;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Host.Services.Tests.Navigation;

public class NavigationItemResolverTests
{
    private readonly IInterceptorSubjectContext _context;
    private readonly NavigationTestParent _root;
    private readonly SubjectComponentRegistry _componentRegistry;
    private readonly NavigationItemResolver _resolver;
    private readonly FakeTimeProvider _timeProvider = new();

    public NavigationItemResolverTests()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        _root = new NavigationTestParent(_context);

        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(NavigationItemResolverTests).Assembly);
        _componentRegistry = new SubjectComponentRegistry(typeProvider);

        _resolver = new NavigationItemResolver(
            _componentRegistry,
            new SubjectPathResolver(() => _root),
            _timeProvider);
    }

    // A resolver for a tree whose root is a folder, which has an order of its own, unlike the default root.
    private NavigationItemResolver CreateResolver(IInterceptorSubject root)
        => new(_componentRegistry, new SubjectPathResolver(() => root), _timeProvider);

    private NavigationTestFolder CreateFolder(
        Dictionary<string, IInterceptorSubject> children, ImmutableArray<string> childOrder = default)
        => new(_context)
        {
            Children = children,
            ChildOrder = childOrder.IsDefault ? [] : childOrder
        };

    private NavigationTestTopBarPage CreateTopBarPage(AppBarAlignment alignment = AppBarAlignment.Left)
        => new(_context) { AppBarAlignment = alignment };

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
    public void WhenParentHasChildOrder_ThenItemsFollowIt()
    {
        // Arrange
        var folder = new NavigationTestFolder(_context)
        {
            Children = new Dictionary<string, IInterceptorSubject>
            {
                ["a.md"] = new NavigationTestPage(_context),
                ["b.md"] = new NavigationTestPage(_context),
                ["c.md"] = new NavigationTestPage(_context)
            },
            ChildOrder = ["c.md", "a.md"]
        };
        _root.Folder = folder;

        // Act
        var items = _resolver.GetChildItems(folder).ToList();

        // Assert
        Assert.Equal(["/Folder/c.md", "/Folder/a.md", "/Folder/b.md"], items.Select(item => item.Path));
    }

    [Fact]
    public void WhenTopBarPageIsInNestedFolder_ThenAppBarItemsContainIt()
    {
        // Arrange
        var claude = CreateTopBarPage();
        var root = CreateFolder(new()
        {
            ["Agents"] = CreateFolder(new() { ["Claude"] = claude }),
            ["Readme.md"] = new NavigationTestPage(_context)
        });
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetAppBarItems(root);

        // Assert
        var item = Assert.Single(items);
        Assert.Same(claude, item.Subject);
        Assert.Equal("/Agents/Claude", item.Path);
    }

    [Fact]
    public void WhenRecentAppBarItemsAreReadAgainWithinTheMaximumAge_ThenTheTreeIsWalkedOnce()
    {
        // Arrange
        var maximumAge = TimeSpan.FromSeconds(5);
        var root = CreateFolder(new() { ["Alpha"] = CreateTopBarPage() });
        var resolver = CreateResolver(root);
        var first = resolver.GetRecentAppBarItems(root, maximumAge);
        root.Children = new() { ["Alpha"] = root.Children["Alpha"], ["Beta"] = CreateTopBarPage() };

        // Act
        var withinMaximumAge = resolver.GetRecentAppBarItems(root, maximumAge);
        _timeProvider.Advance(maximumAge);
        var afterMaximumAge = resolver.GetRecentAppBarItems(root, maximumAge);

        // Assert
        Assert.Equal(["/Alpha"], first.Select(item => item.Path));
        Assert.Equal(["/Alpha"], withinMaximumAge.Select(item => item.Path));
        Assert.Equal(["/Alpha", "/Beta"], afterMaximumAge.Select(item => item.Path));
    }

    [Fact]
    public void WhenTheAppBarItemsAreWalkedAgainWithoutAChange_ThenTheSameArrayIsReturned()
    {
        // Arrange
        var maximumAge = TimeSpan.FromSeconds(5);
        var root = CreateFolder(new() { ["Alpha"] = CreateTopBarPage() });
        var resolver = CreateResolver(root);
        var first = resolver.GetRecentAppBarItems(root, maximumAge);
        _timeProvider.Advance(maximumAge);

        // Act
        var unchanged = resolver.GetRecentAppBarItems(root, maximumAge);
        root.Children = new() { ["Alpha"] = root.Children["Alpha"], ["Beta"] = CreateTopBarPage() };
        _timeProvider.Advance(maximumAge);
        var changed = resolver.GetRecentAppBarItems(root, maximumAge);

        // Assert
        Assert.True(first == unchanged);
        Assert.False(first == changed);
    }

    [Fact]
    public void WhenTopBarPagesAreOnSeveralLevels_ThenAppBarItemsFollowNavigationOrder()
    {
        // Arrange
        var root = CreateFolder(
            new()
            {
                ["Alpha"] = CreateTopBarPage(),
                ["Folder"] = CreateFolder(
                    new()
                    {
                        ["X"] = CreateTopBarPage(),
                        ["Y"] = CreateTopBarPage(),
                        ["Page.md"] = new NavigationTestPage(_context)
                    },
                    ["Y", "X"]),
                ["Zulu"] = CreateTopBarPage()
            },
            ["Zulu", "Folder"]);
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetAppBarItems(root);

        // Assert
        Assert.Equal(["/Zulu", "/Folder/Y", "/Folder/X", "/Alpha"], items.Select(item => item.Path));
    }

    [Fact]
    public void WhenTopBarPagesAreAlignedLeftAndRight_ThenEachAlignmentKeepsNavigationOrder()
    {
        // Arrange
        var root = CreateFolder(
            new()
            {
                ["A"] = CreateTopBarPage(AppBarAlignment.Right),
                ["B"] = CreateTopBarPage(),
                ["Folder"] = CreateFolder(new()
                {
                    ["C"] = CreateTopBarPage(AppBarAlignment.Right),
                    ["D"] = CreateTopBarPage()
                }),
                ["E"] = CreateTopBarPage(AppBarAlignment.Right),
                ["F"] = CreateTopBarPage()
            },
            ["A", "B", "Folder", "E", "F"]);
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetAppBarItems(root);

        // Assert
        Assert.Equal(
            ["/B", "/Folder/D", "/F"],
            items.Where(item => item.Alignment == AppBarAlignment.Left).Select(item => item.Path));
        Assert.Equal(
            ["/A", "/Folder/C", "/E"],
            items.Where(item => item.Alignment == AppBarAlignment.Right).Select(item => item.Path));
    }

    [Fact]
    public void WhenFoldersReferenceEachOther_ThenAppBarItemsListEachPageOnce()
    {
        // Arrange
        var page = CreateTopBarPage();
        var first = CreateFolder(new() { ["Page"] = page });
        var root = CreateFolder(new() { ["First"] = first });
        var second = CreateFolder(new() { ["First"] = first });
        first.Children = new Dictionary<string, IInterceptorSubject>(first.Children) { ["Second"] = second };
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetAppBarItems(root);

        // Assert
        var item = Assert.Single(items);
        Assert.Same(page, item.Subject);
    }

    [Fact]
    public void WhenTopBarPageIsInTwoFolders_ThenAppBarItemsListItAtItsFirstPlace()
    {
        // Arrange
        var shared = CreateTopBarPage();
        var root = CreateFolder(
            new()
            {
                ["First"] = CreateFolder(new() { ["Shared"] = shared }),
                ["Own"] = CreateTopBarPage(),
                ["Second"] = CreateFolder(new() { ["Shared"] = shared })
            },
            ["First", "Own", "Second"]);
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetAppBarItems(root);

        // Assert
        Assert.Equal(2, items.Count);
        Assert.Same(shared, items[0].Subject);
        Assert.Equal("/Own", items[1].Path);
    }

    [Fact]
    public void WhenFolderHasOnlyTopBarPages_ThenItIsNoNavigationBarFolder()
    {
        // Arrange
        var root = CreateFolder(new()
        {
            ["Agents"] = CreateFolder(new() { ["Claude"] = CreateTopBarPage() }),
            ["Docs"] = CreateFolder(new() { ["Readme.md"] = new NavigationTestPage(_context) })
        });
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetChildItems(root).ToDictionary(item => item.Path);

        // Assert
        Assert.True(items["/Agents"].IsFolder);
        Assert.False(items["/Agents"].HasNavigationBarPages);
        Assert.True(items["/Docs"].IsFolder);
        Assert.True(items["/Docs"].HasNavigationBarPages);
    }

    [Fact]
    public void WhenFolderHasNavigationBarPageBelowTopBarPage_ThenItIsNavigationBarFolder()
    {
        // Arrange
        var root = CreateFolder(new()
        {
            ["Agents"] = CreateFolder(new()
            {
                ["Claude"] = CreateTopBarPage(),
                ["Notes"] = CreateFolder(new() { ["Readme.md"] = new NavigationTestPage(_context) })
            })
        });
        var resolver = CreateResolver(root);

        // Act
        var item = Assert.Single(resolver.GetChildItems(root));

        // Assert
        Assert.True(item.HasNavigationBarPages);
    }

    [Fact]
    public void WhenSidebarFolderIsAlsoBelowTopBarFolder_ThenItsSidebarPageCounts()
    {
        // Arrange
        var shared = CreateFolder(new() { ["Readme.md"] = new NavigationTestPage(_context) });
        var topBarFolder = CreateFolder(new() { ["Shared"] = shared });
        var metadata = JsonElement.Parse("""{ "location": "AppBar" }""").EnumerateObject()
            .ToImmutableDictionary(property => property.Name, property => property.Value);
        topBarFolder.TryGetRegisteredSubject()!.AddProperty(KnownAttributes.Metadata, metadata.GetType(), _ => metadata, null);

        var root = CreateFolder(new()
        {
            ["Agents"] = CreateFolder(new()
            {
                ["Hidden"] = topBarFolder,
                ["Shared"] = shared
            })
        });
        var resolver = CreateResolver(root);

        // Act
        var item = Assert.Single(resolver.GetChildItems(root));

        // Assert
        Assert.True(item.HasNavigationBarPages);
    }

    [Fact]
    public void WhenSidebarPageIsOnlyBelowTopBarPage_ThenFolderIsNoNavigationBarFolder()
    {
        // Arrange
        var topBarPageWithChildren = new NavigationTestTopBarFolderPage(_context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Readme.md"] = new NavigationTestPage(_context) }
        };
        var root = CreateFolder(new()
        {
            ["Agents"] = CreateFolder(new() { ["Claude"] = topBarPageWithChildren })
        });
        var resolver = CreateResolver(root);

        // Act
        var item = Assert.Single(resolver.GetChildItems(root));

        // Assert
        Assert.True(item.IsFolder);
        Assert.False(item.HasNavigationBarPages);
    }

    [Fact]
    public void WhenAChildIsExcludedFromTheUserInterface_ThenNavigationLeavesItOutWithWhatItHolds()
    {
        // Arrange
        var root = CreateFolder(new()
        {
            ["Hidden"] = new NavigationTestHiddenPage(_context),
            ["Folder"] = CreateFolder(new() { ["Hidden"] = new NavigationTestHiddenPage(_context) }),
            ["Shown"] = new NavigationTestPage(_context)
        });
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetChildItems(root);

        // Assert
        Assert.Equal(["Shown"], items.Select(item => item.Title));
    }

    [Fact]
    public void WhenPagesAreInTheAppBarMenu_ThenTheirItemsHaveTheMenuAlignmentAndFollowNavigationOrder()
    {
        // Arrange
        var root = CreateFolder(
            new()
            {
                ["a.md"] = new NavigationTestMenuPage(_context) { NavigationTitle = "Alpha" },
                ["b.md"] = new NavigationTestMenuPage(_context) { NavigationTitle = "Bravo" },
                ["c.md"] = new NavigationTestMenuPage(_context) { NavigationTitle = "Charlie" },
                ["r.md"] = new NavigationTestMenuPage(_context) { NavigationTitle = "Right", AppBarAlignment = AppBarAlignment.Right }
            },
            ["r.md", "c.md"]);
        var resolver = CreateResolver(root);

        // Act
        var items = resolver.GetAppBarItems(root)
            .Where(item => item.Alignment == AppBarAlignment.Menu)
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

    public partial NavigationTestFolder? Folder { get; set; }
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
public partial class NavigationTestFolder : IMetadataContainer
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public partial ImmutableArray<string> ChildOrder { get; set; }

    public NavigationTestFolder()
    {
        Children = new();
        ChildOrder = [];
    }
}

[InterceptorSubject]
public partial class NavigationTestTopBarPage : IPage
{
    public string? NavigationTitle => null;

    public string? NavigationIconName => null;

    public NavigationLocation PageLocation => NavigationLocation.AppBar;

    public AppBarAlignment AppBarAlignment { get; init; }
}

[SubjectComponent(SubjectComponentType.Page, typeof(NavigationTestTopBarPage))]
public class NavigationTestTopBarPageComponent
{
}

[InterceptorSubject]
public partial class NavigationTestTopBarFolderPage : IPage
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public string? NavigationTitle => null;

    public string? NavigationIconName => null;

    public NavigationLocation PageLocation => NavigationLocation.AppBar;

    public AppBarAlignment AppBarAlignment => AppBarAlignment.Left;

    public NavigationTestTopBarFolderPage()
    {
        Children = new();
    }
}

[SubjectComponent(SubjectComponentType.Page, typeof(NavigationTestTopBarFolderPage))]
public class NavigationTestTopBarFolderPageComponent
{
}

[InterceptorSubject]
[ExcludeFrom(ExclusionTarget.UserInterface)]
public partial class NavigationTestHiddenPage
{
}

[SubjectComponent(SubjectComponentType.Page, typeof(NavigationTestHiddenPage))]
public class NavigationTestHiddenPageComponent
{
}

[InterceptorSubject]
public partial class NavigationTestMenuPage : IPage
{
    public string? NavigationTitle { get; init; }

    public string? NavigationIconName => "Lightbulb";

    public NavigationLocation PageLocation => NavigationLocation.AppBar;

    public AppBarAlignment AppBarAlignment { get; init; } = AppBarAlignment.Menu;
}

[SubjectComponent(SubjectComponentType.Page, typeof(NavigationTestMenuPage))]
public class NavigationTestMenuPageComponent
{
}
