using HomeBlaze.AI.Blazor;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentMarkdownRendererTests
{
    [Fact]
    public void WhenMarkdownContainsRawHtml_ThenHtmlIsEscaped()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("Hello <script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");

        // Assert
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JavaScript:alert(1))")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox)")]
    [InlineData("[click](/pages/Devices)")]
    [InlineData("[click][target]\n\n[target]: javascript:alert(1)")]
    [InlineData("[click](&#106;avascript:alert(1))")]
    [InlineData("[click](&#x6A;avascript:alert(1))")]
    [InlineData("[click](java&#09;script:alert(1))")]
    [InlineData("[click](java&#10;script:alert(1))")]
    [InlineData("[click](&#32;javascript:alert(1))")]
    [InlineData("[click](<\tjavascript:alert(1)>)")]
    [InlineData("[click](%20javascript:alert(1))")]
    [InlineData("[click](&#1;javascript:alert(1))")]
    public void WhenLinkTargetIsNotAllowed_ThenOnlyTheLinkTextIsRendered(string markdown)
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml(markdown);

        // Assert
        Assert.Contains("<p>click</p>", html);
        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("script:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html);
        Assert.DoesNotContain("/pages/", html);
    }

    [Theory]
    [InlineData("<javascript:alert(1)>", "javascript:alert(1)")]
    [InlineData("Get ftp://example.com/file now", "ftp://example.com/file")]
    public void WhenAutolinkTargetIsNotAllowed_ThenItIsRenderedAsText(string markdown, string expectedText)
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml(markdown);

        // Assert
        Assert.DoesNotContain("<a", html);
        Assert.Contains(expectedText, html);
    }

    [Theory]
    [InlineData("[docs](https://example.com/a?b=c)", "https://example.com/a?b=c")]
    [InlineData("<https://example.com/x>", "https://example.com/x")]
    [InlineData("Visit https://example.com/y now", "https://example.com/y")]
    [InlineData("[mail](mailto:operator@example.com)", "mailto:operator@example.com")]
    [InlineData("<a@b.c>", "mailto:a@b.c")]
    [InlineData("See www.example.com today", "http://www.example.com/")]
    public void WhenLinkTargetIsAllowed_ThenLinkOpensInNewTabWithoutReferrer(string markdown, string expectedUrl)
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml(markdown);

        // Assert
        Assert.Contains($"href=\"{expectedUrl}\"", html);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void WhenMarkdownContainsImage_ThenItIsRenderedAsLink()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("![chart](https://example.com/x.png?data=secret)");

        // Assert
        Assert.DoesNotContain("<img", html);
        Assert.Contains("href=\"https://example.com/x.png?data=secret\"", html);
        Assert.Contains(">chart</a>", html);
    }

    [Fact]
    public void WhenImageTargetIsNotAllowed_ThenOnlyTheAlternativeTextIsRendered()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("![chart](javascript:alert(1))");

        // Assert
        Assert.Equal("<p>chart</p>\n", html);
    }

    [Fact]
    public void WhenMarkdownContainsGenericAttributes_ThenNoAttributesAreApplied()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("# Title {onclick=alert(1)}\n\n[x](https://example.com){onmouseover=alert(1)}");

        // Assert
        Assert.DoesNotContain(" onclick=", html);
        Assert.DoesNotContain(" onmouseover=", html);
    }

    [Fact]
    public void WhenMarkdownContainsListsAndCode_ThenTheyAreRendered()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("- **one**\n- two\n\n```\nvar x = 1 < 2;\n```");

        // Assert
        Assert.Contains("<ul>", html);
        Assert.Contains("<strong>one</strong>", html);
        Assert.Contains("<pre><code>var x = 1 &lt; 2;", html);
    }

    [Theory]
    [InlineData("[click](java\u0001script:alert(1))")]
    [InlineData("[click](\u0000javascript:alert(1))")]
    public void WhenLinkTargetContainsControlCharacters_ThenNoLinkIsRendered(string markdown)
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml(markdown);

        // Assert
        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("href=", html);
    }

    [Fact]
    public void WhenLinkTitleContainsQuotes_ThenTheTitleCannotAddAttributes()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("[x](https://example.com \"a\\\" onmouseover=\\\"alert(1)\") and [y](https://example.com 'b\\' onclick=\\'alert(1)')");

        // Assert
        Assert.Contains("title=\"a&quot; onmouseover=&quot;alert(1)\"", html);
        Assert.Contains("title=\"b' onclick='alert(1)\"", html);
        Assert.DoesNotContain("\" onmouseover=", html);
        Assert.DoesNotContain("\" onclick=", html);
    }

    [Theory]
    [InlineData("::: fixed\nOverlay\n:::")]
    [InlineData("::: mud-dialog-container\nOverlay\n:::")]
    [InlineData("# Title {.mud-overlay #main}")]
    [InlineData("```mud-overlay\ncode\n```")]
    public void WhenMarkdownTriesToSetClassesOrIds_ThenNoneAreRendered(string markdown)
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml(markdown);

        // Assert
        Assert.DoesNotContain("class=", html);
        Assert.DoesNotContain("id=", html);
    }

    [Fact]
    public void WhenMarkdownContainsFootnotes_ThenNoIdsAreRendered()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("Value[^1]\n\n[^1]: The note.");

        // Assert
        Assert.DoesNotContain("id=", html);
        Assert.DoesNotContain("href=\"#", html);
    }

    [Fact]
    public void WhenMarkdownContainsHeading_ThenNoIdIsRendered()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("## Status");

        // Assert
        Assert.Equal("<h2>Status</h2>\n", html);
    }

    [Fact]
    public void WhenMarkdownUsesTheAllowedExtensions_ThenEachIsRendered()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("- [x] done\n- [ ] open\n\n~~old~~ value\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\na. first\nb. second");

        // Assert
        Assert.Contains("<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" />", html);
        Assert.Contains("<del>old</del>", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("<td>2</td>", html);
        Assert.Contains("<ol type=\"a\">", html);
    }

    [Theory]
    [InlineData("Conveyor at **700 RPM**.", "Conveyor at 700 RPM.")]
    [InlineData("# Status\n\n- Freezer: *ok*\n- Oven: `off`", "Status Freezer: ok Oven: off")]
    [InlineData("See [the docs](https://example.com/docs) & <b>more</b>", "See the docs & <b>more</b>")]
    public void WhenConvertingToPlainText_ThenMarkupIsRemovedAndLinesAreJoined(string markdown, string expected)
    {
        // Act
        var text = AgentMarkdownRenderer.ToPlainText(markdown);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData('>', 200, 0)]
    [InlineData('*', 500, 500)]
    public void WhenMarkdownIsNestedBeyondTheDepthLimit_ThenItIsRenderedAsEncodedText(char character, int openingCount, int closingCount)
    {
        // Arrange
        var markdown = new string(character, openingCount) + "<b>deep</b>" + new string(character, closingCount) + "\nnext";

        // Act
        var html = AgentMarkdownRenderer.ToHtml(markdown);
        var text = AgentMarkdownRenderer.ToPlainText(markdown);

        // Assert
        Assert.StartsWith("<p style=\"white-space: pre-wrap;\">", html);
        Assert.Contains("&lt;b&gt;deep&lt;/b&gt;", html);
        Assert.Contains("\nnext</p>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("<b>deep</b>", text);
        Assert.EndsWith(" next", text);
    }
}
