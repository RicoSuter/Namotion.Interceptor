using HomeBlaze.Storage.Pages;
using Markdig.Extensions.DefinitionLists;
using Markdig.Syntax;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownPageParserTests
{
    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    [Fact]
    public void WhenPageHasNestedBlocks_ThenTreeFollowsTheFences()
    {
        // Arrange
        var source = Normalize("""
            ---
            title: Test
            ---

            text

            :: Demo.TabLayout#tabs
            ::: Tab#cooling Title="Cooling Fan {x}"
            The fan runs at {{ ../CoolingFan/CurrentSpeed }} RPM.

            :::: Demo.Widget Path="/House/{Fan}"
            ::::
            :::
            ::: Tab Title="Exhaust Fan"
            More content.
            :::
            ::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var tabs = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, tabs.Kind);
        Assert.Equal("tabs", tabs.Head!.Id);
        Assert.Equal((2, 7, 17), (tabs.FenceLength, tabs.Line, tabs.EndLine));
        Assert.Equal(":: Demo.TabLayout#tabs", source.Substring(tabs.HeadSpan.Start, tabs.HeadSpan.Length));

        Assert.Equal(2, tabs.Children.Count);
        Assert.All(tabs.Children, tab => Assert.Equal(MarkdownBlockKind.Slot, tab.Kind));
        var cooling = tabs.Children[0];
        Assert.Equal((3, 8, 13), (cooling.FenceLength, cooling.Line, cooling.EndLine));
        Assert.Equal(
            """::: Tab#cooling Title="Cooling Fan {x}" """.TrimEnd(),
            source.Substring(cooling.HeadSpan.Start, cooling.HeadSpan.Length));
        var exhaust = tabs.Children[1];
        Assert.Equal((3, 14, 16), (exhaust.FenceLength, exhaust.Line, exhaust.EndLine));
        Assert.Empty(exhaust.Children);

        var widget = Assert.Single(cooling.Children);
        Assert.Equal(MarkdownBlockKind.Widget, widget.Kind);
        Assert.Equal((4, 11, 12), (widget.FenceLength, widget.Line, widget.EndLine));
        Assert.Equal(4, widget.Head!.FenceLength);
        Assert.Equal("/House/{Fan}", widget.Head.Arguments[0].Value);

        Assert.Equal([tabs, cooling, widget, exhaust], page.AllBlocks);
        Assert.Equal([0, 1, 2, 3], page.AllBlocks.Select(block => block.Index));
    }

    [Theory]
    [InlineData(":: Demo.Motor#m Speed=3000")]
    [InlineData("::Demo.Motor#m Speed=3000")]
    [InlineData("::   Demo.Motor#m Speed=3000")]
    public void WhenBlockHasHeadLine_ThenHeadSpanCoversExactlyThatLine(string headLine)
    {
        // Arrange
        var source = $"before\n\n{headLine}\n::\n";

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).Blocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, block.Kind);
        Assert.Equal("Demo.Motor", block.Head!.Name);
        Assert.Equal("m", block.Head.Id);
        Assert.Equal(2, block.FenceLength);
        Assert.Equal(headLine, source.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenTopLevelBlockHasMoreThanTwoCharacters_ThenItIsATopLevelBlock()
    {
        // Arrange
        var source = Normalize("""
            ::: Demo.Card
            text
            :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, block.Kind);
        Assert.Equal((3, 1, 3), (block.FenceLength, block.Line, block.EndLine));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("   ")]
    public void WhenBlockIsIndentedByUpToThreeSpaces_ThenItIsABlockAndTheHeadSpanStartsAtTheFence(string indentation)
    {
        // Arrange
        var source = $"before\n\n{indentation}:: Demo.Card\n{indentation}text\n{indentation}::\n\nafter";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var block = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, block.Kind);
        Assert.Equal((3, 5), (block.Line, block.EndLine));
        Assert.Equal(":: Demo.Card", source.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenFenceLineIsIndentedByFourSpaces_ThenItIsNoBlock()
    {
        // Arrange
        const string source = "before\n\n    :: Demo.Card\n    ::\n\nafter";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Empty(page.Blocks);
        Assert.True(page.IsInsideCode(source.IndexOf("Demo.Card", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(": Demo.Card\n:")]
    [InlineData("text\n: Demo.Card\n:")]
    [InlineData(":Demo.Card")]
    public void WhenLineStartsWithOneFenceCharacter_ThenItIsNoBlock(string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Empty(page.Blocks);
    }

    [Fact]
    public void WhenFirstChildIsJsonFence_ThenItIsTheConfiguration()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Grid Rows=1
            ```json
            { "Cells": [1, 2] }
            ```
            ::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal("{ \"Cells\": [1, 2] }", block.ConfigurationJson);
        var span = block.ConfigurationSpan!.Value;
        Assert.StartsWith("```json", source.Substring(span.Start, span.Length));
        Assert.EndsWith("```", source.Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("- item\n  :: Demo.Card\n  text\n  ::")]
    [InlineData("> quote\n>\n> :: Demo.Card\n> text\n> ::")]
    public void WhenBlockIsInsideListItemOrQuote_ThenItIsInvalid(string source)
    {
        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Equal("A block is only allowed directly on the page or directly inside another block.", block.Error);
    }

    [Theory]
    [InlineData(":: {.band}")]
    [InlineData("::{.band}")]
    [InlineData("::: {.band #top}")]
    public void WhenUnnamedBlockHasAttributes_ThenItIsAStyleBlock(string headLine)
    {
        // Arrange
        var source = $"{headLine}\ntext\n{headLine[..headLine.IndexOf('{')].TrimEnd()}\n";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var block = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Style, block.Kind);
        Assert.Equal((1, 3), (block.Line, block.EndLine));
        Assert.Empty(page.AllBlocks);
    }

    [Fact]
    public void WhenWidgetIsInsideStyleBlock_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :: {.band}
            ::: Demo.Card
            :::
            ::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var block = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("only allowed directly on the page", block.Error);
    }

    [Fact]
    public void WhenNestedBlockHasNoMoreCharactersThanTheBlockAroundIt_ThenItIsInvalidAndRunsToItsOwnClosingLine()
    {
        // Arrange
        var source = Normalize("""
            intro

            :: Demo.Card
            before
            :: Demo.Widget Path=/x
            inside
            ::
            after
            ::
            rest
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((3, 9), (card.Line, card.EndLine));
        var widget = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, widget.Kind);
        Assert.Equal((5, 7), (widget.Line, widget.EndLine));
        Assert.Equal(
            "A block inside another block needs more ':' characters than the block around it. " +
            "This block has 2 and the block around it, at line 3, has 2. Write ':::' here and on its closing line.",
            widget.Error);
        Assert.Equal(2, page.AllBlocks.Count);
    }

    [Fact]
    public void WhenNestedBlockHasFewerCharactersThanTheBlockAroundIt_ThenTheErrorNamesBothCounts()
    {
        // Arrange
        var source = Normalize("""
            :::: Demo.Card
            ::: Footer
            text
            :::
            ::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var footer = Assert.Single(Assert.Single(page.Blocks).Children);
        Assert.Equal(MarkdownBlockKind.Invalid, footer.Kind);
        Assert.Contains("This block has 3 and the block around it, at line 1, has 4.", footer.Error);
        Assert.Contains("Write ':::::' here", footer.Error);
    }

    [Fact]
    public void WhenOnlyTheHeadLineOfANestedBlockHasTooFewCharacters_ThenItsClosingLineClosesItWithoutAnErrorOfItsOwn()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Card
            :: Demo.Motor#m
            :::
            text
            ::
            more page text
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
        var motor = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, motor.Kind);
        Assert.Equal((2, 3), (motor.Line, motor.EndLine));
        Assert.Equal(
            "A block inside another block needs more ':' characters than the block around it. " +
            "This block has 2 and the block around it, at line 1, has 2. Write ':::' here and on its closing line.",
            motor.Error);
        Assert.Equal(2, page.AllBlocks.Count);
    }

    [Theory]
    [InlineData(":::: Demo.Card\n::: Demo.Motor\n:::::\ntext\n::::", 2, 3)]
    [InlineData(":: Demo.Card\n:: Demo.Motor\n:::::\ntext\n::", 2, 3)]
    [InlineData(":: Demo.Flow\n::: Item\n::: Demo.Card\ninside\n::::\ntext\n:::\n::", 3, 5)]
    [InlineData(":: Demo.Card\n:: {.band}\ninside\n:::\ntext\n::", 2, 4)]
    [InlineData(":: Demo.Card\n:: Please note this\n:::\ntext\n::", 2, 3)]
    public void WhenClosingLineHasMoreCharactersThanTheBlockAroundABlockWithTooFew_ThenItClosesThatBlock(
        string source, int expectedLine, int expectedEndLine)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var outer = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, outer.Kind);
        Assert.Equal(source.Split('\n').Length, outer.EndLine);
        var invalid = Assert.Single(page.AllBlocks, block => block.Kind == MarkdownBlockKind.Invalid);
        Assert.Equal((expectedLine, expectedEndLine), (invalid.Line, invalid.EndLine));
        Assert.DoesNotContain("closes no block", invalid.Error);
        Assert.DoesNotContain("not closed", invalid.Error);
    }

    [Theory]
    // The innermost open block has enough characters for its place.
    [InlineData(":: Demo.Card\n::: Demo.Motor\n::::\n:::\n::", 3, "Open here: line 1 (2 characters), line 2 (3 characters).")]
    // A block directly on the page has no block around it.
    [InlineData(":: Demo.Card\n:::\n::", 2, "Open here: line 1 (2 characters).")]
    public void WhenClosingLineFitsNoBlockAndNoBlockWithTooFewCharactersIsItsTarget_ThenItStaysAnError(
        string source, int expectedLine, string expectedEnding)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var closingLine = Assert.Single(page.AllBlocks, block => block.Error?.Contains("closes no block") == true);
        Assert.Equal(expectedLine, closingLine.Line);
        Assert.EndsWith(expectedEnding, closingLine.Error);
        Assert.DoesNotContain(page.AllBlocks, block => block.Error?.Contains("not closed") == true);
    }

    [Theory]
    // The line has no more characters than the block around the block with too few.
    [InlineData(":::: Demo.Card\n:: Demo.Motor\n:::\n::\n::::")]
    // The line is inside a quote, which it does not reach out of.
    [InlineData(":: Demo.Card\n:: Demo.Motor\n> :::\n::\n::")]
    public void WhenClosingLineIsNoneThatCouldBeMeantForTheBlockWithTooFewCharacters_ThenThatBlockRunsToTheLineOfItsOwnLength(
        string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
        var motor = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, motor.Kind);
        Assert.Equal((2, 4), (motor.Line, motor.EndLine));
    }

    [Fact]
    public void WhenBlockIsNotClosedBecauseABlockInsideItWithAsManyCharactersTookTheClosingLine_ThenTheErrorNamesBothLines()
    {
        // Arrange
        var source = Normalize("""
            intro

            :: Demo.Card
            :: note that this is text
            more
            ::
            after
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Invalid, card.Kind);
        Assert.Equal(3, card.Line);
        Assert.Equal(
            "This block is not closed. The line '::' that ends it is missing. " +
            "The '::' at line 6 closed the block at line 4, which is inside this block and has 2 characters as well.",
            card.Error);
    }

    [Theory]
    // The first closing line that a block inside took is the one that is named.
    [InlineData(":: Demo.Card\n:: Demo.A\n:: Demo.B\n::\n::\n:: Demo.C\n::", "The '::' at line 4 closed the block at line 3,")]
    // Also a block deeper inside takes a closing line of that length.
    [InlineData(":: Demo.Card\n::: Footer\n:: Demo.B\n::\n:::", "The '::' at line 4 closed the block at line 3,")]
    public void WhenSeveralBlocksInsideAnUnclosedBlockTookAClosingLine_ThenTheErrorNamesTheFirstOne(string source, string expectedPart)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Contains(expectedPart, card.Error);
    }

    [Theory]
    // No block inside it took a closing line.
    [InlineData(":: note that this is text\nmore\nafter", "Expected key=value but found 'that this is text'. This block is not closed. The line '::' that ends it is missing.")]
    [InlineData(":: Demo.Card\n::: Demo.Motor\n:::\ntext", "This block is not closed. The line '::' that ends it is missing.")]
    // The block inside it was closed by a line with more characters, which could not have closed this block.
    [InlineData("::: Demo.Card\n::: Demo.Motor\n::::\ntext", "This block is not closed. The line ':::' that ends it is missing.")]
    // The closing line was inside a quote, which it does not reach out of.
    [InlineData(":: Demo.Card\n> :: Demo.Motor\n> ::\n\ntext", "This block is not closed. The line '::' that ends it is missing.")]
    public void WhenBlockIsNotClosedAndNoBlockInsideItTookItsClosingLine_ThenTheErrorIsAsBefore(string source, string expectedError)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(expectedError, page.Blocks[0].Error);
    }

    [Fact]
    public void WhenStyleBlockInsideABlockHasNoMoreCharacters_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Card
            :: {.band}
            text
            ::
            ::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        var style = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, style.Kind);
        Assert.Contains("needs more ':' characters", style.Error);
    }

    [Fact]
    public void WhenClosingLineMatchesNoOpenBlock_ThenItIsAnInvalidBlockOfOneLineThatNamesTheOpenBlocks()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Card
            ::: Footer
            text
            ::::
            more
            :::
            ::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        var footer = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Slot, footer.Kind);
        Assert.Equal((2, 6), (footer.Line, footer.EndLine));
        var closingLine = Assert.Single(footer.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, closingLine.Kind);
        Assert.Equal((4, 4, 4), (closingLine.FenceLength, closingLine.Line, closingLine.EndLine));
        Assert.Equal("::::", source.Substring(closingLine.HeadSpan.Start, closingLine.HeadSpan.Length));
        Assert.Equal(
            "This line of 4 ':' characters closes no block. A closing line needs as many characters as the head line " +
            "of its block. Open here: line 1 (2 characters), line 2 (3 characters).",
            closingLine.Error);
    }

    [Theory]
    [InlineData("text\n\n::\n\nafter")]
    [InlineData("text\n:::\nafter")]
    [InlineData(":: Demo.Card\n::\n\n::   \n\nafter")]
    public void WhenClosingLineHasNoOpenBlock_ThenItIsInvalidAndSaysThatNoBlockIsOpen(string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var closingLine = Assert.Single(page.AllBlocks, block => block.Kind == MarkdownBlockKind.Invalid);
        Assert.Contains("closes no block. No block is open here.", closingLine.Error);
        Assert.Empty(closingLine.Children);
    }

    [Fact]
    public void WhenBlockIsNotClosedAtTheEndOfThePage_ThenItIsInvalidAndNamesTheMissingLine()
    {
        // Arrange
        var source = Normalize("""
            before

            ::: Demo.Card
            text
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Invalid, card.Kind);
        Assert.Equal(3, card.Line);
        Assert.True(card.EndLine > 4);
        Assert.Equal("This block is not closed. The line ':::' that ends it is missing.", card.Error);
    }

    [Fact]
    public void WhenClosingLineClosesOuterBlockWhileInnerIsOpen_ThenInnerIsNotClosedAndOuterIsFine()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Card
            before
            ::: Demo.Widget Path=/x
            inside
            ::
            after
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
        var widget = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, widget.Kind);
        Assert.Equal((3, 5), (widget.Line, widget.EndLine));
        Assert.Equal("This block is not closed. The line ':::' that ends it is missing.", widget.Error);
    }

    [Fact]
    public void WhenBlockWithAnotherErrorIsNotClosed_ThenTheErrorSaysBoth()
    {
        // Arrange
        const string source = "before\n\n:: Demo.Card Title=\"open\ntext";

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("closing quote", block.Error);
        Assert.EndsWith("This block is not closed. The line '::' that ends it is missing.", block.Error);
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~~")]
    [InlineData("~~~~markdown")]
    public void WhenFenceLineIsInsideFencedCodeInsideABlock_ThenTheBlockContinues(string codeFence)
    {
        // Arrange
        var closingCodeFence = codeFence.TrimEnd("markdown".ToCharArray());
        var source = $":: Demo.Card\nbefore\n\n{codeFence}\n::: Demo.Widget Path=/x\n:::\n::\n:::\n{closingCodeFence}\n\nafter\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 12), (card.Line, card.EndLine));
        Assert.Empty(card.Children);
        Assert.True(page.IsInsideCode(source.IndexOf("::: Demo.Widget", StringComparison.Ordinal)));
        Assert.False(page.IsInsideCode(source.IndexOf("after", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhenFenceLineIsInsideIndentedCodeInsideABlock_ThenTheBlockContinues()
    {
        // Arrange
        const string source = ":: Demo.Card\nbefore\n\n    ::: Demo.Widget Path=/x\n    :::\n    ::\n\nafter\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 9), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenClosingLineFollowsAnIndentedCodeBlock_ThenItClosesTheBlock()
    {
        // Arrange
        const string source = ":: Demo.Card\nbefore\n\n    code\n::\nrest";

        // Act
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenCodeFenceInsideABlockIsNotClosed_ThenTheBlockIsNotClosedEither()
    {
        // Arrange
        const string source = "before\n\n:: Demo.Card\n```\ncode\n::\nrest";

        // Act
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, card.Kind);
        Assert.Contains("is not closed", card.Error);
        Assert.True(card.EndLine >= 7);
    }

    [Theory]
    [InlineData("<div class=\"row\">\n<span>a</span>\n</div>")]
    [InlineData("<pre>\ntext\n</pre>")]
    public void WhenClosingLineFollowsHtmlWithoutAnEmptyLine_ThenItStillClosesTheBlock(string html)
    {
        // Arrange
        var source = $":: Demo.Card\n{html}\n::\nrest";
        var lineCount = source.Split('\n').Length;

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, lineCount - 1), (card.Line, card.EndLine));
    }

    [Theory]
    [InlineData("<!--\ncomment")]
    [InlineData("<pre>\ntext")]
    [InlineData("<script>\nvar x = 1;")]
    [InlineData("<style>\np { color: red; }")]
    public void WhenClosingLineIsInsideMultiLineHtml_ThenItClosesTheBlock(string html)
    {
        // Arrange
        var source = $":: Demo.Card\n{html}\n::\nrest";

        // Act
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 4), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenClosingLineIsInsideHtmlInsideAListItem_ThenItStaysTextAndTheBlockAroundTheListStaysOpen()
    {
        // Arrange
        const string source = ":: Demo.Card\n- <div>\n  ::\n  </div>\n::\nrest";

        // Act
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenFenceLinesFollowHtmlWithoutAnEmptyLineAndCloseNoOpenBlock_ThenTheyStayText()
    {
        // Arrange
        const string source = ":: Demo.Card\n<div>\n::: Demo.Widget Path=/x\n:::\n</div>\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 6), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenListInsideABlockIsFollowedByTheClosingLine_ThenTheBlockIsClosed()
    {
        // Arrange
        const string source = ":: Demo.Card\n- one\n- two\n::\nrest";

        // Act
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 4), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenBlockInsideAListItemIsLeftOpenAndTheListEnds_ThenTheClosingLineClosesTheBlockAroundTheList()
    {
        // Arrange
        const string source = ":: Demo.Card\n- item\n  ::: Demo.Widget Path=/x\n  text\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
        var widget = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, widget.Kind);
        Assert.Contains("only allowed directly on the page", widget.Error);
        Assert.Contains("is not closed", widget.Error);
    }

    [Fact]
    public void WhenBlockInterruptsAParagraph_ThenItIsABlock()
    {
        // Arrange
        const string source = "text\n:: Demo.Card\ninside\n::\nafter";

        // Act
        var card = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((2, 4), (card.Line, card.EndLine));
    }

    [Fact]
    public void WhenPageHasWindowsLineEndings_ThenBlocksNestAndTheHeadSpanHasNoCarriageReturn()
    {
        // Arrange
        const string source = ":: Demo.Card\r\n::: Footer\r\ntext\r\n:::\r\n::\r\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal(":: Demo.Card", source.Substring(card.HeadSpan.Start, card.HeadSpan.Length));
        Assert.Equal(MarkdownBlockKind.Slot, Assert.Single(card.Children).Kind);
    }

    [Fact]
    public void WhenSlotBlockIsAtPageLevel_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :: Footer
            text
            ::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("not a type name", block.Error);
    }

    [Fact]
    public void WhenHeadLineIsMalformed_ThenBlockCarriesTheGrammarError()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Card Title="open
            ::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Equal("The value of 'Title' has no closing quote.", block.Error);
    }

    [Theory]
    [InlineData(":: A.B\nTerm\n:   Definition\n::\nafter", 4)]
    [InlineData(":: A.B\nTerm\n:   Definition\n\n::\nafter", 5)]
    [InlineData(":: A.B\nTerm\n~   Definition\n::\nafter", 4)]
    [InlineData(":: A.B\nTerm\n:   Definition\n\n# Heading\n::\nafter", 6)]
    [InlineData(":: A.B\nTerm\n:   Definition\n# Heading\n::\nafter", 5)]
    [InlineData(":: A.B\nTerm\n:   One\n:   Two\n::\nafter", 5)]
    [InlineData(":: A.B\nTerm\n:   Definition\n   ::\nafter", 4)]
    [InlineData(":: A.B\nTerm\n:   Definition\n::  \nafter", 4)]
    public void WhenClosingLineFollowsADefinitionList_ThenItClosesTheBlock(string source, int expectedEndLine)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var block = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, block.Kind);
        Assert.Equal((1, expectedEndLine), (block.Line, block.EndLine));
    }

    [Fact]
    public void WhenClosingLineOfAnInnerBlockFollowsADefinitionList_ThenItIsReadWithAllItsCharacters()
    {
        // Arrange
        const string source = ":: A.B\n::: Tab\nTerm\n:   Definition\n:::\nmore\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var outer = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, outer.Kind);
        Assert.Equal((1, 7), (outer.Line, outer.EndLine));
        var tab = Assert.Single(outer.Children);
        Assert.Equal(MarkdownBlockKind.Slot, tab.Kind);
        Assert.Equal((2, 5), (tab.Line, tab.EndLine));
    }

    [Theory]
    [InlineData("Term\n:   Definition\n\n:: A.B\ninside\n::", 4)]
    [InlineData("Term\n:   Definition\n:: A.B\ninside\n::", 3)]
    [InlineData("Term\n:   Definition\n\n  :: A.B\ninside\n::", 4)]
    [InlineData("Term\n:   Definition\n   :: A.B\ninside\n::", 3)]
    [InlineData("Term\n:   One\n:   Two\n\n   :: A.B\ninside\n   ::", 5)]
    public void WhenHeadLineFollowsADefinitionList_ThenItOpensABlockAndTheHeadSpanStartsAtTheFence(string source, int expectedLine)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var block = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, block.Kind);
        Assert.Equal((2, expectedLine, expectedLine + 2), (block.FenceLength, block.Line, block.EndLine));
        Assert.Equal(":: A.B", source.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenClosingLineThatClosesNothingFollowsADefinitionList_ThenTheErrorCountsAllItsCharacters()
    {
        // Arrange
        const string source = "Term\n:   Definition\n:::";

        // Act
        var closingLine = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, closingLine.Kind);
        Assert.Equal(3, closingLine.FenceLength);
        Assert.Equal(":::", source.Substring(closingLine.HeadSpan.Start, closingLine.HeadSpan.Length));
        Assert.Equal("This line of 3 ':' characters closes no block. No block is open here.", closingLine.Error);
    }

    [Theory]
    [InlineData(":: A.B\ntext\n> ::\nafter\n::", 3)]
    [InlineData(":: A.B\ntext\n- ::\nafter\n::", 3)]
    [InlineData(":: A.B\n- item\n  ::\nafter\n::", 3)]
    [InlineData(":: A.B\n1. item\n   ::\nafter\n::", 3)]
    [InlineData(":: A.B\n1. item\n\n   ::\nafter\n::", 4)]
    [InlineData(":: A.B\nTerm\n:   Definition\n\n    ::\n\nafter\n::", 5)]
    public void WhenClosingLineIsInsideAQuoteOrAListItem_ThenItClosesNothingAndSaysThatItReachesNoBlockOutside(
        string source, int expectedLine)
    {
        // Arrange
        var lineCount = source.Split('\n').Length;

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var outer = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, outer.Kind);
        Assert.Equal((1, lineCount), (outer.Line, outer.EndLine));
        var closingLine = Assert.Single(outer.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, closingLine.Kind);
        Assert.Equal((expectedLine, expectedLine), (closingLine.Line, closingLine.EndLine));
        Assert.Equal(
            "This line of 2 ':' characters closes no block. " +
            "A closing line inside a quote or a list item closes no block outside of it.",
            closingLine.Error);
    }

    [Theory]
    [InlineData("> text\n> ::\n\nafter")]
    [InlineData("- item\n  ::\n\nafter")]
    [InlineData(":: A.B\n::\n\n> ::\n\nafter")]
    public void WhenClosingLineIsInsideAQuoteOrAListItemAndNoBlockIsOpenAnywhere_ThenItSaysThatNoBlockIsOpen(string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var closingLine = Assert.Single(page.AllBlocks, block => block.Kind == MarkdownBlockKind.Invalid);
        Assert.Equal("This line of 2 ':' characters closes no block. No block is open here.", closingLine.Error);
    }

    [Fact]
    public void WhenClosingLineInsideAQuoteFitsNoBlockOfTheQuote_ThenItNamesTheBlocksOfTheQuoteAndSaysThatItReachesNoBlockOutside()
    {
        // Arrange
        const string source = ":: A.B\n> ::: {.band}\n> text\n> ::\n> more\n> :::\nafter\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var outer = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, outer.Kind);
        Assert.Equal((1, 8), (outer.Line, outer.EndLine));
        var band = Assert.Single(outer.Children);
        Assert.Equal(MarkdownBlockKind.Style, band.Kind);
        Assert.Equal((2, 6), (band.Line, band.EndLine));
        var closingLine = Assert.Single(band.Children);
        Assert.Equal(
            "This line of 2 ':' characters closes no block. A closing line needs as many characters as the head line " +
            "of its block. Open here: line 2 (3 characters). " +
            "A closing line inside a quote or a list item closes no block outside of it.",
            closingLine.Error);
    }

    [Theory]
    [InlineData("   ::")]
    [InlineData("   ::: C.D\ninside\n:::\n::")]
    public void WhenFenceWithThreeSpacesFollowsADefinitionAndAnEmptyLine_ThenTheDefinitionListIsAsWithTheFenceAtTheLineStart(
        string fenceLines)
    {
        // Arrange
        var indented = $":: A.B\nTerm\n:   One\n\n{fenceLines}\nafter\n";
        var plain = $":: A.B\nTerm\n:   One\n\n{fenceLines.TrimStart()}\nafter\n";

        // Act
        var indentedPage = MarkdownPageParser.Parse(indented);
        var plainPage = MarkdownPageParser.Parse(plain);

        // Assert
        static string Render(IEnumerable<MarkdownSegment> segments) => string.Concat(segments.Select(segment => segment switch
        {
            HtmlMarkdownSegment html => html.Html,
            BlockMarkdownSegment block => $"[{Render(block.Content)}]",
            _ => string.Empty
        }));

        var rendered = Render(MarkdownSegmentBuilder.Build(indentedPage));
        Assert.Equal(Render(MarkdownSegmentBuilder.Build(plainPage)), rendered);
        Assert.Contains("<dd>One</dd>", rendered);
        Assert.All(indentedPage.AllBlocks, block => Assert.Null(block.Error));

        var definitionList = Assert.Single(indentedPage.Document.Descendants<DefinitionList>());
        var endOfDefinition = indented.IndexOf("One", StringComparison.Ordinal) + "One".Length - 1;
        Assert.Equal(endOfDefinition, definitionList.Span.End);
        Assert.Equal(
            Assert.Single(plainPage.Document.Descendants<DefinitionList>()).Span.End,
            definitionList.Span.End);
        Assert.All(
            indentedPage.AllBlocks.Where(block => block.Line > 3),
            block => Assert.True(block.HeadSpan.Start > definitionList.Span.End));
    }

    [Fact]
    public void WhenClosingLineIsInsideAQuoteWithABlockOfItsOwn_ThenItClosesThatBlockOnly()
    {
        // Arrange
        const string source = ":: A.B\n> ::: C.D\n> text\n> :::\n> ::\nafter\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var outer = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, outer.Kind);
        Assert.Equal((1, 7), (outer.Line, outer.EndLine));
        Assert.Equal(2, outer.Children.Count);
        Assert.Equal((2, 4), (outer.Children[0].Line, outer.Children[0].EndLine));
        Assert.Contains("only allowed directly on the page", outer.Children[0].Error);
        Assert.DoesNotContain("not closed", outer.Children[0].Error);
        Assert.Equal(5, outer.Children[1].Line);
        Assert.Contains("closes no block. A closing line inside a quote or a list item closes no block outside of it.", outer.Children[1].Error);
    }

    [Fact]
    public void WhenProseInsideABlockStartsWithAFenceAndAWord_ThenTheErrorIsTheOneOfTheHeadGrammar()
    {
        // Arrange
        const string source = ":: Demo.Card\n:: Please note this\ntext\n::\nafter\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 6), (card.Line, card.EndLine));
        var prose = Assert.Single(card.Children);
        Assert.Equal(MarkdownBlockKind.Invalid, prose.Kind);
        Assert.Equal("Expected key=value but found 'note this'.", prose.Error);
    }

    [Theory]
    [InlineData(":: 12:30 lunch")]
    [InlineData("::)")]
    [InlineData("::1")]
    [InlineData(":: 1 is the loopback address")]
    [InlineData("::: - a dash")]
    [InlineData(":: #name")]
    [InlineData(":: \"quoted\"")]
    [InlineData("  :: 42")]
    public void WhenTextAfterAFenceStartsWithNoLetterUnderscoreOrBrace_ThenTheLineIsNoBlock(string line)
    {
        // Arrange
        var source = $"before\n\n{line}\n\n:: Demo.Card\n{line}\n::\n\nafter";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((5, 7), (card.Line, card.EndLine));
        Assert.Empty(card.Children);
    }

    [Theory]
    [InlineData("::note", "::", "'note' is not a type name. A slot block is only allowed directly inside a widget block.")]
    [InlineData(":: warning", "::", "'warning' is not a type name. A slot block is only allowed directly inside a widget block.")]
    [InlineData(":: Note: read this", "::", "'Note:' is not a valid block name.")]
    [InlineData(":: _draft text", "::", "Expected key=value but found 'text'.")]
    [InlineData(":: \u00dcbersicht", "::", "'\u00dcbersicht' is not a valid block name.")]
    [InlineData("::: {.band} trailing", ":::", "'{.band}' is not a valid block name.")]
    public void WhenTextAfterAFenceStartsWithALetterAnUnderscoreOrABrace_ThenTheLineIsAHeadLineWithTheGrammarError(
        string headLine, string closingLine, string expectedError)
    {
        // Arrange
        var source = $"before\n\n{headLine}\ntext\n{closingLine}\nafter";

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Equal((3, 5), (block.Line, block.EndLine));
        Assert.Equal(expectedError, block.Error);
    }

    [Fact]
    public void WhenFenceIsAloneOnItsLine_ThenItIsAClosingLineAlsoNextToTextThatIsNoHead()
    {
        // Arrange
        const string source = ":: Demo.Card\n::1\n::\n::2\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(2, page.AllBlocks.Count);
        Assert.Equal(MarkdownBlockKind.Widget, page.AllBlocks[0].Kind);
        Assert.Equal((1, 3), (page.AllBlocks[0].Line, page.AllBlocks[0].EndLine));
        Assert.Equal(MarkdownBlockKind.Invalid, page.AllBlocks[1].Kind);
        Assert.Equal(5, page.AllBlocks[1].Line);
        Assert.Contains("closes no block. No block is open here.", page.AllBlocks[1].Error);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 6)]
    [InlineData(4, 6)]
    [InlineData(5, 6)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(8, 12)]
    [InlineData(9, 12)]
    [InlineData(10, 12)]
    [InlineData(11, 12)]
    [InlineData(12, 12)]
    [InlineData(13, 13)]
    [InlineData(14, 14)]
    [InlineData(15, 15)]
    [InlineData(16, 16)]
    public void WhenNewBlockIsPlacedAtALineBeforeOrInsideAJsonBlock_ThenItGoesAfterTheJsonBlock(int line, int expectedLine)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Normalize("""
            intro
            :: Demo.Grid Rows=1
            ```json
            { "Cells": [] }
            ```
            text
            ::: Demo.Card

            ```json
            { "Title": "x" }
            ```
            inner
            :::
            ::
            end
            """));

        // Act
        var insertLine = page.GetLineForNewBlock(line);

        // Assert
        Assert.Equal(expectedLine, insertLine);
    }

    [Theory]
    [InlineData(":: Demo.Card\ntext\n::", 2, 2)]
    [InlineData(":: Demo.Card\n```\ncode\n```\n::", 3, 3)]
    [InlineData(":: Demo.Card Title=\"open\n```json\n{}\n```\n::", 3, 3)]
    [InlineData("```json\n{}\n```", 2, 2)]
    [InlineData("", 1, 1)]
    public void WhenNewBlockIsPlacedWhereNoJsonBlockOfABlockIs_ThenItStaysAtItsLine(string source, int line, int expectedLine)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var insertLine = page.GetLineForNewBlock(line);

        // Assert
        Assert.Equal(expectedLine, insertLine);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    [InlineData(8, 5)]
    [InlineData(9, 3)]
    [InlineData(10, 3)]
    [InlineData(11, 2)]
    [InlineData(12, 2)]
    public void WhenNewBlockIsInsertedBeforeALine_ThenItsFenceIsOneLongerThanTheFenceOfTheBlockAroundThatPlace(
        int line, int expectedFenceLength)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(Normalize("""
            intro
            :: Demo.Card
            text
            :::: Demo.TabLayout
            ::::: Tab
            inner
            :::::
            ::::
            after
            ::
            end
            """));

        // Act
        var fenceLength = page.GetFenceLengthForNewBlock(line);

        // Assert
        Assert.Equal(expectedFenceLength, fenceLength);
    }

    [Theory]
    [InlineData(":: Demo.Card\ntext", 2, 3)]
    [InlineData(":: Demo.Card\ntext", 3, 3)]
    [InlineData(":: Demo.Card\ntext\n", 3, 3)]
    [InlineData("::: {.band}\ntext\n:::", 2, 4)]
    [InlineData("::: {.band}\ntext\n:::", 3, 4)]
    [InlineData("text\n::\nmore", 2, 2)]
    [InlineData("text\n::\nmore", 3, 2)]
    [InlineData("", 1, 2)]
    public void WhenNewBlockIsInsertedIntoAnUnclosedBlockOrAStyleBlock_ThenItsFenceIsOneLonger(
        string source, int line, int expectedFenceLength)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(source);

        // Act
        var fenceLength = page.GetFenceLengthForNewBlock(line);

        // Assert
        Assert.Equal(expectedFenceLength, fenceLength);
    }

    [Theory]
    [InlineData("before\n\n```\n{{ a/B }}\n```\n\nafter", "```\n{{ a/B }}\n```")]
    [InlineData("before\n\n~~~~markdown\n{{ a/B }}\n~~~~\n\nafter", "~~~~markdown\n{{ a/B }}\n~~~~")]
    [InlineData("before `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("# Heading `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("- item\n- before `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("> quote\n> before `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("| A | B |\n|---|---|\n| x | before `{{ a/B }}` after |", "`{{ a/B }}`")]
    [InlineData(":: Demo.Card\nbefore `{{ a/B }}` after\n::", "`{{ a/B }}`")]
    [InlineData(":: Demo.Card\ntext\n\n```\n{{ a/B }}\n```\n::", "```\n{{ a/B }}\n```")]
    [InlineData("- item\n\n  ```\n  {{ a/B }}\n  ```\n\nafter", "```\n  {{ a/B }}\n  ```")]
    public void WhenPageHasMarkdownCode_ThenExactlyItsOffsetsAreInsideCode(string source, string code)
    {
        // Arrange
        var start = source.IndexOf(code, StringComparison.Ordinal);
        var end = start + code.Length;

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.False(page.IsInsideCode(start - 1));
        Assert.True(page.IsInsideCode(start));
        Assert.True(page.IsInsideCode(source.IndexOf("{{", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(end - 1));
        Assert.False(page.IsInsideCode(end));
    }

    [Fact]
    public void WhenPageHasIndentedCodeBlock_ThenItsLinesAreInsideCodeAndTheTextAroundIsNot()
    {
        // Arrange
        const string source = "before\n\n    {{ a/B }}\n    more\n\nafter";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.False(page.IsInsideCode(source.IndexOf("before", StringComparison.Ordinal) + 5));
        Assert.True(page.IsInsideCode(source.IndexOf("    {{", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.IndexOf("{{", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.IndexOf("more", StringComparison.Ordinal) + 3));
        Assert.False(page.IsInsideCode(source.IndexOf("after", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("```\nfirst {{ a/B }}\n\nlast")]
    [InlineData("text\n\n~~~~markdown\nfirst {{ a/B }}\nlast\n")]
    [InlineData("- item\n\n\t```\n\tfirst {{ a/B }}\n\tlast")]
    [InlineData("text\n\n```markdown {{ a/B }} last")]
    public void WhenFenceIsNotClosed_ThenEverythingUpToItsLastLineIsInsideCode(string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.True(page.IsInsideCode(source.IndexOf("{{", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.IndexOf("last", StringComparison.Ordinal) + 3));
    }

    [Fact]
    public void WhenFenceIsNotClosedInsideAQuote_ThenTheTextAfterTheQuoteIsNotInsideCode()
    {
        // Arrange
        const string source = "> ```\n> first {{ a/B }}\n> last\n\nafter {{ c/D }}";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.True(page.IsInsideCode(source.IndexOf("{{ a/B", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.IndexOf("last", StringComparison.Ordinal) + 3));
        Assert.False(page.IsInsideCode(source.IndexOf("after", StringComparison.Ordinal)));
        Assert.False(page.IsInsideCode(source.IndexOf("{{ c/D", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhenInlineCodeIsReportedAtTheWrongPlace_ThenTheTextAtThatPlaceIsNotInsideCode()
    {
        // Arrange
        // Markdig reports the inline code of this tab-indented line six characters too early, from "del" on.
        const string source = "- item\n\t~~del~~ `{{ a/B }}` end";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.False(page.IsInsideCode(source.IndexOf("del", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("---\ntitle: {{ a/B }}\n---\n\ntext")]
    [InlineData("text {{ a/B }} text")]
    [InlineData("Value <code>{{ a/B }}</code> now")]
    [InlineData("<pre>\n{{ a/B }}\n</pre>")]
    [InlineData("$$\n{{ a/B }}\n$$")]
    public void WhenExpressionIsNotInMarkdownCode_ThenItsOffsetIsNotInsideCode(string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.False(page.IsInsideCode(source.IndexOf("{{", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhenPageHasSeveralCodeRegions_ThenEachOneIsInsideCodeAndTheTextBetweenIsNot()
    {
        // Arrange
        var source = Normalize("""
            One `first` two `second` three

            ```
            third
            ```

            four
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.True(page.IsInsideCode(source.IndexOf("first", StringComparison.Ordinal)));
        Assert.False(page.IsInsideCode(source.IndexOf("two", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.IndexOf("second", StringComparison.Ordinal)));
        Assert.False(page.IsInsideCode(source.IndexOf("three", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.IndexOf("third", StringComparison.Ordinal)));
        Assert.False(page.IsInsideCode(source.IndexOf("four", StringComparison.Ordinal)));
        Assert.False(page.IsInsideCode(-1));
        Assert.False(page.IsInsideCode(source.Length));
    }
}
