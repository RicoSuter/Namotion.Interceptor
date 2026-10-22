using HomeBlaze.Storage.Pages;

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

    [Fact]
    public void WhenARunOfTextHoldsRawHtml_ThenTheRunIsOneTextBlockThatCannotBeEdited()
    {
        // Arrange
        var page = MarkdownPageParser.Parse("Before.\n\n<div>\n\nInside.\n\n</div>\n\n:: Demo.One\n::\n\nAfter.\n");

        // Act
        var textBlocks = MarkdownTextBlocks.Find(page);

        // Assert
        Assert.Equal(
            [(1, 7, false), (12, 12, true)],
            textBlocks.Select(textBlock => (textBlock.Line, textBlock.EndLine, textBlock.IsEditable)));
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
}
