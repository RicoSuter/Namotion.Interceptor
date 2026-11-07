using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownFileLinkTests
{
    private static async Task<string> RenderAsync(string pagePath, string source)
    {
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(pagePath, source);
        return MarkdownPageFixture.Html(page.Children);
    }

    [Theory]
    [InlineData("page.md", "![Plan](plan.png)", "src=\"/files/plan.png\"")]
    [InlineData("Docs/page.md", "![Plan](images/plan.png)", "src=\"/files/Docs/images/plan.png\"")]
    [InlineData("Docs/sub/page.md", "![Plan](../images/plan.png)", "src=\"/files/Docs/images/plan.png\"")]
    [InlineData("Docs/page.md", "![Plan](./plan.png)", "src=\"/files/Docs/plan.png\"")]
    [InlineData("Docs/page.md", "[Manual](manual.pdf#page=2)", "href=\"/files/Docs/manual.pdf#page=2\"")]
    [InlineData("Docs/page.md", "[Export](export.csv?download=1)", "href=\"/files/Docs/export.csv?download=1\"")]
    public async Task WhenLinkIsRelativeAndNotAPage_ThenItLeadsToTheFileRoute(string pagePath, string source, string expected)
    {
        // Act
        var html = await RenderAsync(pagePath, source);

        // Assert
        Assert.Contains(expected, html);
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
        // Act
        var html = await RenderAsync("Docs/page.md", source);

        // Assert
        Assert.Contains(expected, html);
    }

    [Theory]
    [InlineData("Readme.md", "[Demo](Demo/)", "<a href=\"/browser/Demo\">Demo</a>")]
    [InlineData("Docs/home.md", "[Design](architecture/design/)", "<a href=\"/browser/Docs/architecture/design\">Design</a>")]
    [InlineData("Docs/architecture/overview.md", "[Up](../)", "<a href=\"/browser/Docs\">Up</a>")]
    [InlineData("Docs/sub/page.md", "[Up](..)", "<a href=\"/browser/Docs\">Up</a>")]
    [InlineData("Docs/page.md", "[Here](.)", "<a href=\"/browser/Docs\">Here</a>")]
    [InlineData("Docs/a/b/page.md", "[Up](../..)", "<a href=\"/browser/Docs\">Up</a>")]
    [InlineData("Docs/sub/page.md", "[Here](./.)", "<a href=\"/browser/Docs/sub\">Here</a>")]
    [InlineData("Docs/sub/page.md", "[Up](..?view=list)", "<a href=\"/browser/Docs?view=list\">Up</a>")]
    public async Task WhenLinkIsARelativeFolder_ThenItOpensTheFolderInTheBrowserInTheSameTab(string pagePath, string source, string expected)
    {
        // Act
        var html = await RenderAsync(pagePath, source);

        // Assert
        Assert.Contains(expected, html);
    }

    [Theory]
    [InlineData("[Manual](manual.pdf)", "<a href=\"/files/Docs/manual.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">Manual</a>")]
    [InlineData("[Web](https://example.com)", "<a href=\"https://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">Web</a>")]
    [InlineData("[Other](other.md)", "<a href=\"/pages/Docs/other.md\">Other</a>")]
    [InlineData("[Section](#a)", "<a href=\"/pages/Docs/page.md#a\">Section</a>")]
    [InlineData("[App](/browser/Demo)", "<a href=\"/browser/Demo\">App</a>")]
    [InlineData("[Manual](/files/Docs/manual.pdf)", "<a href=\"/files/Docs/manual.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">Manual</a>")]
    [InlineData("[Notes](notes.markdown)", "<a href=\"/pages/Docs/notes.markdown\">Notes</a>")]
    [InlineData("[Other](other.md?v=1)", "<a href=\"/pages/Docs/other.md?v=1\">Other</a>")]
    [InlineData("[Other](other.md?v=1#a)", "<a href=\"/pages/Docs/other.md?v=1#a\">Other</a>")]
    [InlineData("[Edit](?view=edit)", "<a href=\"/pages/Docs/page.md?view=edit\">Edit</a>")]
    [InlineData("[Other host](//example.com/x)", "<a href=\"//example.com/x\" target=\"_blank\" rel=\"noopener noreferrer\">Other host</a>")]
    [InlineData("[Manual](/FILES/Docs/manual.pdf)", "<a href=\"/FILES/Docs/manual.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">Manual</a>")]
    [InlineData("[Dots](...)", "<a href=\"/files/Docs/...\" target=\"_blank\" rel=\"noopener noreferrer\">Dots</a>")]
    public async Task WhenPageHasALink_ThenOnlyFileAndExternalLinksOpenInANewTab(string source, string expected)
    {
        // Act
        var html = await RenderAsync("Docs/page.md", source);

        // Assert
        Assert.Contains(expected, html);
    }

    [Theory]
    [InlineData("Docs/C# notes/page.md", "![Plan](plan.png)", "src=\"/files/Docs/C%23%20notes/plan.png\"")]
    [InlineData("Docs/C# notes/page.md", "![Plan](my%20plan.png)", "src=\"/files/Docs/C%23%20notes/my%20plan.png\"")]
    [InlineData("Docs/100% done/page.md", "[Manual](manual.pdf)", "href=\"/files/Docs/100%25%20done/manual.pdf\"")]
    [InlineData("Docs/What?/page.md", "[Sub](sub/)", "href=\"/browser/Docs/What%3F/sub\"")]
    [InlineData("Docs/C# notes/sub/page.md", "[Up](..)", "href=\"/browser/Docs/C%23%20notes\"")]
    [InlineData("Docs/\u00dcber/page.md", "[Other](other.md)", "href=\"/pages/Docs/%C3%9Cber/other.md\"")]
    [InlineData("Docs/C# notes/page.md", "[Section](#a)", "href=\"/pages/Docs/C%23%20notes/page.md#a\"")]
    [InlineData("Docs/C# notes/page.md", "[Edit](?view=edit)", "href=\"/pages/Docs/C%23%20notes/page.md?view=edit\"")]
    public async Task WhenPageIsInAFolderWithSpecialCharacters_ThenItsLinksAreRequestedIntact(string pagePath, string source, string expected)
    {
        // Act
        var html = await RenderAsync(pagePath, source);

        // Assert
        Assert.Contains(expected, html);
    }
}
