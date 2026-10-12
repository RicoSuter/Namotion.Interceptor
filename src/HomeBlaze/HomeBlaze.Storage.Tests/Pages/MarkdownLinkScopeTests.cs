using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownLinkScopeTests
{
    private static readonly string Namespace = typeof(TestLeaf).Namespace!;

    private static async Task<(MarkdownPageFixture Fixture, IInterceptorSubject Leaf, string Html)> AddPageAsync()
    {
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            [Other](other.md)

            :: {{{Namespace}}}.TestLeaf#leaf Count=1
            ::
            """);

        var leaf = ((IPathScope)page).TryGetNamedSubject("leaf")!;
        return (fixture, leaf, MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenLinkIsRelativeToThePage_ThenItResolvesLikeALinkInTheText()
    {
        // Arrange
        var (_, leaf, html) = await AddPageAsync();

        // Act
        var link = leaf.ResolveScopedLink("other.md");

        // Assert
        Assert.NotNull(link);
        Assert.StartsWith("/pages/", link);
        Assert.Contains($"href=\"{link}\"", html);
    }

    [Theory]
    [InlineData("/pages/Demo/Blocks.md")]
    [InlineData("https://example.com/a.md")]
    [InlineData("picture.png")]
    public async Task WhenLinkNeedsNoResolving_ThenItIsReturnedAsWritten(string written)
    {
        // Arrange
        var (_, leaf, _) = await AddPageAsync();

        // Act
        var link = leaf.ResolveScopedLink(written);

        // Assert
        Assert.Equal(written, link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WhenLinkIsMissing_ThenItIsReturnedAsGiven(string? written)
    {
        // Arrange
        var (_, leaf, _) = await AddPageAsync();

        // Act
        var link = leaf.ResolveScopedLink(written);

        // Assert
        Assert.Equal(written, link);
    }

    [Fact]
    public async Task WhenSubjectIsInNoLinkScope_ThenLinkIsReturnedAsWritten()
    {
        // Arrange
        var (fixture, _, _) = await AddPageAsync();

        // Act
        var link = fixture.Storage.ResolveScopedLink("other.md");

        // Assert
        Assert.Equal("other.md", link);
    }
}
