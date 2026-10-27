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
    public void WhenBlockArgumentHasAnExpression_ThenItIsNoExpressionOfThePageText()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(":: Demo.Card Title=\"{{ subject/Name }}\"\n```json\n{ \"Footer\": \"{{ a/B }}\" }\n```\n::\n\nText {{ c/D }}");

        // Act
        var found = MarkdownSegmentBuilder.FindExpressions(page).Select(expression => expression.Text).ToArray();

        // Assert
        Assert.Equal(["c/D"], found);
    }

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
        Assert.Equal(new HtmlMarkdownSegment("<p>Value ", [0]), segments[0]);
        Assert.Equal(new ExpressionMarkdownSegment("a/b"), segments[1]);
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
        Assert.Contains(card.Content, segment => segment is ExpressionMarkdownSegment { Text: "/A/B" });
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
        Assert.Equal("c/D", expression.Text);
        Assert.Contains("<code>{{ a/B }}</code>", VisibleText(segments));
    }

    [Fact]
    public void WhenInlineCodeHasOnlyOpeningBraces_ThenTheExpressionAfterItIsStillASegment()
    {
        // Act
        var segments = Build("An expression starts with `{{` like {{ a/B }} here");

        // Assert
        var expression = Assert.Single(segments.OfType<ExpressionMarkdownSegment>());
        Assert.Equal("a/B", expression.Text);
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
        Assert.Equal("a/B", expression.Text);
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
        Assert.Equal(new ExpressionMarkdownSegment("motor/Speed"), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
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
            new ExpressionMarkdownSegment("motor/Speed"), Assert.Single(block.Content.OfType<ExpressionMarkdownSegment>()));
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
        Assert.Equal(new ExpressionMarkdownSegment("a/B"), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
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
            $"<p>Before</p><!--BLOCK:{token}:0--><p>Inside</p>", page, token, []);

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal(new HtmlMarkdownSegment("<p>Before</p>", [0, 13]), segments[0]);
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
        Assert.Equal(new ExpressionMarkdownSegment(expectedPath), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
    }

    [Fact]
    public void WhenBracesEncloseALiteralLessThanSign_ThenTheyAreAnExpressionWithIt()
    {
        // Act
        var segments = Build("Value {{ a < b }} now and {{ c/D }}");

        // Assert
        Assert.Equal(["a < b", "c/D"], segments.OfType<ExpressionMarkdownSegment>().Select(segment => segment.Text));
        Assert.Contains("Value ", VisibleText(segments));
        Assert.DoesNotContain("{{", VisibleText(segments));
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
    [InlineData("<div>{{ a/B &lt; 5 }}</div>")]
    [InlineData("Value {{ a/B < 5 }} and <b title=\"{{ c/D &gt; 1 }}\">x</b>")]
    [InlineData("Nothing to see here")]
    public void WhenPageIsRendered_ThenItsExpressionsAreTheOnesFoundInItsText(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var rendered = Flatten(MarkdownSegmentBuilder.Build(page))
            .OfType<ExpressionMarkdownSegment>()
            .Select(expression => expression.Text);
        var found = MarkdownSegmentBuilder.FindExpressions(page).Select(expression => expression.Text);

        // Assert
        Assert.Equal(found, rendered);
    }

    [Fact]
    public void WhenExpressionHasAPipe_ThenItIsFoundWithItsWholeText()
    {
        // Act
        var expression = Assert.Single(MarkdownSegmentBuilder.FindExpressions(MarkdownPageParser.Parse("Value {{ a | raw }} now")));

        // Assert
        Assert.Equal("a | raw", expression.Text);
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
        Assert.Equal("a/B", expression.Text);
    }

    private static IEnumerable<MarkdownSegment> Flatten(IEnumerable<MarkdownSegment> segments) =>
        segments.SelectMany(segment => segment is BlockMarkdownSegment block
            ? Flatten(block.Content).Prepend(segment)
            : [segment]);

    [Fact]
    public void WhenPageHasATable_ThenTheTableStandsInABoxOfItsOwn()
    {
        // Act
        var html = AllHtml(Build("""
            | Name | Speed |
            | --- | --- |
            | Pump | {{ pump/CurrentSpeed }} |
            """));

        // Assert: the box scrolls a wide table sideways, so the table itself keeps its semantics.
        Assert.Matches(@"^<div class=""markdown-table"">\s*<table>", html);
        Assert.Matches(@"</table>\s*</div>\s*$", html);
    }

    [Fact]
    public void WhenCodeInATableCellHasDots_ThenItMayBreakAfterEachDotOnly()
    {
        // Act
        var html = AllHtml(Build("""
            Text with `HomeBlaze.Components.Card`.

            | `Type.Name` | Kind |
            | --- | --- |
            | `HomeBlaze.Components.Card` | `Subtitle` |
            """));

        // Assert
        Assert.Contains("<p>Text with <code>HomeBlaze.Components.Card</code>.</p>", html);
        Assert.Contains("<code>Type.<wbr>Name</code>", html);
        Assert.Contains("<code>HomeBlaze.<wbr>Components.<wbr>Card</code>", html);
        Assert.Contains("<code>Subtitle</code>", html);
    }

    [Fact]
    public void WhenTextBlocksAreMarked_ThenEachStartsAnHtmlSegmentWithItsLines()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("# Title\n\nFirst {{ a/B }} paragraph.\n\n:: Demo.Card\nInside.\n::\n");

        // Act
        var segments = MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true);

        // Assert
        var starts = segments.OfType<HtmlMarkdownSegment>().Where(segment => segment.TextBlock is not null).ToList();
        Assert.Equal([1, 3], starts.Select(segment => segment.TextBlock!.Line));
        Assert.StartsWith("<h1", starts[0].Html);
        Assert.StartsWith("<p>First ", starts[1].Html.TrimStart());
        var all = segments.ToList();
        Assert.IsType<ExpressionMarkdownSegment>(all[all.IndexOf(starts[1]) + 1]);
        var inside = segments.OfType<BlockMarkdownSegment>().Single().Content
            .OfType<HtmlMarkdownSegment>().Single(segment => segment.TextBlock is not null);
        Assert.Same(page.Blocks[0], inside.TextBlock!.Owner);
        Assert.Contains("Inside.", inside.Html);
    }

    [Fact]
    public void WhenTextBlocksAreMarkedAndAListItemHoldsAWidgetWithABlockInside_ThenTheListStartsATextSegment()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("- item\n\n  :: Demo.Card\n  ::: Demo.One\n  :::\n  ::\n\nAfter.\n");

        // Act
        var segments = MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true);

        // Assert
        var starts = segments.OfType<HtmlMarkdownSegment>().Where(segment => segment.TextBlock is not null).ToList();
        Assert.Equal([(1, false), (8, true)], starts.Select(segment => (segment.TextBlock!.Line, segment.TextBlock.IsEditable)));
    }

    [Fact]
    public void WhenThePageHasACommentShapedLikeATextMarker_ThenItStaysInTheHtmlAndStartsNoTextBlock()
    {
        // Arrange: the comment names the index of the last text block, but not the token of the build.
        var page = MarkdownPageParser.Parse("<!--TEXT:00000000000000000000000000000000:1-->\n\nText.\n\n:: Demo.Card\n::\n\nAfter.\n");

        // Act
        var segments = MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true);

        // Assert
        var starts = segments.OfType<HtmlMarkdownSegment>().Where(segment => segment.TextBlock is not null).ToList();
        Assert.Equal([1, 3, 8], starts.Select(segment => segment.TextBlock!.Line));
        Assert.Equal("<!--TEXT:00000000000000000000000000000000:1-->\n", starts[0].Html);
        Assert.Contains("Text.", starts[1].Html);
        Assert.Contains("After.", starts[2].Html);
    }

    [Fact]
    public void WhenTextBlocksStartRightBeforeAnotherMarker_ThenEachStartsAnEmptySegment()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("One.\n\nTwo.\n");
        var textBlocks = MarkdownTextBlocks.Find(page);
        const string token = "0123456789abcdef0123456789abcdef";

        // Act: the first marker is right before another, the second right before an expression.
        var segments = MarkdownSegmentBuilder.Split(
            $"<!--TEXT:{token}:0--><!--TEXT:{token}:1-->{{{{ a/B }}}}<p>x</p>", page, token, [], textBlocks);

        // Assert
        Assert.Equal(4, segments.Count);
        Assert.Equal(new HtmlMarkdownSegment(string.Empty) { TextBlock = textBlocks[0] }, segments[0]);
        Assert.Equal(new HtmlMarkdownSegment(string.Empty) { TextBlock = textBlocks[1] }, segments[1]);
        Assert.IsType<ExpressionMarkdownSegment>(segments[2]);
        Assert.Equal(new HtmlMarkdownSegment("<p>x</p>", [8]), segments[3]);
    }

    [Fact]
    public void WhenTheLastTextBlockStartsAtTheEndOfTheHtml_ThenItStartsAnEmptySegment()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("One.\n");
        var textBlocks = MarkdownTextBlocks.Find(page);
        const string token = "0123456789abcdef0123456789abcdef";

        // Act
        var segments = MarkdownSegmentBuilder.Split($"<p>x</p><!--TEXT:{token}:0-->", page, token, [], textBlocks);

        // Assert
        Assert.Equal(
            [new HtmlMarkdownSegment("<p>x</p>", [0]), new HtmlMarkdownSegment(string.Empty) { TextBlock = textBlocks[0] }],
            segments);
    }

    [Fact]
    public void WhenTheHtmlIsSplitWithoutTextBlocks_ThenATextMarkerWithTheTokenStaysInTheHtml()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("One.\n");
        const string token = "0123456789abcdef0123456789abcdef";

        // Act
        var segments = MarkdownSegmentBuilder.Split($"<!--TEXT:{token}:0--><p>One.</p>", page, token, []);

        // Assert
        var html = $"<!--TEXT:{token}:0--><p>One.</p>";
        Assert.Equal(new HtmlMarkdownSegment(html, [0, html.Length]), Assert.Single(segments));
    }

    [Theory]
    [InlineData("Power {{ a==1 or b==2 }} now", "a==1 or b==2")]
    [InlineData("Power {{ a < 5 }} now", "a < 5")]
    [InlineData("Power {{ a <= 5 && b }} now", "a <= 5 && b")]
    [InlineData("Power {{ a == 'x' }} now", "a == 'x'")]
    [InlineData("Power **{{ a > 5 }}** now", "a > 5")]
    public void WhenExpressionHasOperators_ThenItsTextReachesTheSegmentAsWritten(string source, string text)
    {
        // Act
        var segments = Build(source);

        // Assert
        Assert.Equal(new ExpressionMarkdownSegment(text), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
    }

    [Fact]
    public void WhenExpressionInATableCellHasPipes_ThenTheCellIsNotSplit()
    {
        // Act
        var segments = Build("| A | B |\n|---|---|\n| {{ a | raw }} | {{ a || b }} |\n| {{ c \\| raw }} | z |");

        // Assert
        Assert.Equal(
            ["a | raw", "a || b", "c \\| raw"],
            segments.OfType<ExpressionMarkdownSegment>().Select(segment => segment.Text));
        // Two body rows of two cells each: a '|' inside an expression split none of them.
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(AllHtml(segments), "<td").Count);
    }

    [Fact]
    public void WhenRawHtmlHasAnExpressionInAnAttribute_ThenItIsStillASegment()
    {
        // Act
        var segments = Build("<span title=\"{{ a &lt; 5 }}\">{{ b }}</span>");

        // Assert
        Assert.Equal(["a < 5", "b"], segments.OfType<ExpressionMarkdownSegment>().Select(segment => segment.Text).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void WhenTextLooksLikeAMarker_ThenItIsNoExpression()
    {
        // Act
        var segments = Build("<!--EXPR:00000000000000000000000000000000:0--> and {{ a }}");

        // Assert
        Assert.Equal(new ExpressionMarkdownSegment("a"), Assert.Single(segments.OfType<ExpressionMarkdownSegment>()));
    }

    [Fact]
    public void WhenPageHasTwoParagraphs_ThenTheSecondStartsABlock()
    {
        // Act
        var segments = Build("First.\n\nSecond.");

        // Assert
        Assert.Equal([new HtmlMarkdownSegment("<p>First.</p>\n<p>Second.</p>\n", [0, 14, 29])], segments);
    }

    [Fact]
    public void WhenAnHtmlBlockOpensAnElement_ThenNoBlockStartsBeforeOrAfterIt()
    {
        // Act
        var segments = Build("Before.\n\n<div class=\"box\">\n\nInside.\n\n</div>\n\nAfter.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal("<p>Before.</p>\n<div class=\"box\">\n<p>Inside.</p>\n</div>\n<p>After.</p>\n", html.Html);
        Assert.Equal(BlockStartsOfPage(html.Html), html.BlockStarts.ToArray());
    }

    [Fact]
    public void WhenInlineHtmlInTheFirstParagraphOpensAnElement_ThenNoBlockStarts()
    {
        // Act
        var segments = Build("One <span class=\"x\">open\n\nTwo close</span>\n\nThree.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html), html.BlockStarts.ToArray());
    }

    [Fact]
    public void WhenRawHtmlInsideAStyleBlockOpensAnElement_ThenNoBlockStartsAfterIt()
    {
        // Act
        var segments = Build("Before.\n\n:: {.box}\nOne <span class=\"x\">open\n::\n\nInside.\n\n:: {.box}\nTwo close</span>\n::\n\nAfter.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html, "<div class=\"box\">"), html.BlockStarts.ToArray());
    }

    [Fact]
    public void WhenRawHtmlInsideAWidgetBlockIsNeverClosed_ThenBlocksStillStartAfterTheWidget()
    {
        // Act
        var segments = Build("Before.\n\n:: Demo.Card\n<div class=\"x\">\n::\n\nAfter.\n\nLast.");

        // Assert
        Assert.Collection(segments,
            segment => Assert.Equal(new HtmlMarkdownSegment("<p>Before.</p>\n", [0, 15]), segment),
            segment => Assert.Contains("<div class=\"x\">", AllHtml(Assert.IsType<BlockMarkdownSegment>(segment).Content)),
            segment =>
            {
                var html = Assert.IsType<HtmlMarkdownSegment>(segment);
                Assert.Equal(BlockStartsOfPage(html.Html, "<p>Last."), html.BlockStarts.ToArray());
            });
    }

    [Fact]
    public void WhenAnHtmlBlockIsNeverClosed_ThenNoBlockStartsAfterIt()
    {
        // Act
        var segments = Build("Before\n\n<div class=\"x\">\n\nUnclosed {{ a/B }}\n\nAfter {{ c/D }}");

        // Assert
        var html = segments.OfType<HtmlMarkdownSegment>().ToArray();
        Assert.Equal(3, html.Length);
        Assert.Equal([0], html[0].BlockStarts.ToArray());
        Assert.True(html[1].BlockStarts.IsDefault);
        Assert.Equal([html[2].Html.Length], html[2].BlockStarts.ToArray());
        Assert.Equal(["a/B", "c/D"], segments.OfType<ExpressionMarkdownSegment>().Select(segment => segment.Text));
    }

    [Fact]
    public void WhenInlineHtmlIsNeverClosed_ThenNoBlockStartsAfterIt()
    {
        // Act
        var segments = Build("Some <b>bold\n\nNext {{ a/B }}\n\nLast.");

        // Assert
        var html = segments.OfType<HtmlMarkdownSegment>().ToArray();
        Assert.Equal(2, html.Length);
        Assert.Equal([0], html[0].BlockStarts.ToArray());
        Assert.Equal([html[1].Html.Length], html[1].BlockStarts.ToArray());
    }

    [Fact]
    public void WhenRawHtmlOnlyClosesAnElement_ThenBlocksStillStartAfterIt()
    {
        // Act
        var segments = Build("Before.\n\n</span>\n\nText <span>open\n\nAfter.\n\nLast.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html, "<p>Text"), html.BlockStarts.ToArray());
    }

    [Theory]
    [InlineData("</p>")]
    [InlineData("</br>")]
    [InlineData("<col>")]
    public void WhenAnHtmlBlockFollowsAParagraph_ThenItStartsNoBlockOfItsOwn(string rawHtml)
    {
        // Act
        var segments = Build($"Before.\n\n{rawHtml}\n\nAfter.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html, "<p>After."), html.BlockStarts.ToArray());
    }

    [Theory]
    [InlineData("An <img src=\"x\"> image.")]
    [InlineData("A <br> break.")]
    [InlineData("A <!-- c --> comment.")]
    [InlineData("An <img src=\"a.png\"> image<br/> and <!-- <div> --> text.")]
    public void WhenRawHtmlHasOnlyVoidElementsAndComments_ThenBlocksStillStartAfterIt(string paragraph)
    {
        // Act
        var segments = Build($"Before.\n\n{paragraph}\n\nAfter.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html, "<p>A", "<p>After."), html.BlockStarts.ToArray());
    }

    // The rule does not look for the closing tag, so even a closed element ends the block starts of the page.
    [Theory]
    [InlineData("<details><summary>x</summary>y</details>")]
    [InlineData("<div class=\"spacer\"/>")]
    [InlineData("<!--> <div>")]
    [InlineData("<tr><td>x</td></tr>")]
    [InlineData("<hr class=\"x\"")]
    public void WhenAnHtmlBlockOpensAnElementInAnyForm_ThenNoBlockStarts(string block)
    {
        // Act
        var segments = Build($"Before.\n\n{block}\n\nAfter.\n\nLast.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html), html.BlockStarts.ToArray());
    }

    [Fact]
    public void WhenInlineHtmlOpensAnElementWithASlashAtTheEnd_ThenNoBlockStartsAfterIt()
    {
        // Act
        var segments = Build("Before.\n\nJump <a name=\"top\"/> here.\n\nAfter.\n\nLast.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html, "<p>Jump"), html.BlockStarts.ToArray());
    }

    [Fact]
    public void WhenACommentIsNeverEnded_ThenNoBlockStartsAfterIt()
    {
        // Act
        var segments = Build("Before.\n\n:: {.box}\n<!-- note\n::\n\nAfter.\n\nLast.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html, "<div class=\"box\">"), html.BlockStarts.ToArray());
    }

    [Fact]
    public void WhenPageTextContainsABreakMarker_ThenItIsNoBreak()
    {
        // Act
        var segments = Build("One <!--BREAK:00000000000000000000000000000000--> two.");

        // Assert
        var html = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segments));
        Assert.Equal(BlockStartsOfPage(html.Html), html.BlockStarts.ToArray());
        Assert.Contains("<!--BREAK:00000000000000000000000000000000-->", html.Html);
    }

    [Fact]
    public void WhenAWidgetBlockIsBetweenParagraphs_ThenBlocksStartBeforeAndAfterIt()
    {
        // Act
        var segments = Build("First.\n\n:: Demo.Card\ninside\n::\n\nAfter.");

        // Assert
        Assert.Collection(segments,
            segment => Assert.Equal(new HtmlMarkdownSegment("<p>First.</p>\n", [0, 14]), segment),
            segment => Assert.IsType<BlockMarkdownSegment>(segment),
            segment => Assert.Equal(new HtmlMarkdownSegment("\n<p>After.</p>\n", [0, 15]), segment));
    }

    [Fact]
    public void WhenAWidgetIsAfterRawHtmlThatOpensAnElement_ThenBlocksStillStartAtTheWidget()
    {
        // Act
        var segments = Build("<div>\n\n:: Demo.Card\n::\n\nAfter.\n\nLast.");

        // Assert
        Assert.Collection(segments,
            segment => Assert.Equal(new HtmlMarkdownSegment("<div>\n", [0, 6]), segment),
            segment => Assert.IsType<BlockMarkdownSegment>(segment),
            segment =>
            {
                var html = Assert.IsType<HtmlMarkdownSegment>(segment);
                Assert.Equal([0, html.Html.Length], html.BlockStarts.ToArray());
            });
    }

    [Fact]
    public void WhenABlockStartsBeforeOrAfterAnExpression_ThenTheOffsetIsAtTheEndOrTheStartOfTheHtml()
    {
        // Arrange
        var token = new string('a', 32);

        // Act
        var segments = MarkdownSegmentBuilder.Split(
            $"<p>First.</p>\n<!--BREAK:{token}--><!--EXPR:{token}:0--><!--BREAK:{token}--><p>After.</p>\n",
            MarkdownPageParser.Parse("First."),
            token,
            ["a/b"]);

        // Assert
        Assert.Equal(
            [
                new HtmlMarkdownSegment("<p>First.</p>\n", [0, 14]),
                new ExpressionMarkdownSegment("a/b"),
                new HtmlMarkdownSegment("<p>After.</p>\n", [0, 14])
            ],
            segments);
    }

    [Fact]
    public void WhenFrontMatterIsTheFirstBlock_ThenTheFirstHtmlStartsTheFirstBlock()
    {
        // Act
        var segments = Build("---\ntitle: Test\n---\n\nFirst.\n\nSecond.");

        // Assert
        Assert.Equal([new HtmlMarkdownSegment("<p>First.</p>\n<p>Second.</p>\n", [0, 14, 29])], segments);
    }

    [Fact]
    public void WhenBlockStartsAreEmpty_ThenTheSegmentEqualsOneWithoutThem()
    {
        // Act
        var segment = new HtmlMarkdownSegment("<p>x</p>", []);

        // Assert
        Assert.True(segment.BlockStarts.IsDefault);
        Assert.Equal(new HtmlMarkdownSegment("<p>x</p>"), segment);
        Assert.NotEqual(new HtmlMarkdownSegment("<p>x</p>", [0]), segment);
    }

    // A page of one segment: a block starts at its start, before each of the texts and at its end.
    private static int[] BlockStartsOfPage(string html, params string[] texts) =>
        [0, .. texts.Select(text => BlockStart(html, text)), html.Length];

    private static int BlockStart(string html, string text)
    {
        var index = html.IndexOf(text, StringComparison.Ordinal);
        Assert.True(index > 0, $"'{text}' is not in the HTML after its start.");
        return index;
    }

    // What a browser shows for the HTML: character references are decoded.
    private static string VisibleText(IEnumerable<MarkdownSegment> segments) =>
        WebUtility.HtmlDecode(AllHtml(segments));
}
