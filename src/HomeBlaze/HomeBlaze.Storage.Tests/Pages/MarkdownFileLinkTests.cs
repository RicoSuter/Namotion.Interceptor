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
    [InlineData("[Other host](//example.com/plan.png)", "href=\"//example.com/plan.png\"")]
    [InlineData("[Upper](HTTP://example.com/plan.png)", "href=\"HTTP://example.com/plan.png\"")]
    public async Task WhenLinkHasASchemeOrStartsWithASlash_ThenItStaysAsWritten(string source, string expected)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("Docs/page.md", source);

        // Assert
        Assert.Contains(expected, MarkdownPageFixture.Html(page.Children));
    }

    [Theory]
    [InlineData("Readme.md", "[Demo](Demo/)", "<a href=\"/browser/Demo\">Demo</a>")]
    [InlineData("Docs/home.md", "[Design](architecture/design/)", "<a href=\"/browser/Docs/architecture/design\">Design</a>")]
    [InlineData("Docs/architecture/overview.md", "[Up](../)", "<a href=\"/browser/Docs\">Up</a>")]
    public async Task WhenLinkIsARelativeFolder_ThenItOpensTheFolderInTheBrowserInTheSameTab(string pagePath, string source, string expected)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(pagePath, source);

        // Assert
        Assert.Contains(expected, MarkdownPageFixture.Html(page.Children));
    }

    [Theory]
    [InlineData("[Manual](manual.pdf)", "<a href=\"/files/Docs/manual.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">Manual</a>")]
    [InlineData("[Web](https://example.com)", "<a href=\"https://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">Web</a>")]
    [InlineData("[Other](other.md)", "<a href=\"/pages/Docs/other.md\">Other</a>")]
    [InlineData("[Section](#a)", "<a href=\"/pages/Docs/page.md#a\">Section</a>")]
    [InlineData("[App](/browser/Demo)", "<a href=\"/browser/Demo\">App</a>")]
    [InlineData("[Manual](/files/Docs/manual.pdf)", "<a href=\"/files/Docs/manual.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">Manual</a>")]
    [InlineData("[Notes](notes.markdown)", "<a href=\"/pages/Docs/notes.markdown\">Notes</a>")]
    public async Task WhenPageHasALink_ThenOnlyFileAndExternalLinksOpenInANewTab(string source, string expected)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("Docs/page.md", source);

        // Assert
        Assert.Contains(expected, MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenPageHasAPictureFromTheStorage_ThenThePictureHasNoTarget()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("Docs/page.md", "![Plan](plan.png)");

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("src=\"/files/Docs/plan.png\"", html);
        Assert.DoesNotContain("target", html);
    }
}
