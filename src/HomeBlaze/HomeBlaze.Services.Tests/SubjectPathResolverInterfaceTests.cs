using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Models;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// Tests that SubjectPathResolver is consumable through the ISubjectPathResolver seam.
/// </summary>
public class SubjectPathResolverInterfaceTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenUsedThroughInterface_ThenAllMembersDispatchToConcreteResolver()
    {
        // Arrange
        var root = new TestContainer(Context) { Name = "Root" };
        RootManager.Root = root;
        ISubjectPathResolver seam = Resolver;

        // Act
        var path = seam.GetPath(root, PathStyle.Canonical);
        var paths = seam.GetPaths(root, PathStyle.Canonical);
        var resolved = seam.ResolveSubject("/", PathStyle.Canonical);

        // Assert
        Assert.Equal("/", path);
        Assert.Equal(new[] { "/" }, paths);
        Assert.Same(root, resolved);
    }
}
