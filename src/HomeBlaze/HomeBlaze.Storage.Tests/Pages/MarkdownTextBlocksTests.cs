using HomeBlaze.Storage.Pages;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownTextBlocksTests
{
    [Fact]
    public void WhenAPageHasTextOfEveryKind_ThenEachTopLevelBlockIsATextBlockWithItsLines()
    {
        // Arrange
        const string source =
            "# Title\n" +
            "\n" +
            "A paragraph\non two lines.\n" +
            "\n" +
            "- one\n- two\n  - nested\n" +
            "\n" +
            "> quote\n" +
            "\n" +
            "```\ncode\n```\n" +
            "\n" +
            "| a | b |\n|---|---|\n| 1 | 2 |\n" +
            "\n" +
            "---\n";
        var page = MarkdownPageParser.Parse(source);

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 1), (3, 4), (6, 8), (10, 10), (12, 14), (16, 18), (20, 20)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine)));
        Assert.All(textBlocks, textBlock => Assert.True(textBlock.IsEditable));
        Assert.All(textBlocks, textBlock => Assert.Null(textBlock.Owner));
    }

    [Fact]
    public void WhenABlockHasContent_ThenItsTextNamesTheBlockAndTheJsonBlockAndTheFrontMatterAreLeftOut()
    {
        // Arrange
        const string source =
            "---\ntitle: T\n---\n\n" +
            ":: Demo.Card\n```json\n{ \"Title\": \"A\" }\n```\nInside.\n" +
            "::: Footer\nFoot.\n:::\n" +
            "::\n";
        var page = MarkdownPageParser.Parse(source);
        var card = page.Blocks[0];

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal([(9, 9), (11, 11)], textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine)));
        Assert.Same(card, textBlocks[0].Owner);
        Assert.Same(card.Children[0], textBlocks[1].Owner);
    }

    [Theory]
    [InlineData("<script>\nlet a = 1;\n</script>", HtmlBlockType.ScriptPreOrStyle)]
    [InlineData("<pre>\n<div>\n</pre>", HtmlBlockType.ScriptPreOrStyle)]
    [InlineData("<!--\nc\n\nd\n-->", HtmlBlockType.Comment)]
    [InlineData("<?xml version=\"1.0\"?>", HtmlBlockType.ProcessingInstruction)]
    [InlineData("<!DOCTYPE html>", HtmlBlockType.DocumentType)]
    [InlineData("<![CDATA[\n<div>\n]]>", HtmlBlockType.CData)]
    [InlineData("<div align=\"center\">\n<img src=\"a.png\"><br/>\n</div>", HtmlBlockType.InterruptingBlock)]
    [InlineData("<my-element>\nx\n</my-element>", HtmlBlockType.NonInterruptingBlock)]
    public void WhenRawHtmlClosesWhatItOpens_ThenItIsATextBlockOfItsOwnAndTheTextAroundItCanBeEdited(string html, HtmlBlockType type)
    {
        // Arrange
        var htmlLineCount = html.Count(character => character == '\n') + 1;
        var page = MarkdownPageParser.Parse($"Before.\n\n{html}\n\nAfter.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 1, true, false), (3, 2 + htmlLineCount, false, true), (4 + htmlLineCount, 4 + htmlLineCount, true, false)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable, textBlock.HoldsHtml)));
        Assert.Equal(type, Assert.IsType<HtmlBlock>(textBlocks[1].FirstBlock).Type);
    }

    [Fact]
    public void WhenRawHtmlOpensAnElementThatALaterBlockCloses_ThenTheBlocksUpToTheClosingOneAreOneTextBlockThatCannotBeEdited()
    {
        // Arrange: the comment and the void element inside open nothing, the details element is closed by the last HTML block.
        var page = MarkdownPageParser.Parse(
            "Before.\n\n<details>\n<summary>More</summary>\n\nInside.\n\n<!-- </details> -->\n\n<br>\n\n</details>\n\nAfter.\n\n" +
            ":: Demo.One\n::\n\nLast.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 1, true, false), (3, 12, false, true), (14, 14, true, false), (19, 19, true, false)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable, textBlock.HoldsHtml)));
    }

    // Each case keeps text in an element that a browser leaves open, so the text up to the end of the run is one text block
    // that cannot be edited and leaves the HTML open. HtmlGroupingTests of the E2E tests compare this with a browser.
    [Theory]
    [InlineData("<div/>\n\nInside.\n")]
    [InlineData("<a name=\"x\"/>\n\nInside.\n")]
    [InlineData("<custom-x/>\n\nInside.\n")]
    [InlineData("<div>x</div> <!-- open\n\nInside.\n")]
    [InlineData("<div title=\"open>\n\nInside.\n")]
    [InlineData("<div></div><!DOCTYPE\n\nInside.\n")]
    [InlineData("Text with <b>bold\n\nInside.\n")]
    [InlineData("- An item with <b>bold\n\nInside.\n")]
    public void WhenHtmlLeavesSomethingOpenUpToTheEndOfTheRun_ThenTheRunFromItIsOneTextBlockThatLeavesTheHtmlOpen(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source + "\n:: Demo.One\n::\n\nAfter.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 3, false, true, true), (8, 8, true, false, false)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable, textBlock.HoldsHtml, textBlock.LeavesHtmlOpen)));
    }

    [Theory]
    [InlineData("Text with `<div>` in code.\n\nInside.\n")]
    [InlineData("Text with <b>bold</b> and <br> inline.\n\nInside.\n")]
    public void WhenInlineHtmlClosesWhatItOpens_ThenTheTextAroundItCanBeEdited(string source)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal([(1, 1, true), (3, 3, true)], textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable)));
    }

    [Fact]
    public void WhenRawHtmlShowsNothing_ThenItsTextBlockSaysSo()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("<!-- c -->\n\n<script>let a;</script>\n\n<div>x</div>\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal([true, true, false], textBlocks.Select(textBlock => textBlock.ShowsNothing));
    }

    [Fact]
    public void WhenAnElementOfRawHtmlIsNotClosedBeforeAWidget_ThenItsTextBlockEndsWithTheRun()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("<div>\n\nInside.\n\n:: Demo.One\n::\n\n</div>\n\nAfter.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 3, false, true), (8, 8, false, true), (10, 10, true, false)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable, textBlock.HoldsHtml)));
    }

    [Fact]
    public void WhenAPageHasAStyleBlockAnErrorAndFootnotes_ThenTheyAreTextBlocksThatCannotBeEdited()
    {
        // Arrange: a closing line that closes no block is an error block of one line.
        var page = MarkdownPageParser.Parse(":: {.ab}\nStyled.\n::\n\nText[^1].\n\n::\n\n[^1]: Note.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 3, false), (5, 5, true), (7, 7, false), (9, 9, false)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable)));
    }

    [Fact]
    public void WhenAnIndentedCodeBlockIsFollowedByEmptyLines_ThenItEndsAtItsLastCodeLine()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Text.\n\n    code\n    more\n\n\n:: Demo.One\n::\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal([(1, 1), (3, 4)], textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine)));
    }

    [Fact]
    public void WhenAListItemHoldsAWidgetWithABlockInside_ThenTheListIsTextThatCannotBeEdited()
    {
        // Arrange: the widget in the list item is an invalid block, whose inner block the parser does not read.
        var page = MarkdownPageParser.Parse("- item\n\n  :: Demo.Card\n  ::: Demo.One\n  :::\n  ::\n\nAfter.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 6, false), (8, 8, true)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable)));
    }

    [Fact]
    public void WhenThePageHasALoneCarriageReturn_ThenNoTextBlockCanBeEdited()
    {
        // Arrange: Markdig ends a line at the lone carriage return, the lines of the page count line feeds only.
        var page = MarkdownPageParser.Parse("x\rb\n\nText.\n\n:: Demo.One\n::\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.NotEmpty(textBlocks);
        Assert.All(textBlocks, textBlock => Assert.False(textBlock.IsEditable));
    }
}
