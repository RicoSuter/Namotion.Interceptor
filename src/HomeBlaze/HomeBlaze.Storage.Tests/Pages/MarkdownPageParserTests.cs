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
    public void WhenHeadLineHasAsManyCharactersAsTheOpenBlock_ThenItEndsThatBlockAndItsBlockIsBesideIt()
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
        Assert.Equal(
            ExpectedTree("""
                Widget :: Demo.Card [3-4]
                Widget :: Demo.Widget Path=/x [5-7 closed]
                Invalid This line of 2 ':' characters closes no block. No block is open here. [9-9 closed]
                """),
            DescribeTree(page.Blocks, withLines: true));
    }

    [Fact]
    public void WhenHeadLineHasFewerCharactersThanTheOpenBlock_ThenItEndsThatBlockAndItsBlockIsNotInsideIt()
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
        Assert.Equal(
            ExpectedTree("""
                Widget :::: Demo.Card [1-1]
                Invalid 'Footer' is not a type name. A slot block is only allowed directly inside a widget block. [2-4 closed]
                Invalid This line of 4 ':' characters closes no block. No block is open here. [5-5 closed]
                """),
            DescribeTree(page.Blocks, withLines: true));
    }

    public static TheoryData<string, string> PagesWhereAHeadLineEndsABlockAndAClosingLineFitsNoOpenBlock() => new()
    {
        {
            // The closing line after the motor has more characters than its block.
            ":: Demo.Card\n:: Demo.Motor#m\n:::\ntext\n::\nmore page text",
            """
            Widget :: Demo.Card [1-1]
            Widget :: Demo.Motor#m [2-5 closed]
              Invalid This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (2 characters). [3-3 closed]
            """
        },
        {
            ":::: Demo.Card\n::: Demo.Motor\n:::::\ntext\n::::",
            """
            Widget :::: Demo.Card [1-1]
            Widget ::: Demo.Motor [2-5]
              Invalid This line of 5 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (3 characters). [3-3 closed]
              Invalid This line of 4 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (3 characters). [5-5 closed]
            """
        },
        {
            ":: Demo.Card\n:: Demo.Motor\n:::::\ntext\n::",
            """
            Widget :: Demo.Card [1-1]
            Widget :: Demo.Motor [2-5 closed]
              Invalid This line of 5 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (2 characters). [3-3 closed]
            """
        },
        {
            ":: Demo.Flow\n::: Item\n::: Demo.Card\ninside\n::::\ntext\n:::\n::",
            """
            Widget :: Demo.Flow [1-8 closed]
              Slot ::: Item [2-2]
              Widget ::: Demo.Card [3-7 closed]
                Invalid This line of 4 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 1 (2 characters), line 3 (3 characters). [5-5 closed]
            """
        },
        {
            ":: Demo.Card\n:: {.band}\ninside\n:::\ntext\n::",
            """
            Widget :: Demo.Card [1-1]
            Style [2-6 closed]
              Invalid This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (2 characters). [4-4 closed]
            """
        },
        {
            // The blocks inside a block that is an error are not read.
            ":: Demo.Card\n:: Please note this\n:::\ntext\n::",
            """
            Widget :: Demo.Card [1-1]
            Invalid Expected key=value but found 'note this'. [2-5 closed]
            """
        },
        {
            // The last line was written for the card, which the head line of the motor ended.
            ":::: Demo.Card\n:: Demo.Motor\n:::\n::\n::::",
            """
            Widget :::: Demo.Card [1-1]
            Widget :: Demo.Motor [2-4 closed]
              Invalid This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (2 characters). [3-3 closed]
            Invalid This line of 4 ':' characters closes no block. No block is open here. [5-5 closed]
            """
        },
        {
            ":: Demo.Card\n:: Demo.Motor\n> :::\n::\n::",
            """
            Widget :: Demo.Card [1-1]
            Widget :: Demo.Motor [2-4 closed]
              Invalid This line of 3 ':' characters closes no block. A closing line inside a quote or a list item closes no block outside of it. [3-3 closed]
            Invalid This line of 2 ':' characters closes no block. No block is open here. [5-5 closed]
            """
        },
        {
            ":: Demo.Card\n:: Demo.A\n:: Demo.B\n::\n::\n:: Demo.C\n::",
            """
            Widget :: Demo.Card [1-1]
            Widget :: Demo.A [2-2]
            Widget :: Demo.B [3-4 closed]
            Invalid This line of 2 ':' characters closes no block. No block is open here. [5-5 closed]
            Widget :: Demo.C [6-7 closed]
            """
        },
        {
            ":: Demo.Card\n::: Footer\n:: Demo.B\n::\n:::",
            """
            Widget :: Demo.Card [1-2]
              Slot ::: Footer [2-2]
            Widget :: Demo.B [3-4 closed]
            Invalid This line of 3 ':' characters closes no block. No block is open here. [5-5 closed]
            """
        }
    };

    [Theory]
    [MemberData(nameof(PagesWhereAHeadLineEndsABlockAndAClosingLineFitsNoOpenBlock))]
    public void WhenHeadLineEndedABlockAndAClosingLineFitsNoOpenBlock_ThenThatLineClosesNothing(string source, string expectedTree)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(ExpectedTree(expectedTree), DescribeTree(page.Blocks, withLines: true));
    }

    [Theory]
    [InlineData(":: Demo.Card\n::: Demo.Motor\n::::\n:::\n::", 3, "Open here: line 1 (2 characters), line 2 (3 characters).")]
    [InlineData(":: Demo.Card\n:::\n::", 2, "Open here: line 1 (2 characters).")]
    public void WhenClosingLineHasMoreCharactersThanEveryOpenBlock_ThenItIsAnErrorThatNamesTheOpenBlocks(
        string source, int expectedLine, string expectedEnding)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var closingLine = Assert.Single(page.AllBlocks, block => block.Error is not null);
        Assert.Equal(expectedLine, closingLine.Line);
        Assert.StartsWith("This line of ", closingLine.Error);
        Assert.EndsWith(expectedEnding, closingLine.Error);
    }

    [Fact]
    public void WhenTextInsideABlockStartsWithAFenceAndAWord_ThenItEndsTheBlockAndIsAnErrorThatTakesTheClosingLine()
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
        Assert.Equal(
            ExpectedTree("""
                Widget :: Demo.Card [3-3]
                Invalid Expected key=value but found 'that this is text'. [4-6 closed]
                """),
            DescribeTree(page.Blocks, withLines: true));
    }

    public static TheoryData<string, string> PagesThatEndInsideABlock() => new()
    {
        {
            ":: note that this is text\nmore\nafter",
            "Invalid Expected key=value but found 'that this is text'. [1-3]"
        },
        {
            ":: Demo.Card\n::: Demo.Motor\n:::\ntext",
            """
            Widget :: Demo.Card [1-4]
              Widget ::: Demo.Motor [2-3 closed]
            """
        },
        {
            ":: Demo.Card\n> :: Demo.Motor\n> ::\n\ntext",
            """
            Widget :: Demo.Card [1-5]
              Invalid A block is only allowed directly on the page or directly inside another block. [2-3 closed]
            """
        },
        {
            // The second head line ended the card, and the closing line fits neither block.
            "::: Demo.Card\n::: Demo.Motor\n::::\ntext",
            """
            Widget ::: Demo.Card [1-1]
            Widget ::: Demo.Motor [2-4]
              Invalid This line of 4 ':' characters closes no block. A closing line needs as many characters as the head line of its block. Open here: line 2 (3 characters). [3-3 closed]
            """
        }
    };

    [Theory]
    [MemberData(nameof(PagesThatEndInsideABlock))]
    public void WhenPageEndsInsideABlock_ThenTheBlockEndsThereWithoutAnErrorForIt(string source, string expectedTree)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(ExpectedTree(expectedTree), DescribeTree(page.Blocks, withLines: true));
    }

    [Fact]
    public void WhenHeadLineOfAStyleBlockHasAsManyCharactersAsTheOpenBlock_ThenItEndsThatBlockAndIsAStyleBlockBesideIt()
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
        Assert.Equal(
            ExpectedTree("""
                Widget :: Demo.Card [1-1]
                Style [2-4 closed]
                Invalid This line of 2 ':' characters closes no block. No block is open here. [5-5 closed]
                """),
            DescribeTree(page.Blocks, withLines: true));
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
    public void WhenClosingLineClosesOuterBlockWhileInnerIsOpen_ThenItEndsTheInnerBlockAsWell()
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
        Assert.Equal(
            ExpectedTree("""
                Widget :: Demo.Card [1-5 closed]
                  Widget ::: Demo.Widget Path=/x [3-4]
                """),
            DescribeTree(page.Blocks, withLines: true));
    }

    [Fact]
    public void WhenBlockWithAnErrorHasNoClosingLine_ThenTheErrorIsTheOneOfItsHeadLineOnly()
    {
        // Arrange
        const string source = "before\n\n:: Demo.Card Title=\"open\ntext";

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Equal("The value of 'Title' has no closing quote.", block.Error);
        Assert.Equal((3, 4, false), (block.Line, block.EndLine, block.HasClosingLine));
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
    public void WhenCodeFenceInsideABlockIsNotClosed_ThenTheBlockRunsToTheEndOfThePageWithEveryLineAsCode()
    {
        // Arrange
        const string source = "before\n\n:: Demo.Card\n```\ncode\n::\n:: Demo.Motor\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Null(card.Error);
        Assert.Equal((3, 8, false), (card.Line, card.EndLine, card.HasClosingLine));
        Assert.True(page.IsInsideCode(source.IndexOf(":: Demo.Motor", StringComparison.Ordinal)));
        Assert.True(page.IsInsideCode(source.Length - 1));
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
    public void WhenClosingLineFollowsHtmlWithoutAnEmptyLineAndFitsNoOpenBlock_ThenItStaysText()
    {
        // Arrange
        const string source = ":: Demo.Card\n<div>\n:::\n</div>\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, 5), (card.Line, card.EndLine));
        Assert.Contains("<div>\n:::\n</div>", PageErrorBoxes.RenderSyntaxOnly(page));
    }

    [Fact]
    public void WhenBlockIsWrittenInsideHtmlWithoutEmptyLinesInsideABlock_ThenItsHeadLineEndsTheHtmlAndItIsABlock()
    {
        // Arrange
        const string source = ":: Demo.Card\n<div>\n::: Demo.Widget Path=/x\n:::\n</div>\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(
            ExpectedTree("""
                Widget :: Demo.Card [1-6 closed]
                  Widget ::: Demo.Widget Path=/x [3-4 closed]
                """),
            DescribeTree(page.Blocks, withLines: true));

        var card = Assert.IsType<BlockMarkdownSegment>(MarkdownSegmentBuilder.Build(page)[0]);
        Assert.Equal("<div>\n", Assert.IsType<HtmlMarkdownSegment>(card.Content[0]).Html);
        Assert.IsType<BlockMarkdownSegment>(card.Content[1]);
        Assert.Equal("</div>\n", Assert.IsType<HtmlMarkdownSegment>(card.Content[2]).Html);
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
        Assert.Equal("A block is only allowed directly on the page or directly inside another block.", widget.Error);
        Assert.Equal((3, 4, false), (widget.Line, widget.EndLine, widget.HasClosingLine));
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

    [Theory]
    [InlineData(":: A.B\nTerm\n:   Definition\n:: C.D\ninside", "Widget :: A.B [1-3]\nWidget :: C.D [4-5]\n")]
    [InlineData(":: A.B\nTerm\n:   Definition\n\n:: C.D\ninside", "Widget :: A.B [1-3]\nWidget :: C.D [5-6]\n")]
    [InlineData(":: A.B\nTerm\n:   Definition\n   :: C.D\ninside", "Widget :: A.B [1-3]\nWidget :: C.D [4-5]\n")]
    [InlineData(":: A.B\nTerm\n:   Definition\n\n   :: C.D\ninside", "Widget :: A.B [1-3]\nWidget :: C.D [5-6]\n")]
    [InlineData(":: A.B\nTerm\n:   One\n:   Two\n::: C.D\ninside", "Widget :: A.B [1-6]\n  Widget ::: C.D [5-6]\n")]
    [InlineData(":: A.B\n::: Tab\nTerm\n:   Definition\n   ::: Tab\ninside", "Widget :: A.B [1-6]\n  Slot ::: Tab [2-4]\n  Slot ::: Tab [5-6]\n")]
    public void WhenHeadLineFollowsADefinitionListInsideABlock_ThenItEndsTheBlocksWithAsManyCharactersOrMore(
        string source, string expectedTree)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(expectedTree, DescribeTree(page.Blocks, withLines: true));
        Assert.Contains("<dd>", PageErrorBoxes.RenderSyntaxOnly(page));
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
        Assert.Equal("A block is only allowed directly on the page or directly inside another block.", outer.Children[0].Error);
        Assert.Equal(5, outer.Children[1].Line);
        Assert.Contains("closes no block. A closing line inside a quote or a list item closes no block outside of it.", outer.Children[1].Error);
    }

    [Fact]
    public void WhenProseInsideABlockStartsWithAFenceAndAWord_ThenTheErrorIsTheOneOfTheHeadGrammarAndTheClosingLineOfTheBlockClosesNothing()
    {
        // Arrange
        const string source = ":: Demo.Card\n:: Please note this\ntext\n::\nafter\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(
            ExpectedTree("""
                Widget :: Demo.Card [1-1]
                Invalid Expected key=value but found 'note this'. [2-4 closed]
                Invalid This line of 2 ':' characters closes no block. No block is open here. [6-6 closed]
                """),
            DescribeTree(page.Blocks, withLines: true));
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

    private const string LayoutWithEveryClosingLine = """
        :: HomeBlaze.Components.TabLayout
        ::: Tab#pumps Title="Pumps" Icon=WaterDrop
        :::: HomeBlaze.Components.FlowLayout MinWidth=320
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
        ::::::: HomeBlaze.Components.Widget Path=../WaterPump
        :::::::
        ::::::
        :::::
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
        ::::::: HomeBlaze.Components.Widget Path=../Compressor
        :::::::
        ::::::
        :::::
        ::::
        :::
        ::: Tab#belt Title="Conveyor Belt" Icon=Settings
        The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
        :::
        ::
        """;

    private const string LayoutInTheShortForm = """
        :: HomeBlaze.Components.TabLayout
        ::: Tab#pumps Title="Pumps" Icon=WaterDrop
        :::: HomeBlaze.Components.FlowLayout MinWidth=320
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
        ::::::: HomeBlaze.Components.Widget Path=../WaterPump
        ::::: Item
        :::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
        ::::::: HomeBlaze.Components.Widget Path=../Compressor
        ::: Tab#belt Title="Conveyor Belt" Icon=Settings
        The conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.
        """;

    /// <summary>
    /// Gets the tree of the blocks, one block per line: its kind and its head line or its error. With
    /// <paramref name="withLines"/> also its first and its last line, and "closed" when the last one is its closing line.
    /// </summary>
    private static string DescribeTree(IEnumerable<MarkdownBlock> blocks, bool withLines = false, int depth = 0) =>
        string.Concat(blocks.Select(block =>
        {
            var lines = withLines ? $"[{block.Line}-{block.EndLine}{(block.HasClosingLine ? " closed" : string.Empty)}]" : null;
            string?[] parts = [block.Kind.ToString(), block.Head?.Format() ?? block.Error, lines];
            return $"{new string(' ', depth * 2)}{string.Join(' ', parts.Where(part => part is not null))}\n" +
                   DescribeTree(block.Children, withLines, depth + 1);
        }));

    /// <summary>Gets the lines of every block in document order: its head line, its last line and whether that is a closing line.</summary>
    private static (int Line, int EndLine, bool HasClosingLine)[] DescribeLines(MarkdownPage page) =>
        page.AllBlocks.Select(block => (block.Line, block.EndLine, block.HasClosingLine)).ToArray();

    private static string ExpectedTree(string tree) => tree.ReplaceLineEndings("\n") + "\n";

    [Fact]
    public void WhenLayoutIsWrittenInTheShortForm_ThenItHasTheTreeOfTheSamePageWithEveryClosingLine()
    {
        // Arrange
        var withEveryClosingLine = MarkdownPageParser.Parse(Normalize(LayoutWithEveryClosingLine));

        // Act
        var shortForm = MarkdownPageParser.Parse(Normalize(LayoutInTheShortForm));

        // Assert
        Assert.Equal(DescribeTree(withEveryClosingLine.Blocks), DescribeTree(shortForm.Blocks));
        Assert.Equal(
            """
            Widget :: HomeBlaze.Components.TabLayout
              Slot ::: Tab#pumps Title="Pumps" Icon=WaterDrop
                Widget :::: HomeBlaze.Components.FlowLayout MinWidth=320
                  Slot ::::: Item
                    Widget :::::: HomeBlaze.Components.Card Title="Water Pump" Icon=WaterDrop
                      Widget ::::::: HomeBlaze.Components.Widget Path=../WaterPump
                  Slot ::::: Item
                    Widget :::::: HomeBlaze.Components.Card Title="Compressor" Icon=Bolt
                      Widget ::::::: HomeBlaze.Components.Widget Path=../Compressor
              Slot ::: Tab#belt Title="Conveyor Belt" Icon=Settings

            """.ReplaceLineEndings("\n"),
            DescribeTree(shortForm.Blocks));

        Assert.All(withEveryClosingLine.AllBlocks, block => Assert.True(block.HasClosingLine));
        Assert.Equal(
            [
                (1, 11, false), (2, 9, false), (3, 9, false), (4, 6, false), (5, 6, false), (6, 6, false),
                (7, 9, false), (8, 9, false), (9, 9, false), (10, 11, false)
            ],
            DescribeLines(shortForm));
    }

    [Fact]
    public void WhenTwoEmptyBlocksFollowEachOtherWithoutClosingLines_ThenBothAreBlocksOfOneLine()
    {
        // Arrange
        const string source = ":: Demo.Card\n:: Demo.Motor";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal("Widget :: Demo.Card\nWidget :: Demo.Motor\n", DescribeTree(page.Blocks));
        Assert.Equal([(1, 1, false), (2, 2, false)], DescribeLines(page));
    }

    [Theory]
    [InlineData("before\n\n::: Demo.Card\ntext", 4)]
    [InlineData("before\n\n::: Demo.Card\ntext\n", 4)]
    [InlineData("before\n\n::: Demo.Card\ntext\n\n\n", 4)]
    [InlineData("before\n\n::: Demo.Card\none\n\ntwo", 6)]
    [InlineData("before\n\n::: Demo.Card", 3)]
    [InlineData("before\n\n::: Demo.Card\n\n", 3)]
    public void WhenBlockIsEndedByTheEndOfThePage_ThenItIsABlockThatEndsWithItsLastLineOfContent(string source, int expectedEndLine)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Null(card.Error);
        Assert.Equal((3, expectedEndLine, false), (card.Line, card.EndLine, card.HasClosingLine));
    }

    [Fact]
    public void WhenBlocksAtEveryDepthHaveNoClosingLine_ThenAHeadLineEndsTheBlocksWithAsManyCharactersOrMore()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Tabs
            ::: Tab
            one
            ::: Tab
            :::: Demo.Flow
            ::::: Item
            two
            ::::: Item
            three
            :::: Demo.Flow
            ::: Tab
            :: Demo.Card
            four
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(
            """
            Widget :: Demo.Tabs
              Slot ::: Tab
              Slot ::: Tab
                Widget :::: Demo.Flow
                  Slot ::::: Item
                  Slot ::::: Item
                Widget :::: Demo.Flow
              Slot ::: Tab
            Widget :: Demo.Card

            """.ReplaceLineEndings("\n"),
            DescribeTree(page.Blocks));
        Assert.Equal(
            [
                (1, 11, false), (2, 3, false), (4, 10, false), (5, 9, false), (6, 7, false), (8, 9, false),
                (10, 10, false), (11, 11, false), (12, 13, false)
            ],
            DescribeLines(page));
    }

    [Fact]
    public void WhenHeadLineHasFewerCharactersThanThreeOpenBlocks_ThenItEndsAllOfThemAtOnce()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Tabs
            ::: Tab
            :::: Demo.Card
            ::::: Demo.Widget Path=/x

            ::: Tab
            after
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(
            """
            Widget :: Demo.Tabs
              Slot ::: Tab
                Widget :::: Demo.Card
                  Widget ::::: Demo.Widget Path=/x
              Slot ::: Tab

            """.ReplaceLineEndings("\n"),
            DescribeTree(page.Blocks));
        Assert.Equal([(1, 7, false), (2, 4, false), (3, 4, false), (4, 4, false), (6, 7, false)], DescribeLines(page));
    }

    [Fact]
    public void WhenSomeBlocksHaveAClosingLineAndOthersHaveNone_ThenEachClosingLineEndsItsBlockAndEverythingOpenInsideIt()
    {
        // Arrange
        var source = Normalize("""
            :: Demo.Card
            ::: Footer
            :::: Demo.Widget Path=/x
            :::
            body
            ::
            page text
            :: Demo.Flow
            ::: Item
            :::: Demo.Card
            inside
            ::::
            ::: Item
            last
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(
            """
            Widget :: Demo.Card
              Slot ::: Footer
                Widget :::: Demo.Widget Path=/x
            Widget :: Demo.Flow
              Slot ::: Item
                Widget :::: Demo.Card
              Slot ::: Item

            """.ReplaceLineEndings("\n"),
            DescribeTree(page.Blocks));
        Assert.Equal(
            [(1, 6, true), (2, 4, true), (3, 3, false), (8, 14, false), (9, 12, false), (10, 12, true), (13, 14, false)],
            DescribeLines(page));
    }

    [Fact]
    public void WhenTextFollowsABlockWithoutClosingLine_ThenItIsContentOfThatBlock()
    {
        // Arrange
        const string source = ":: Demo.Card\nbefore\n::: Demo.Widget Path=/x\nafter\n::\nrest";

        // Act
        var segments = MarkdownSegmentBuilder.Build(MarkdownPageParser.Parse(source));

        // Assert
        var card = Assert.IsType<BlockMarkdownSegment>(segments[0]);
        Assert.Equal((1, 5, true), (card.Block.Line, card.Block.EndLine, card.Block.HasClosingLine));
        Assert.Equal("<p>before</p>\n", Assert.IsType<HtmlMarkdownSegment>(card.Content[0]).Html);
        var widget = Assert.IsType<BlockMarkdownSegment>(card.Content[1]);
        Assert.Equal(MarkdownBlockKind.Widget, widget.Block.Kind);
        Assert.Equal((3, 4, false), (widget.Block.Line, widget.Block.EndLine, widget.Block.HasClosingLine));
        Assert.Equal("<p>after</p>\n", Assert.IsType<HtmlMarkdownSegment>(Assert.Single(widget.Content)).Html);
        Assert.Equal(2, card.Content.Count);
        Assert.Equal("\n<p>rest</p>\n", Assert.IsType<HtmlMarkdownSegment>(segments[1]).Html);
    }

    [Fact]
    public void WhenClosingLineFitsNoOpenBlockAfterAHeadLineEndedBlocks_ThenItIsStillTheErrorOfALineThatClosesNothing()
    {
        // Arrange
        const string source = ":: Demo.Card\n::: Demo.Widget Path=/x\n:: Demo.Card\ntext\n:::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(2, page.Blocks.Count);
        Assert.Equal([(1, 2, false), (2, 2, false), (3, 5, false), (5, 5, true)], DescribeLines(page));
        var closingLine = Assert.Single(page.Blocks[1].Children);
        Assert.Equal(MarkdownBlockKind.Invalid, closingLine.Kind);
        Assert.Equal(
            "This line of 3 ':' characters closes no block. A closing line needs as many characters as the head line " +
            "of its block. Open here: line 3 (2 characters).",
            closingLine.Error);
        Assert.All(page.AllBlocks.Take(3), block => Assert.Equal(MarkdownBlockKind.Widget, block.Kind));
    }

    [Theory]
    [InlineData(":: Demo.Card\nbefore\n> :: Demo.Motor\n> inside\n\nafter\n::\nrest", 3, 4)]
    [InlineData(":: Demo.Card\nbefore\n> ::: Demo.Motor\n> inside\n> :: Demo.Motor\n\nafter\n::\nrest", 5, 5)]
    [InlineData(":: Demo.Card\n- item\n  :: Demo.Motor\n  inside\n\nafter\n::\nrest", 3, 4)]
    [InlineData(":: Demo.Card\n1. item\n\n   :: Demo.Motor\n\nafter\n::\nrest", 4, 4)]
    public void WhenHeadLineIsInsideAQuoteOrAListItemInsideABlock_ThenItEndsNoBlockOutsideAndStaysAnError(
        string source, int expectedLine, int expectedEndLine)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, card.Kind);
        Assert.Equal((1, source.Split('\n').Length - 1, true), (card.Line, card.EndLine, card.HasClosingLine));
        var inner = card.Children[^1];
        Assert.Equal(MarkdownBlockKind.Invalid, inner.Kind);
        Assert.Equal("A block is only allowed directly on the page or directly inside another block.", inner.Error);
        Assert.Equal((expectedLine, expectedEndLine, false), (inner.Line, inner.EndLine, inner.HasClosingLine));
        Assert.Contains("<p>after</p>", PageErrorBoxes.RenderSyntaxOnly(page));
    }

    [Fact]
    public void WhenHeadLineInsideAQuoteHasNoMoreCharactersThanABlockOfTheQuote_ThenItEndsThatBlockOnly()
    {
        // Arrange
        const string source = ":: Demo.Card\n> ::: {.band}\n> text\n> :: {.other}\n> more\n\nafter\n::";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.Blocks);
        Assert.Equal((1, 8, true), (card.Line, card.EndLine, card.HasClosingLine));
        Assert.Equal(2, card.Children.Count);
        Assert.All(card.Children, style => Assert.Equal(MarkdownBlockKind.Style, style.Kind));
        Assert.Equal((2, 3, false), (card.Children[0].Line, card.Children[0].EndLine, card.Children[0].HasClosingLine));
        Assert.Equal((4, 5, false), (card.Children[1].Line, card.Children[1].EndLine, card.Children[1].HasClosingLine));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~~")]
    public void WhenFenceLinesAreInsideFencedCodeInsideABlockWithoutClosingLine_ThenTheyStayTextAndTheBlockContinues(string codeFence)
    {
        // Arrange
        var source = $":: Demo.Card\nbefore\n\n{codeFence}\n:: Demo.Motor\n::\n:::\n{codeFence}\n\nafter\n:: Demo.Motor";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal("Widget :: Demo.Card\nWidget :: Demo.Motor\n", DescribeTree(page.Blocks));
        Assert.Equal([(1, 10, false), (11, 11, false)], DescribeLines(page));
        Assert.True(page.IsInsideCode(source.IndexOf(":: Demo.Motor", StringComparison.Ordinal)));

        var card = Assert.IsType<BlockMarkdownSegment>(MarkdownSegmentBuilder.Build(page)[0]);
        var content = Assert.IsType<HtmlMarkdownSegment>(Assert.Single(card.Content)).Html;
        Assert.Contains(":: Demo.Motor\n::\n:::\n</code></pre>", content);
        Assert.EndsWith("<p>after</p>\n", content);
    }

    [Fact]
    public void WhenFenceLinesAreInsideIndentedCodeInsideABlockWithoutClosingLine_ThenTheyStayTextAndTheBlockContinues()
    {
        // Arrange
        const string source = ":: Demo.Card\nbefore\n\n    :: Demo.Motor\n    ::\n\nafter\n:: Demo.Motor";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal("Widget :: Demo.Card\nWidget :: Demo.Motor\n", DescribeTree(page.Blocks));
        Assert.Equal([(1, 7, false), (8, 8, false)], DescribeLines(page));
    }

    [Theory]
    [InlineData("<div class=\"row\">\n<span>a</span>")]
    [InlineData("</div>")]
    [InlineData("<custom-element>\ntext")]
    public void WhenHeadLineFollowsHtmlThatEndsAtAnEmptyLineInsideABlock_ThenItEndsTheHtmlAndIsReadAsAHeadLine(string html)
    {
        // Arrange
        var source = $":: Demo.Card\n{html}\n::: Demo.Widget Path=/x\n:: Demo.Motor\n{html}\n:: Demo.Motor";
        var htmlLineCount = html.Split('\n').Length;

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(
            "Widget :: Demo.Card\n  Widget ::: Demo.Widget Path=/x\nWidget :: Demo.Motor\nWidget :: Demo.Motor\n",
            DescribeTree(page.Blocks));
        Assert.Equal(
            [
                (1, 2 + htmlLineCount, false),
                (2 + htmlLineCount, 2 + htmlLineCount, false),
                (3 + htmlLineCount, 3 + 2 * htmlLineCount, false),
                (4 + 2 * htmlLineCount, 4 + 2 * htmlLineCount, false)
            ],
            DescribeLines(page));
    }

    [Theory]
    [InlineData("<style>\n::selection { color: red }\n</style>", "::selection { color: red }")]
    [InlineData("<script>\n:: Demo.Motor\n</script>", ":: Demo.Motor")]
    [InlineData("<pre>\n:: Demo.Motor\n</pre>", ":: Demo.Motor")]
    [InlineData("<!--\n:: Demo.Motor\n-->", ":: Demo.Motor")]
    [InlineData("<?php\n:: Demo.Motor\n?>", ":: Demo.Motor")]
    public void WhenHeadLineIsInsideHtmlThatEndsAtItsOwnEndInsideABlock_ThenItStaysPartOfTheHtml(string html, string line)
    {
        // Arrange
        var source = $":: Demo.Card\n{html}\nafter\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = Assert.Single(page.AllBlocks);
        Assert.Equal((MarkdownBlockKind.Widget, 1, 6, true), (card.Kind, card.Line, card.EndLine, card.HasClosingLine));
        var segment = Assert.IsType<BlockMarkdownSegment>(MarkdownSegmentBuilder.Build(page)[0]);
        Assert.Equal($"{html}\n<p>after</p>\n", Assert.IsType<HtmlMarkdownSegment>(Assert.Single(segment.Content)).Html);
        Assert.Contains(line, html);
    }

    [Theory]
    [InlineData("<div>\n:: Demo.Card\n</div>")]
    [InlineData("<custom-element>\n:: Demo.Card\ntext")]
    public void WhenHeadLineFollowsHtmlOutsideOfAnyBlock_ThenItStaysPartOfTheHtml(string source)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Empty(page.Blocks);
        Assert.Equal($"{source}\n", PageErrorBoxes.RenderSyntaxOnly(page));
    }

    [Fact]
    public void WhenHeadLineFollowsHtmlInsideAQuoteInsideABlock_ThenItEndsTheBlockAroundTheQuote()
    {
        // Arrange
        const string source = ":: Demo.Card\n> <div>\n> text\n:: Demo.Motor";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal("Widget :: Demo.Card\nWidget :: Demo.Motor\n", DescribeTree(page.Blocks));
        Assert.Equal([(1, 3, false), (4, 4, false)], DescribeLines(page));
    }

    [Theory]
    [InlineData(":: Demo.Card\ntext\n\n:: Demo.Motor", "text")]
    [InlineData(":: Demo.Card\none\ntwo\n:: Demo.Motor", "two")]
    [InlineData(":: Demo.Card\n::: Footer\ninside\n\n\n:: Demo.Motor", "inside")]
    [InlineData(":: Demo.Card\n::: Demo.Widget Path=/x\n:: Demo.Motor", "Path=/x")]
    [InlineData(":: Demo.Card\n- one\n- two\n", "- two")]
    [InlineData(":: Demo.Card\n```\ncode\n", "code")]
    [InlineData(":: Demo.Card\ntext\n> ::\n", "> ::")]
    public void WhenBlockHasNoClosingLine_ThenItsSpanEndsWithItsLastLineOfContent(string source, string expectedEnd)
    {
        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var card = page.Document.Descendants<MarkdownBlockContainer>().First();
        Assert.Equal(0, card.Span.Start);
        Assert.Equal(source.IndexOf(expectedEnd, StringComparison.Ordinal) + expectedEnd.Length - 1, card.Span.End);
    }

    [Fact]
    public void WhenBlockHasAClosingLine_ThenItsSpanEndsWithThatLineAndTheSpanOfAnOpenBlockInsideItEndsBefore()
    {
        // Arrange
        const string source = ":: Demo.Card\n::: Demo.Widget Path=/x\ninside\n\n::\nrest";

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var containers = page.Document.Descendants<MarkdownBlockContainer>().ToList();
        Assert.Equal(source.IndexOf("\nrest", StringComparison.Ordinal) - 1, containers[0].Span.End);
        Assert.Equal(source.IndexOf("inside", StringComparison.Ordinal) + "inside".Length - 1, containers[1].Span.End);
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
