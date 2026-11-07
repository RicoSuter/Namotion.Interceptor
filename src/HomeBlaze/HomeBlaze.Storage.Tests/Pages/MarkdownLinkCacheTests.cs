using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownLinkCacheTests
{
    private static async Task<(MarkdownPageFixture Fixture, MarkdownFile Page)> AddPageAsync(string pagePath = "Docs/page.md")
    {
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(pagePath, "# Page");
        return (fixture, page);
    }

    [Theory]
    [InlineData("plan.png")]
    [InlineData("other.md")]
    [InlineData("sub/")]
    [InlineData("#section")]
    public async Task WhenALinkIsResolvedAgain_ThenTheSameStringIsReturnedWithoutAllocating(string link)
    {
        // Arrange
        var (_, page) = await AddPageAsync();
        var linkScope = (ILinkScope)page;
        var first = linkScope.ResolveLink(link);
        linkScope.ResolveLink(link);

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var second = linkScope.ResolveLink(link);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Same(first, second);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task WhenThePathsChange_ThenTheLinkIsResolvedAgain()
    {
        // Arrange
        var (fixture, page) = await AddPageAsync();
        var linkScope = (ILinkScope)page;
        var before = linkScope.ResolveLink("plan.png");

        // Act
        await fixture.AddPageAsync("other.md", "# Other");
        var after = linkScope.ResolveLink("plan.png");

        // Assert
        Assert.Equal("/files/Docs/plan.png", after);
        Assert.Equal(before, after);
        Assert.NotSame(before, after);
    }

    [Fact]
    public async Task WhenMoreLinksAreResolvedThanTheCacheHolds_ThenOnlyTheFirstOnesAreKept()
    {
        // Arrange
        var (_, page) = await AddPageAsync();
        var linkScope = (ILinkScope)page;
        var links = Enumerable.Range(0, MarkdownContentParser.MaximumCachedLinks + 8).Select(index => $"file{index}.png").ToArray();
        var firstResults = links.Select(linkScope.ResolveLink).ToArray();

        // Act
        var secondResults = links.Select(linkScope.ResolveLink).ToArray();

        // Assert
        Assert.Same(firstResults[0], secondResults[0]);
        Assert.Equal(firstResults[^1], secondResults[^1]);
        Assert.NotSame(firstResults[^1], secondResults[^1]);
        Assert.Equal(MarkdownContentParser.MaximumCachedLinks, page.ResolvedLinks!.Count);
    }

    [Fact]
    public async Task WhenALinkStaysAsWritten_ThenNothingIsCached()
    {
        // Arrange
        var (_, page) = await AddPageAsync();

        // Act
        var link = ((ILinkScope)page).ResolveLink("https://example.com/plan.png");

        // Assert
        Assert.Equal("https://example.com/plan.png", link);
        Assert.Null(page.ResolvedLinks);
    }
}
