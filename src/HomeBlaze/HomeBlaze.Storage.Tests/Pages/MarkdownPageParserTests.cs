using HomeBlaze.Storage.Pages;

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

            :::::Demo.TabLayout#tabs
            ::::Tab title="Heizung {x}"
            Body

            :::Demo.Widget path="/House/{Heizung}"
            :::
            ::::
            ::::Tab title="Strom"
            ::::
            :::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var tabs = Assert.Single(page.Blocks);
        Assert.Equal(MarkdownBlockKind.Widget, tabs.Kind);
        Assert.Equal(7, tabs.Line);
        Assert.Equal("tabs", tabs.Head!.Id);
        Assert.Equal(2, tabs.Children.Count);
        Assert.All(tabs.Children, tab => Assert.Equal(MarkdownBlockKind.Slot, tab.Kind));
        var widget = Assert.Single(tabs.Children[0].Children);
        Assert.Equal("/House/{Heizung}", widget.Head!.Arguments[0].Value);
        Assert.Equal([0, 1, 2, 3], page.AllBlocks.Select(block => block.Index));
    }

    [Fact]
    public void WhenBlockHasHeadLine_ThenHeadSpanCoversExactlyThatLine()
    {
        // Arrange
        var source = Normalize("""
            before

            :::Demo.Motor#m speed=3000
            :::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal(":::Demo.Motor#m speed=3000", source.Substring(block.HeadSpan.Start, block.HeadSpan.Length));
    }

    [Fact]
    public void WhenFirstChildIsJsonFence_ThenItIsTheConfiguration()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Grid rows=1
            ```json
            { "cells": [1, 2] }
            ```
            :::
            """);

        // Act
        var block = MarkdownPageParser.Parse(source).Blocks[0];

        // Assert
        Assert.Equal("{ \"cells\": [1, 2] }", block.ConfigurationJson);
        var span = block.ConfigurationSpan!.Value;
        Assert.StartsWith("```json", source.Substring(span.Start, span.Length));
        Assert.EndsWith("```", source.Substring(span.Start, span.Length));
    }

    [Fact]
    public void WhenBlockIsInsideListItem_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            - item
              :::Demo.Card
              text
              :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("only allowed directly on the page", block.Error);
    }

    [Fact]
    public void WhenUnnamedBlockHasAttributes_ThenItIsAStyleBlock()
    {
        // Arrange
        var source = Normalize("""
            ::: {.band}
            text
            :::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(MarkdownBlockKind.Style, Assert.Single(page.Blocks).Kind);
        Assert.Empty(page.AllBlocks);
    }

    [Fact]
    public void WhenWidgetIsInsideStyleBlock_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :::: {.band}
            :::Demo.Card
            :::
            ::::
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, Assert.Single(page.AllBlocks).Kind);
    }

    [Fact]
    public void WhenInnerFenceIsAsLongAsOuter_ThenAnUnnamedBlockIsReported()
    {
        // Arrange
        var source = Normalize("""
            :::Demo.Card
            :::Demo.Widget path=/x
            :::
            after
            :::
            rest
            """);

        // Act
        var page = MarkdownPageParser.Parse(source);

        // Assert
        var unnamed = page.AllBlocks.Single(block => block.Kind == MarkdownBlockKind.Invalid);
        Assert.Contains("outer block must be longer", unnamed.Error);
    }

    [Fact]
    public void WhenSlotBlockIsAtPageLevel_ThenItIsInvalid()
    {
        // Arrange
        var source = Normalize("""
            :::Footer
            text
            :::
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
            :::Demo.Card title="open
            :::
            """);

        // Act
        var block = Assert.Single(MarkdownPageParser.Parse(source).AllBlocks);

        // Assert
        Assert.Equal(MarkdownBlockKind.Invalid, block.Kind);
        Assert.Contains("closing quote", block.Error);
    }

    [Theory]
    [InlineData("before\n\n```\n{{ a/B }}\n```\n\nafter", "```\n{{ a/B }}\n```")]
    [InlineData("before\n\n~~~~markdown\n{{ a/B }}\n~~~~\n\nafter", "~~~~markdown\n{{ a/B }}\n~~~~")]
    [InlineData("before `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("# Heading `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("- item\n- before `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("> quote\n> before `{{ a/B }}` after", "`{{ a/B }}`")]
    [InlineData("| A | B |\n|---|---|\n| x | before `{{ a/B }}` after |", "`{{ a/B }}`")]
    [InlineData(":::Demo.Card\nbefore `{{ a/B }}` after\n:::", "`{{ a/B }}`")]
    [InlineData(":::Demo.Card\ntext\n\n```\n{{ a/B }}\n```\n:::", "```\n{{ a/B }}\n```")]
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
