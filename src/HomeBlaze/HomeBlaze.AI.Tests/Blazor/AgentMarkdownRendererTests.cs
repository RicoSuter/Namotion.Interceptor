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

    [Fact]
    public void WhenAutolinkTargetIsNotAllowed_ThenItIsRenderedAsText()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("<javascript:alert(1)>");

        // Assert
        Assert.DoesNotContain("<a", html);
        Assert.Contains("javascript:alert(1)", html);
    }

    [Theory]
    [InlineData("[docs](https://example.com/a?b=c)", "https://example.com/a?b=c")]
    [InlineData("<https://example.com/x>", "https://example.com/x")]
    [InlineData("Visit https://example.com/y now", "https://example.com/y")]
    [InlineData("[mail](mailto:operator@example.com)", "mailto:operator@example.com")]
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
    public void WhenMarkdownContainsPipeTable_ThenTableIsRendered()
    {
        // Act
        var html = AgentMarkdownRenderer.ToHtml("| Device | Value |\n|---|---|\n| Motor | 42 |");

        // Assert
        Assert.Contains("<table>", html);
        Assert.Contains("<th>Device</th>", html);
        Assert.Contains("<td>42</td>", html);
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
}
