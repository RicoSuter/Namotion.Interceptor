using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownFileLinkTests
{
    [Theory]
    [InlineData("page.md", "![Plan](plan.png)", "src=\"/files/plan.png\"")]
    [InlineData("Docs/page.md", "![Plan](images/plan.png)", "src=\"/files/Docs/images/plan.png\"")]
    [InlineData("Docs/sub/page.md", "![Plan](../images/plan.png)", "src=\"/files/Docs/images/plan.png\"")]
    [InlineData("Docs/page.md", "![Plan](./plan.png)", "src=\"/files/Docs/plan.png\"")]
    [InlineData("Docs/page.md", "[Manual](manual.pdf#page=2)", "href=\"/files/Docs/manual.pdf#page=2\"")]
    [InlineData("Docs/page.md", "[Export](export.csv?download=1)", "href=\"/files/Docs/export.csv?download=1\"")]
    public async Task WhenLinkIsRelativeAndNotAPage_ThenItLeadsToTheFileRoute(string pagePath, string source, string expected)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(pagePath, source);

        // Assert
        Assert.Contains(expected, MarkdownPageFixture.Html(page.Children));
    }

    [Theory]
    [InlineData("[Web](https://example.com/plan.png)", "href=\"https://example.com/plan.png\"")]
    [InlineData("[Mail](mailto:someone@example.com)", "href=\"mailto:someone@example.com\"")]
    [InlineData("[Phone](tel:+41000000000)", "href=\"tel:+41000000000\"")]
    [InlineData("[App](/browser/Demo)", "href=\"/browser/Demo\"")]
    [InlineData("![Inline](data:image/png;base64,AAAA)", "src=\"data:image/png;base64,AAAA\"")]
    public async Task WhenLinkHasASchemeOrStartsWithASlash_ThenItStaysAsWritten(string source, string expected)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("Docs/page.md", source);

        // Assert
        Assert.Contains(expected, MarkdownPageFixture.Html(page.Children));
    }
}
