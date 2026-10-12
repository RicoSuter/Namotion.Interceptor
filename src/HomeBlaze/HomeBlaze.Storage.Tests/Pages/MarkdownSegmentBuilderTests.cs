using System.Net;
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
        Assert.Equal(new ExpressionMarkdownSegment("a/b", ExpressionFormat.Display, null), segments[1]);
        Assert.DoesNotContain("title", AllHtml(segments));
    }

    [Fact]
    public void WhenBlocksAreNested_ThenSegmentsAreNestedTheSameWay()
    {
        // Act
        var segments = Build("""
            :: Demo.Card
            Text {{ /A/B }}

            ::: Demo.Widget Path=/x
            :::
            ::
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
            :: Demo.Grid
            ```json
            { "Cells": [] }
            ```
            Visible text
            ::
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

            :: Demo.Card Title="open
            inside
            ::

            last
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        var html = WebUtility.HtmlDecode(AllHtml(segments));
        Assert.Contains("<div class=\"markdown-block-error\" role=\"alert\">Line 3: The value of 'Title' has no closing quote.</div>", html);
        Assert.Contains("<p>first</p>", html);
        Assert.Contains("<p>last</p>", html);
        Assert.DoesNotContain("inside", html);
    }

    [Fact]
    public void WhenClosingLineClosesNothing_ThenAnErrorBoxIsAtThatLineAndTheTextAroundItIsKept()
    {
        // Act
        var segments = Build("""
            first

            :: Demo.Card
            before
            :::
            after
            ::

            last
            """);

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        var content = AllHtml(card.Content);
        Assert.Contains(
            "<div class=\"markdown-block-error\" role=\"alert\">Line 5: This line of 3 ':' characters closes no block. " +
            "A closing line needs as many characters as the head line of its block. Open here: line 3 (2 characters).</div>",
            WebUtility.HtmlDecode(content));
        Assert.Contains("<p>before</p>", content);
        Assert.Contains("<p>after</p>", content);
        var outside = AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment));
        Assert.Contains("<p>first</p>", outside);
        Assert.Contains("<p>last</p>", outside);
        Assert.DoesNotContain("markdown-block-error", outside);
    }

    [Fact]
    public void WhenClosingLineIsOnAPageWithoutOpenBlock_ThenAnErrorBoxSaysThatNoBlockIsOpen()
    {
        // Act
        var segments = Build("""
            first
            ::
            last
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        var html = WebUtility.HtmlDecode(AllHtml(segments));
        Assert.Contains("Line 2: This line of 2 ':' characters closes no block. No block is open here.</div>", html);
        Assert.Contains("<p>first</p>", html);
        Assert.Contains("<p>last</p>", html);
    }

    [Fact]
    public void WhenBlockHasNoClosingLineAtTheEndOfThePage_ThenItIsABlockWithTheRestOfThePageAsContent()
    {
        // Act
        var segments = Build("""
            first

            :: Demo.Card
            inside
            """);

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal("<p>first</p>\n", Assert.IsType<HtmlMarkdownSegment>(segments[0]).Html);
        var card = Assert.IsType<BlockMarkdownSegment>(segments[1]);
        Assert.Equal("Demo.Card", card.Block.Head!.Name);
        Assert.Equal("\n<p>inside</p>\n", AllHtml(card.Content));
        Assert.DoesNotContain("markdown-block-error", AllHtml(segments));
    }

    [Fact]
    public void WhenInnerBlockHasNoClosingLine_ThenItsContentIsTheTextUpToTheClosingLineOfTheBlockAroundIt()
    {
        // Act
        var segments = Build("""
            first

            :: Demo.Card
            before
            ::: Demo.Widget Path=/x
            inside
            ::

            last
            """);

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Card", card.Block.Head!.Name);
        Assert.Equal("\n<p>before</p>\n", Assert.IsType<HtmlMarkdownSegment>(card.Content[0]).Html);
        var widget = Assert.IsType<BlockMarkdownSegment>(card.Content[1]);
        Assert.Equal("Demo.Widget", widget.Block.Head!.Name);
        Assert.Equal("\n<p>inside</p>\n", AllHtml(widget.Content));
        Assert.Equal(2, card.Content.Count);
        var outside = AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment));
        Assert.Contains("<p>first</p>", outside);
        Assert.Contains("<p>last</p>", outside);
        Assert.DoesNotContain("markdown-block-error", AllHtml(segments));
    }

    [Fact]
    public void WhenBlockHasAsManyCharactersAsTheOpenBlock_ThenBothAreBlocksBesideEachOtherAndTheClosingLineThatIsLeftOverIsABox()
    {
        // Act
        var segments = Build("""
            first

            :: Demo.Card
            before
            :: Demo.Widget Path=/x
            inside
            ::
            after
            ::

            last
            """);

        // Assert
        var blocks = segments.OfType<BlockMarkdownSegment>().ToList();
        Assert.Equal(["Demo.Card", "Demo.Widget"], blocks.Select(block => block.Block.Head!.Name));
        Assert.Equal("\n<p>before</p>\n", AllHtml(blocks[0].Content));
        Assert.Equal("\n<p>inside</p>\n", AllHtml(blocks[1].Content));
        var outside = WebUtility.HtmlDecode(AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment)));
        Assert.Equal(
            "<p>first</p>\n\n\n<p>after</p>\n" +
            "<div class=\"markdown-block-error\" role=\"alert\">Line 9: This line of 2 ':' characters closes no block. No block is open here.</div>\n\n" +
            "<p>last</p>\n",
            outside);
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~~")]
    public void WhenFenceLinesAreInsideFencedCodeInsideABlock_ThenTheyAreShownAsWrittenAndTheBlockContinues(string codeFence)
    {
        // Act
        var segments = Build(
            $":: Demo.Card\nbefore\n\n{codeFence}\n::: Demo.Widget Path=/x\n:::\n::\n{codeFence}\n\nafter\n::\n\nlast");

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Empty(card.Content.OfType<BlockMarkdownSegment>());
        var content = AllHtml(card.Content);
        Assert.Contains("::: Demo.Widget Path=/x\n:::\n::\n</code></pre>", content);
        Assert.Contains("<p>after</p>", content);
        var outside = AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment));
        Assert.Contains("<p>last</p>", outside);
        Assert.DoesNotContain("markdown-block-error", AllHtml(segments));
    }

    [Fact]
    public void WhenBlockIsInsideAQuote_ThenAnErrorBoxIsInsideTheQuote()
    {
        // Act
        var segments = Build("""
            > quote
            >
            > :: Demo.Card
            > inside
            > ::

            last
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        var html = AllHtml(segments);
        Assert.Contains("Line 3: A block is only allowed directly on the page or directly inside another block.</div>", html);
        Assert.Contains("<p>quote</p>", html);
        Assert.Contains("<p>last</p>", html);
    }

    [Theory]
    [InlineData(":: 12:30 lunch", "<p>:: 12:30 lunch</p>")]
    [InlineData("::)", "<p>::)</p>")]
    [InlineData("\\:: Demo.Card", "<p>:: Demo.Card</p>")]
    [InlineData("\\::", "<p>::</p>")]
    public void WhenLineStartsWithFenceCharactersButIsNoFenceLine_ThenItIsShownAsText(string line, string expectedHtml)
    {
        // Act
        var segments = Build($"first\n\n{line}\n\nlast");

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal($"<p>first</p>\n{expectedHtml}\n<p>last</p>\n", AllHtml(segments));
    }

    [Fact]
    public void WhenClosingLineIsInsideAQuoteInsideABlock_ThenAnErrorBoxIsInTheQuoteAndTheBlockGoesOn()
    {
        // Act
        var segments = Build("""
            :: Demo.Card
            before
            > ::
            after
            ::

            last
            """);

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        var content = WebUtility.HtmlDecode(AllHtml(card.Content));
        Assert.Contains("<p>before</p>", content);
        Assert.Contains("<blockquote>\n<div class=\"markdown-block-error\" role=\"alert\">Line 3: This line of 2 ':' characters closes no block. A closing line inside a quote or a list item closes no block outside of it.</div>", content);
        Assert.Contains("<p>after</p>", content);
        Assert.Contains("<p>last</p>", AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment)));
    }

    [Fact]
    public void WhenClosingLineFollowsADefinitionListInsideABlock_ThenTheBlockEndsThereAndTheTextAfterItIsOutside()
    {
        // Act
        var segments = Build("""
            :: Demo.Card
            Term
            :   Definition
            ::
            after
            """);

        // Assert
        var card = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Contains("<dd>Definition</dd>", AllHtml(card.Content));
        Assert.DoesNotContain("after", AllHtml(card.Content));
        Assert.Contains("<p>after</p>", AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment)));
        Assert.DoesNotContain("markdown-block-error", AllHtml(segments));
    }

    [Fact]
    public void WhenThreeFenceCharactersAreInTheText_ThenTheyHaveNoOtherMeaning()
    {
        // Act
        var segments = Build("A ::marked:: word and a ratio of 1:::2");

        // Assert
        Assert.Equal("<p>A ::marked:: word and a ratio of 1:::2</p>\n", AllHtml(segments));
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
            :: {.band}
            text
            ::
            """);

        // Assert
        Assert.Contains("<div class=\"band\">", AllHtml(segments));
    }

    [Fact]
    public void WhenPageTextContainsLiteralBlockMarkers_ThenTheyStayAsHtmlAndNoBlockIsCreated()
    {
        // Act
        var segments = Build("""
            Intro

            <!--BLOCK:7-->

            Middle

            <!--/BLOCK:7-->
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        var html = AllHtml(segments);
        Assert.Contains("<!--BLOCK:7-->", html);
        Assert.Contains("<!--/BLOCK:7-->", html);
        Assert.Contains("Middle", html);
    }

    [Fact]
    public void WhenLiteralBlockMarkerPrecedesRealBlock_ThenOnlyTheRealBlockIsCreated()
    {
        // Act
        var segments = Build("""
            <!--BLOCK:0-->

            :: Demo.Widget Path=/x
            ::
            """);

        // Assert
        var widget = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Widget", widget.Block.Head!.Name);
        Assert.Contains("<!--BLOCK:0-->", AllHtml(segments));
    }

    [Fact]
    public void WhenOldSubjectFenceIsNotOnFirstLine_ThenErrorBoxReportsTheFenceLine()
    {
        // Act
        var segments = Build("""
            first

            ```subject(old)
            { "$type": "X" }
            ```
            """);

        // Assert
        Assert.Contains("role=\"alert\">Line 3: ", AllHtml(segments));
    }

    [Fact]
    public void WhenLinkRewriterIsGiven_ThenLinksInsideBlocksAreRewritten()
    {
        // Act
        var segments = Build("""
            :: Demo.Card
            [Notes](notes.md)
            ::
            """, url => "/pages/" + url);

        // Assert
        Assert.Contains("href=\"/pages/notes.md\"", AllHtml(segments));
    }

    [Theory]
    [InlineData("~~~~markdown\nSpeed {{ a/B }} RPM\n~~~~")]
    [InlineData("```\nSpeed {{ a/B }} RPM\n```")]
    [InlineData("```text\nSpeed {{ a/B }} RPM\n```")]
    [InlineData("```\nSpeed {{ a/B }} RPM")]
    [InlineData("Intro\n\n    Speed {{ a/B }} RPM")]
    [InlineData("Write `Speed {{ a/B }} RPM` here")]
    [InlineData(":: Demo.Card\n```\nSpeed {{ a/B }} RPM\n```\n::")]
    public void WhenExpressionIsInsideMarkdownCode_ThenItIsShownAsWrittenAndIsNoSegment(string source)
    {
        // Act
        var segments = Build(source);

        // Assert
        Assert.DoesNotContain(Flatten(segments), segment => segment is ExpressionMarkdownSegment);
        Assert.Contains("Speed {{ a/B }} RPM", VisibleText(segments));
    }

    [Fact]
    public void WhenExpressionIsNextToInlineCode_ThenOnlyTheOneOutsideTheCodeIsASegment()
    {
        // Act
        var segments = Build("Write `{{ a/B }}` to show {{ c/D }} here");

        // Assert
        var expression = Assert.Single(segments.OfType<ExpressionMarkdownSegment>());
        Assert.Equal("c/D", expression.Path);
        Assert.Contains("<code>{{ a/B }}</code>", VisibleText(segments));
    }

    [Fact]
    public void WhenInlineCodeHasOnlyOpeningBraces_ThenTheExpressionAfterItIsStillASegment()
    {
        // Act
        var segments = Build("An expression starts with `{{` like {{ a/B }} here");

        // Assert
        var expression = Assert.Single(segments.OfType<ExpressionMarkdownSegment>());
        Assert.Equal("a/B", expression.Path);
        Assert.Contains("<code>{{</code>", VisibleText(segments));
    }

    [Fact]
    public void WhenClosingBracesAreInsideInlineCode_ThenTheyCloseNoExpression()
    {
        // Act
        var segments = Build("Open with {{ and close with `}}` here");

        // Assert
        Assert.Empty(segments.OfType<ExpressionMarkdownSegment>());
        Assert.Contains("Open with {{ and close with <code>}}</code> here", VisibleText(segments));
    }

    [Theory]
    [InlineData("Value <code>{{ a/B }}</code> now")]
    [InlineData("<div>{{ a/B }}</div>")]
    [InlineData("<pre>{{ a/B }}</pre>")]
    public void WhenExpressionIsInsideRawHtmlOfTheAuthor_ThenItIsStillASegment(string source)
    {
        // Act
        var segments = Build(source);

        // Assert
        var expression = Assert.Single(segments.OfType<ExpressionMarkdownSegment>());
        Assert.Equal("a/B", expression.Path);
    }

    [Fact]
    public void WhenMermaidFenceContainsDoubleBraces_ThenItRendersAsDiagramWithoutExpression()
    {
        // Act
        var segments = Build("""
            ```mermaid
            graph TD;
                A{{text}} --> B
            ```
            """);

        // Assert
        Assert.Empty(segments.OfType<ExpressionMarkdownSegment>());
        Assert.Contains("<pre class=\"mermaid\">", AllHtml(segments));
        Assert.Contains("A{{text}} --> B", VisibleText(segments));
    }

    [Fact]
    public void WhenBlockIsInsideFencedCode_ThenItIsShownAsWrittenAndIsNoBlock()
    {
        // Act
        var segments = Build("""
            ~~~~markdown
            :: Demo.Widget Path=/x
            ::
            ~~~~
            """);

        // Assert
        Assert.Empty(segments.OfType<BlockMarkdownSegment>());
        Assert.Contains(":: Demo.Widget Path=/x", AllHtml(segments));
    }

    [Fact]
    public void WhenUnclosedExpressionIsInsideBlock_ThenTheRestOfThePageAndTheLaterExpressionAreKept()
    {
        // Act
        var segments = Build("""
            Before

            :: Demo.Card
            Use {{ to start an expression.
            ::

            After the card

            Speed: {{ motor/Speed }}
            """);

        // Assert
        var block = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Card", block.Block.Head!.Name);
        Assert.Contains("Use {{ to start an expression.", AllHtml(block.Content));
        Assert.Contains("After the card", AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment)));
        Assert.Equal(new ExpressionMarkdownSegment("motor/Speed", ExpressionFormat.Display, null), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
    }

    [Fact]
    public void WhenUnclosedExpressionIsBeforeBlock_ThenTheBlockAndTheExpressionInItAreKept()
    {
        // Act
        var segments = Build("""
            Use {{ to start an expression.

            :: Demo.Card
            Speed: {{ motor/Speed }}
            ::

            After the card
            """);

        // Assert
        var block = Assert.Single(segments.OfType<BlockMarkdownSegment>());
        Assert.Equal("Demo.Card", block.Block.Head!.Name);
        Assert.Equal(
            new ExpressionMarkdownSegment("motor/Speed", ExpressionFormat.Display, null), Assert.Single(block.Content.OfType<ExpressionMarkdownSegment>()));
        Assert.Empty(segments.OfType<ExpressionMarkdownSegment>());
        var outside = AllHtml(segments.Where(segment => segment is HtmlMarkdownSegment));
        Assert.Contains("Use {{ to start an expression.", outside);
        Assert.Contains("After the card", outside);
    }

    [Fact]
    public void WhenUnclosedExpressionPrecedesCompleteOneInTheSameParagraph_ThenOnlyTheCompleteOneIsASegment()
    {
        // Act
        var segments = Build("Use {{ to start one, like {{ a/B }} here");

        // Assert
        Assert.Equal(new ExpressionMarkdownSegment("a/B", ExpressionFormat.Display, null), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
        Assert.Contains("Use {{ to start one, like ", AllHtml(segments));
    }

    [Fact]
    public void WhenClosingMarkerOfBlockIsMissing_ThenItsContentIsStillInTheResult()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card\n::");
        const string token = "0123456789abcdef0123456789abcdef";

        // Act
        var segments = MarkdownSegmentBuilder.Split(
            $"<p>Before</p><!--BLOCK:{token}:0--><p>Inside</p>", page, token);

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal(new HtmlMarkdownSegment("<p>Before</p>"), segments[0]);
        var block = Assert.IsType<BlockMarkdownSegment>(segments[1]);
        Assert.Same(page.AllBlocks[0], block.Block);
        Assert.Equal(new HtmlMarkdownSegment("<p>Inside</p>"), Assert.Single(block.Content));
    }

    [Theory]
    [InlineData("""Value {{ Items["a"]/B }} now""", """Items["a"]/B""")]
    [InlineData("Value {{ A&B/C }} now", "A&B/C")]
    [InlineData("Value {{ Items['a']/B }} now", "Items['a']/B")]
    public void WhenPathHasCharactersThatHtmlEncodes_ThenTheSegmentHasThePathAsWritten(string source, string expectedPath)
    {
        // Act
        var segments = Build(source);

        // Assert
        Assert.Equal(new ExpressionMarkdownSegment(expectedPath, ExpressionFormat.Display, null), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
    }

    [Fact]
    public void WhenBracesEncloseALiteralLessThanSign_ThenTheyAreShownAsWrittenAndAreNoSegment()
    {
        // Act
        var segments = Build("Value {{ a < b }} now and {{ c/D }}");

        // Assert
        Assert.Equal(new ExpressionMarkdownSegment("c/D", ExpressionFormat.Display, null), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
        Assert.Contains("Value {{ a < b }} now and ", VisibleText(segments));
    }

    [Theory]
    [InlineData("Speed {{ a/B }} RPM and {{ c/D }} bar")]
    [InlineData("Write `{{ a/B }}` to show {{ c/D }} here")]
    [InlineData("```\nSpeed {{ a/B }} RPM\n```\n\nSpeed {{ c/D }} RPM")]
    [InlineData("~~~~markdown\n:: Demo.Card\n{{ a/B }}\n::\n~~~~")]
    [InlineData(":: Demo.Card\nSpeed {{ a/B }} RPM\n::\n\nAfter {{ c/D }}")]
    [InlineData(":: Demo.Card\n::: Footer\nSpeed {{ a/B }} RPM\n:::\n::")]
    [InlineData("<div title=\"{{ a/B }}\">{{ c/D }}</div>")]
    [InlineData("Value <code>{{ a/B }}</code> now")]
    [InlineData("An expression starts with `{{` like {{ a/B }} here")]
    [InlineData("Open with {{ and close with `}}` here")]
    [InlineData("Use {{ to start one, like {{ a/B }} here")]
    [InlineData("Before\n\n:: Demo.Card\nUse {{ to start an expression.\n::\n\nSpeed: {{ a/B }}")]
    [InlineData("Use {{ to start an expression.\n\n:: Demo.Card\nSpeed: {{ a/B }}\n::")]
    [InlineData("""Value {{ Items["a"]/B }} and {{ A&B/C }} now""")]
    [InlineData("Value {{ a < b }} now and {{ c/D }}")]
    [InlineData("Value {{ a&b/C }} now")]
    [InlineData("Nothing to see here")]
    public void WhenPageIsRendered_ThenItsExpressionsAreTheOnesFoundInItsText(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var rendered = Flatten(MarkdownSegmentBuilder.Build(page))
            .OfType<ExpressionMarkdownSegment>()
            .Select(expression => expression.Path);
        var found = MarkdownSegmentBuilder.FindExpressions(page).Select(expression => expression.Path);

        // Assert
        Assert.Equal(found, rendered);
    }

    [Fact]
    public void WhenExpressionHasTheRawFormat_ThenItIsFoundWithThePathWithoutTheFormat()
    {
        // Act
        var expression = Assert.Single(MarkdownSegmentBuilder.FindExpressions(MarkdownPageParser.Parse("Value {{ a | raw }} now")));

        // Assert
        Assert.Equal("a", expression.Path);
        Assert.Equal(ExpressionFormat.Raw, expression.Format);
        Assert.Null(expression.Error);
    }

    [Fact]
    public void WhenExpressionHasAnUnknownFormat_ThenItIsFoundWithAnError()
    {
        // Act
        var expression = Assert.Single(MarkdownSegmentBuilder.FindExpressions(MarkdownPageParser.Parse("Value {{ a | x }} now")));

        // Assert
        Assert.Equal("a", expression.Path);
        Assert.Equal("Unknown format 'x'", expression.Error);
    }

    [Fact]
    public void WhenExpressionIsFoundInText_ThenItsSpanCoversTheBraces()
    {
        // Arrange
        const string source = "Use {{ to start one, like {{ a/B }} here";

        // Act
        var expression = Assert.Single(MarkdownSegmentBuilder.FindExpressions(MarkdownPageParser.Parse(source)));

        // Assert
        Assert.Equal("{{ a/B }}", source.Substring(expression.Span.Start, expression.Span.Length));
        Assert.Equal("a/B", expression.Path);
    }

    private static IEnumerable<MarkdownSegment> Flatten(IEnumerable<MarkdownSegment> segments) =>
        segments.SelectMany(segment => segment is BlockMarkdownSegment block
            ? Flatten(block.Content).Prepend(segment)
            : [segment]);

    // What a browser shows for the HTML: character references are decoded.
    private static string VisibleText(IEnumerable<MarkdownSegment> segments) =>
        WebUtility.HtmlDecode(AllHtml(segments));
}
