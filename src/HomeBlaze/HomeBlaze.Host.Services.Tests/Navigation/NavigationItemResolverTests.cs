using System.Collections.Immutable;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
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

    public bool HasOwnMetadata => true;

    public FolderMetadata? Metadata => null;

    public NavigationTestFolder()
    {
        Children = new();
        ChildOrder = [];
    }

    public Task WriteMetadataAsync(FolderMetadata? metadata, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WriteChildMetadataAsync(IInterceptorSubject child, SubjectMetadata? metadata, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task MoveChildAsync(string name, string targetName, CancellationToken cancellationToken) => Task.CompletedTask;
}
