using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownSegmentBuilderTests
{
    private static IReadOnlyList<MarkdownSegment> Build(string source, Func<string, string>? linkRewriter = null) =>
        MarkdownSegmentBuilder.Build(MarkdownPageParser.Parse(source.Replace("\r\n", "\n")), linkRewriter);

    private static string AllHtml(IEnumerable<MarkdownSegment> segments) =>
        string.Concat(segments.Select(segment => segment switch
        {
            HtmlMarkdownSegment html => html.Html,
            BlockMarkdownSegment block => AllHtml(block.Content),
            _ => string.Empty
        }));

    [Fact]
    public void WhenPageHasFrontmatterAndExpression_ThenFrontmatterIsDroppedAndPathIsTrimmed()
    {
        // Act
        var segments = Build("""
            ---
            title: Test
            ---

            Value {{   a/b   }} end
            """);

        // Assert
        Assert.Equal(new HtmlMarkdownSegment("<p>Value "), segments[0]);
        Assert.Equal(new ExpressionMarkdownSegment("a/b"), segments[1]);
        Assert.DoesNotContain("title", AllHtml(segments));
    }

    [Fact]
    public void WhenBlocksAreNested_ThenSegmentsAreNestedTheSameWay()
    {
        // Act
        var segments = Build("""
            ::::Demo.Card
            Text {{ /A/B }}

            :::Demo.Widget path=/x
            :::
            ::::
            """);

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Card", card.Block.Head!.Name);
        Assert.Contains(card.Content, segment => segment is ExpressionMarkdownSegment { Path: "/A/B" });
        var widget = Assert.Single(card.Content.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Widget", widget.Block.Head!.Name);
        Assert.Empty(widget.Content);
    }

    [Fact]
    public void WhenBlockHasJsonFence_ThenFenceIsNotRendered()
    {
        // Act
        var segments = Build("""
            :::Demo.Grid
            ```json
            { "cells": [] }
            ```
            Visible text
            :::
            """);

        // Assert
        var html = AllHtml(segments);
        Assert.DoesNotContain("cells", html);
        Assert.Contains("Visible text", html);
    }

    [Fact]
    public void WhenBlockIsInvalid_ThenAnErrorBoxWithTheLineIsRendered()
    {
        // Act
        var segments = Build("""
            first

            :::
            unnamed
            :::
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        Assert.Contains("<div class=\"markdown-block-error\" role=\"alert\">Line 3:", AllHtml(segments));
    }

    [Fact]
    public void WhenPageHasOldSubjectFence_ThenAnErrorBoxExplainsTheNewSyntax()
    {
        // Act
        var segments = Build("""
            ```subject(old)
            { "$type": "X" }
            ```
            """);

        // Assert
        var html = AllHtml(segments);
        Assert.Contains("no longer supported", html);
        Assert.DoesNotContain("$type", html);
    }

    [Fact]
    public void WhenPageHasOrdinaryAndMermaidFences_ThenTheyRenderAsBefore()
    {
        // Act
        var segments = Build("""
            ```csharp
            var x = 1;
            ```

            ```mermaid
            graph TD;
            ```
            """);

        // Assert
        var html = AllHtml(segments);
        Assert.Contains("<pre><code class=\"language-csharp\">", html);
        Assert.Contains("<pre class=\"mermaid\">", html);
    }

    [Fact]
    public void WhenStyleBlockIsUsed_ThenItRendersAsDivWithItsClass()
    {
        // Act
        var segments = Build("""
            ::: {.band}
            text
            :::
            """);

        // Assert
        Assert.Contains("<div class=\"band\">", AllHtml(segments));
    }

    [Fact]
    public void WhenLinkRewriterIsGiven_ThenLinksInsideBlocksAreRewritten()
    {
        // Act
        var segments = Build("""
            :::Demo.Card
            [Notes](notes.md)
            :::
            """, url => "/pages/" + url);

        // Assert
        Assert.Contains("href=\"/pages/notes.md\"", AllHtml(segments));
    }
}
